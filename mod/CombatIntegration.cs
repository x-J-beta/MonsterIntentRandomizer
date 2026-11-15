using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace MonsterIntentRandomizer;

[HarmonyPatch]
internal static class CombatIntegration
{
    private static readonly ConditionalWeakTable<ICombatState, EncounterRules> Encounters = new();
    private static readonly AccessTools.FieldRef<MonsterModel, MoveState> NextMove =
        AccessTools.FieldRefAccess<MonsterModel, MoveState>("<NextMove>k__BackingField");

    private sealed class EncounterRules(RuleConfiguration rules)
    {
        public RuleConfiguration Rules { get; } = rules;
        public ActionPlanner Planner { get; } = new(rules.PoolSettings);
        public ActionCatalog Catalog { get; } = new(rules.Actions);
        public Dictionary<Creature, EnemyRules> Enemies { get; } = new();
        public Queue<Dictionary<Creature, double>> RecentDamage { get; } = new();
        public Dictionary<Creature, double> PlayerDamage { get; } = new();
        public HashSet<PowerModel> OneTurnDebuffs { get; } = new();
        public Creature? SpecialDebuffOwner { get; set; }
        public int SpecialDebuffRound { get; set; }
        public bool SpecialDebuffSpent { get; set; }
        public bool Ready { get; set; }
        public double DamagePrior { get; init; }
    }

    private sealed class EnemyRules(MonsterRules rules, MonsterProfile profile)
    {
        public MonsterRules Rules { get; } = rules;
        public MonsterProfile Profile { get; } = profile;
        public Pools Balance { get; set; }
        public ActionHistory History { get; set; } = new();
        public int PlannedRound { get; set; }
        public int SettledRound { get; set; }
        public MoveState? RandomMove { get; set; }
        public ActionPlan? Plan { get; set; }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    private static void StartFight(ICombatState? combatState, ref Task __result)
    {
        if (combatState == null || !ModSettings.Current.Enabled ||
            RunManager.Instance.NetService.Type != NetGameType.Singleplayer || combatState.Players.Count != 1)
            return;

        var rules = ModSettings.Rules;
        __result = SetUpAfterOriginal(__result, combatState, rules);
    }

    private static async Task SetUpAfterOriginal(Task original, ICombatState combat, RuleConfiguration rules)
    {
        await original;
        if (!IsCurrentCombat(combat) || Encounters.TryGetValue(combat, out _))
            return;

        try
        {
            var encounter = new EncounterRules(rules) { DamagePrior = StageDamage(combat, rules) };
            Encounters.Add(combat, encounter);
            var candidates = new Dictionary<string, IReadOnlyList<OpeningOption>>();
            var actionValues = new Dictionary<string, double>();
            var byId = new Dictionary<string, Creature>();
            foreach (var creature in combat.Enemies)
            {
                string id = creature.CombatId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ??
                    $"enemy-{byId.Count}";
                byId.Add(id, creature);
                if (creature.Monster is { } monster && FindProfile(rules, monster) is { } configured)
                    actionValues[id] = configured.Profile.ActionValue;
                var state = Register(encounter, creature);
                if (state == null)
                    continue;

                candidates[id] = state.Rules.OpeningStates
                    .Where(key => (key != "STUN" || !state.Rules.FixedOpening) && rules.OpeningValues[key].MinAct <= Act(combat))
                    .Select(key => OpeningValuation.Option(key, rules.OpeningValues[key], state.Profile))
                    .Where(option => NativeActions.CanApplyOpening(creature, option, combat.Enemies))
                    .ToArray();
            }

            var draws = OpeningValuation.Draw(candidates, actionValues, rules.Draws, RandomFor(combat, 0, 0));
            // A newly added Artifact should not consume a simultaneously drawn penalty.
            foreach (var pick in draws.OrderBy(pick => pick.State.Id == "ARTIFACT"))
            {
                if (!IsCurrentCombat(combat))
                    return;
                Creature target = byId[pick.MonsterId];
                int applied = await NativeActions.ApplyOpening(target, pick.State);
                var state = encounter.Enemies[target];
                var price = rules.OpeningValues[pick.State.Id];
                state.Balance = state.Balance with
                {
                    ActionValue = state.Balance.ActionValue - OpeningValuation.Evaluate(price, state.Profile, applied)
                };
            }
            encounter.Ready = true;
        }
        catch (Exception error)
        {
            Log.Warn($"[MonsterIntentRandomizer] Combat setup failed: {error}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.RollMove))]
    private static void PlanMove(MonsterModel __instance)
    {
        Creature creature = __instance.Creature;
        if (creature.CombatState is not { } combat || !Encounters.TryGetValue(combat, out var encounter) ||
            !encounter.Ready || !creature.IsAlive || __instance.NextMove is not { } original)
            return;

        try
        {
            var enemy = Register(encounter, creature);
            if (enemy == null || !original.CanTransitionAway || creature.IsStunned ||
                KeepsNativeMove(enemy, original, combat.RoundNumber) || !IsOrdinaryMove(original))
                return;
            int round = combat.RoundNumber;
            if (enemy.SettledRound >= round)
                return;
            if (enemy.PlannedRound != round)
            {
                var context = Snapshot(creature, combat, encounter);
                enemy.Plan = encounter.Planner.Plan(enemy.Balance, enemy.Profile,
                    encounter.Catalog.Generate(enemy.Profile, context, enemy.History, round),
                    RandomFor(combat, creature.CombatId ?? 0, round));
                enemy.RandomMove = enemy.Plan.Action is { } action ? NativeActions.Build(__instance, action) : null;
                enemy.PlannedRound = round;
                if (enemy.Plan.Action?.Effects?.Any(effect => effect.IsSpecialDebuff) == true)
                {
                    encounter.SpecialDebuffOwner = creature;
                    encounter.SpecialDebuffRound = round;
                }
            }

            // The original state machine keeps its branch and stun history. Only the advertised/executed move changes.
            if (enemy.RandomMove is { } replacement)
                NextMove(__instance) = replacement;
        }
        catch (Exception error)
        {
            Log.Warn($"[MonsterIntentRandomizer] Keeping {creature.ModelId.Entry}'s original intent: {error}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.MonsterPerformedMove))]
    private static void SettleMove(ICombatState __0, MonsterModel __1, MoveState __2)
    {
        if (!Encounters.TryGetValue(__0, out var encounter) || !encounter.Enemies.TryGetValue(__1.Creature, out var state) ||
            state.SettledRound >= __0.RoundNumber)
            return;

        state.SettledRound = __0.RoundNumber;
        if (state.PlannedRound == __0.RoundNumber && ReferenceEquals(state.RandomMove, __2) &&
            state.Plan is { Action: { } action } plan)
        {
            state.History = state.History.Record(action, __0.RoundNumber);
            state.Balance = plan.After;
        }
        else if (!IsOrdinaryMove(__2) || KeepsNativeMove(state, __2, __0.RoundNumber))
        {
            state.Balance = encounter.Planner.Plan(state.Balance, state.Profile, [], new Random(0), canRandomize: false).After;
        }
        else
        {
            int hits = __2.Intents.OfType<AttackIntent>().Sum(intent => intent.Repeats);
            double value = hits == 0 ? state.Profile.ActionValue * state.Profile.NonAttackMultiplier :
                __2.Intents.OfType<AttackIntent>().Sum(intent => (double)(intent.DamageCalc?.Invoke() ?? 0) * intent.Repeats);
            var granted = state.PlannedRound == __0.RoundNumber && state.Plan is { } fallback ? fallback.Granted :
                encounter.Planner.Plan(state.Balance, state.Profile, [], RandomFor(__0, __1.Creature.CombatId ?? 0, __0.RoundNumber)).Granted;
            state.Balance = encounter.Planner.Settle(granted, value, hits);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.DamageReceived))]
    private static void RecordDamage(ICombatState __0, Creature __1, Creature? __2, DamageResult __3)
    {
        if (__0.CurrentSide == CombatSide.Player && __1.IsEnemy && __2?.Side == CombatSide.Player &&
            Encounters.TryGetValue(__0, out var encounter))
        {
            encounter.PlayerDamage[__1] = encounter.PlayerDamage.GetValueOrDefault(__1) +
                __3.BlockedDamage + __3.UnblockedDamage;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnEnd))]
    private static void RecordPlayerTurn(ICombatState __0, CombatSide __1, ref Task __result)
    {
        if (__1 == CombatSide.Player && Encounters.TryGetValue(__0, out var encounter))
            __result = FinishDamageRound(__result, encounter);
    }

    internal static void TrackOneTurnDebuff(ICombatState combat, PowerModel power)
    {
        if (Encounters.TryGetValue(combat, out var encounter))
            encounter.OneTurnDebuffs.Add(power);
    }

    internal static bool SpendSpecialDebuff(ICombatState combat)
    {
        if (!Encounters.TryGetValue(combat, out var encounter) || encounter.SpecialDebuffSpent)
            return false;
        encounter.SpecialDebuffSpent = true;
        return true;
    }

    private static async Task FinishDamageRound(Task original, EncounterRules encounter)
    {
        await original;
        foreach (var power in encounter.OneTurnDebuffs)
        {
            if (power.Owner.Powers.Contains(power))
                await PowerCmd.Remove(power);
        }
        encounter.OneTurnDebuffs.Clear();
        int recentRounds = Math.Max(0, encounter.Rules.Forecast.GetProperty("recentPlayerRoundLimit").GetInt32());
        encounter.RecentDamage.Enqueue(new Dictionary<Creature, double>(encounter.PlayerDamage));
        encounter.PlayerDamage.Clear();
        while (encounter.RecentDamage.Count > recentRounds)
            encounter.RecentDamage.Dequeue();
    }

    private static EnemyRules? Register(EncounterRules encounter, Creature creature)
    {
        if (encounter.Enemies.TryGetValue(creature, out var existing))
            return existing;
        if (creature.Monster is not { } monster || creature.CombatState == null ||
            creature.IsPet || !creature.IsAlive ||
            encounter.Rules.KeepVanilla.Contains(monster.Id.Entry) ||
            FindProfile(encounter.Rules, monster) is not { Rules.Enabled: true } configured ||
            monster.MoveStateMachine is not { } machine ||
            machine.States.Values.OfType<MoveState>().Any(move => !IsOrdinaryMove(move)))
            return null;

        var state = new EnemyRules(configured.Rules, configured.Profile)
        {
            Balance = encounter.Planner.Start(configured.Profile)
        };
        encounter.Enemies.Add(creature, state);
        return state;
    }

    private static (MonsterRules Rules, MonsterProfile Profile)? FindProfile(RuleConfiguration rules, MonsterModel monster) =>
        monster.GetType().Assembly == typeof(MonsterModel).Assembly &&
        rules.Monsters.TryGetValue(monster.Id.Entry, out var entry)
            ? (entry, entry.AtAscension(RunManager.Instance.HasAscension(AscensionLevel.DeadlyEnemies)))
            : null;

    private static bool IsOrdinaryMove(MoveState move) => move.Intents.Count > 0 &&
        move.Intents.All(intent => intent.IntentType is IntentType.Attack or IntentType.Buff or IntentType.Debuff or
            IntentType.DebuffStrong or IntentType.Defend or IntentType.Heal or IntentType.StatusCard or IntentType.CardDebuff);

    private static bool KeepsNativeMove(EnemyRules enemy, MoveState move, int round) =>
        (enemy.Rules.FixedOpening && round == 1) || enemy.Rules.KeepMoves.Contains(move.Id);

    private static ActionContext Snapshot(Creature self, ICombatState combat, EncounterRules encounter)
    {
        var player = combat.PlayerCreatures[0];
        double priorWeight = encounter.Rules.Forecast.GetProperty("stagePriorWeightInPlayerRounds").GetDouble();
        double pressure = encounter.RecentDamage.Sum(round => round.GetValueOrDefault(self));
        double prior = encounter.DamagePrior / Math.Max(1, combat.Enemies.Count(enemy => enemy.IsAlive));
        double sampleWeight = priorWeight + encounter.RecentDamage.Count;
        double damage = sampleWeight > 0 ? (priorWeight * prior + pressure) / sampleWeight : prior;
        return encounter.Rules.TurnPrior with
        {
            ExpectedDamageAgainstMonster = damage,
            ExistingBlock = 0, // This action's Block and the next player attack follow the enemy block reset.
            MissingHp = self.MaxHp - self.CurrentHp,
            Strength = self.GetPower<StrengthPower>()?.Amount ?? 0,
            Weakened = self.HasPower<WeakPower>(),
            AlreadyIntangible = self.HasPower<IntangiblePower>(),
            PlayerAlreadyWeak = player.HasPower<WeakPower>(),
            PlayerAlreadyVulnerable = player.HasPower<VulnerablePower>(),
            PlayerHasArtifact = player.HasPower<ArtifactPower>(),
            PlayerAlreadyRestricted = player.HasPower<TangledPower>() || player.HasPower<RingingPower>() || player.HasPower<SmoggyPower>(),
            AllowHardCardLimit = combat.RoundNumber >= encounter.Rules.HardCardLimitFirstTurn,
            Act = Act(combat),
            ActiveEffects = NativeActions.ActiveEffects(self, player, combat.Enemies),
            RareBuffRoll = RandomFor(combat, (self.CombatId ?? 0) ^ 0xA4CB124Bu, combat.RoundNumber).NextDouble(),
            SpecialDebuffRoll = RandomFor(combat, (self.CombatId ?? 0) ^ 0x0BC16431u, combat.RoundNumber).NextDouble(),
            SpecialDebuffAvailable = !encounter.SpecialDebuffSpent &&
                (encounter.SpecialDebuffRound != combat.RoundNumber || encounter.SpecialDebuffOwner == self)
        };
    }

    private static double StageDamage(ICombatState combat, RuleConfiguration rules)
    {
        var stage = rules.Forecast.GetProperty("playerDamageByStage").GetProperty($"act{Act(combat)}");
        return stage.GetProperty("singleTarget").GetDouble() + stage.GetProperty("areaPerTarget").GetDouble();
    }

    private static int Act(ICombatState combat) => Math.Clamp(combat.RunState.CurrentActIndex + 1, 1, 3);

    private static bool IsCurrentCombat(ICombatState combat) =>
        CombatManager.Instance.IsInProgress && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat);

    private static Random RandomFor(ICombatState combat, uint creatureId, int round)
    {
        ulong seed = combat.RunState.Rng.Seed;
        unchecked
        {
            uint mixed = (uint)seed ^ (uint)(seed >> 32);
            mixed = (mixed ^ (uint)combat.RunState.TotalFloor) * 16777619;
            mixed = (mixed ^ creatureId) * 16777619;
            mixed = (mixed ^ (uint)round) * 16777619;
            return new Random((int)mixed);
        }
    }
}
