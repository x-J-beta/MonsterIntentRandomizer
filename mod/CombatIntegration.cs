using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
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
        public Dictionary<ThornsPower, int> TemporaryThorns { get; } = new();
        public HashSet<(EffectTarget Target, string Id)> UsedEffects { get; } = new();
        public Dictionary<EffectKind, (Creature Owner, int Round)> UniquePlans { get; } = new();
        public Creature? SpecialDebuffOwner { get; set; }
        public int SpecialDebuffRound { get; set; }
        public bool SpecialDebuffSpent { get; set; }
        public bool Ready { get; set; }
        public int Rooms { get; init; }
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
            int rooms = Math.Max(0, combat.RunState.MapPointHistory.SelectMany(act => act).Sum(point => point.Rooms.Count) - 1);
            var encounter = new EncounterRules(rules) { Rooms = rooms };
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
                if (state == null || ProtectedByAlly(creature, state.Rules))
                    continue;

                var profile = WithSurvival(state.Profile, creature, combat, encounter);
                candidates[id] = rules.OpeningStatesFor(state.Rules)
                    .Where(key => key != "STUN" || (creature.Monster?.NextMove is { } first &&
                        CanReplaceMove(creature, state, first, 1)))
                    .Select(key => rules.OpeningOption(key, profile, rooms))
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
                if (applied != 0)
                    encounter.UsedEffects.Add((EffectTarget.Self, pick.State.Id));
                var state = encounter.Enemies[target];
                var price = rules.OpeningValues[pick.State.Id];
                state.Balance = state.Balance with
                {
                    ActionValue = state.Balance.ActionValue - OpeningValuation.Evaluate(price,
                        WithSurvival(state.Profile, target, combat, encounter), applied)
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
            if (enemy == null || !CanReplaceMove(creature, enemy, original, combat.RoundNumber))
                return;
            int round = combat.RoundNumber;
            if (enemy.SettledRound >= round)
                return;
            if (enemy.PlannedRound != round)
            {
                var context = Snapshot(creature, combat, encounter);
                var profile = WithSurvival(enemy.Profile, creature, combat, encounter);
                enemy.Plan = encounter.Planner.Plan(enemy.Balance, profile,
                    encounter.Catalog.Generate(profile, context, enemy.History, round),
                    RandomFor(combat, creature.CombatId ?? 0, round),
                    buildAttack: (hits, target) => encounter.Catalog.BuildAttack(profile, context, hits, target));
                enemy.RandomMove = enemy.Plan.Action is { } action ? NativeActions.Build(__instance, action) : null;
                enemy.PlannedRound = round;
                foreach (var effect in enemy.Plan.Action?.Effects ?? [])
                    if (encounter.Rules.UniqueAction(effect.Kind))
                        encounter.UniquePlans[effect.Kind] = (creature, round);
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
            state.SettledRound >= __0.RoundNumber || state.PlannedRound != __0.RoundNumber ||
            !ReferenceEquals(state.RandomMove, __2) || state.Plan is not { Action: { } action } plan)
            return;

        state.SettledRound = __0.RoundNumber;
        state.History = state.History.Record(action);
        state.Balance = plan.After;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnEnd))]
    private static void RecordPlayerTurn(ICombatState __0, CombatSide __1, ref Task __result)
    {
        if (__1 == CombatSide.Player && Encounters.TryGetValue(__0, out var encounter))
            __result = ExpireTemporaryThorns(__result, encounter);
    }

    internal static void TrackTemporaryThorns(ICombatState combat, ThornsPower power, int amount)
    {
        if (Encounters.TryGetValue(combat, out var encounter))
            encounter.TemporaryThorns[power] = encounter.TemporaryThorns.GetValueOrDefault(power) + amount;
    }

    internal static bool SpendSpecialDebuff(ICombatState combat)
    {
        if (!Encounters.TryGetValue(combat, out var encounter) || encounter.SpecialDebuffSpent)
            return false;
        encounter.SpecialDebuffSpent = true;
        return true;
    }

    internal static bool CanUseEffect(ICombatState combat, Creature self, Creature player, EffectKind kind)
    {
        if (!Encounters.TryGetValue(combat, out var encounter))
            return true;
        var targetKind = ActionEffect.TargetFor(kind);
        var target = targetKind == EffectTarget.Self ? self : player;
        if (!encounter.Rules.Actions.PolicyFor(kind).Stackable && NativeActions.HasActionEffect(kind, target))
            return false;
        return !encounter.Rules.UniqueAction(kind) ||
            (!encounter.UsedEffects.Contains((targetKind, EffectPolicy.Id(kind))) &&
                !(targetKind == EffectTarget.Self ? combat.Enemies : combat.PlayerCreatures)
                    .Any(creature => creature.IsAlive && NativeActions.HasActionEffect(kind, creature)));
    }

    internal static void RecordEffect(ICombatState combat, EffectKind kind)
    {
        if (Encounters.TryGetValue(combat, out var encounter))
            encounter.UsedEffects.Add((ActionEffect.TargetFor(kind), EffectPolicy.Id(kind)));
    }

    private static async Task ExpireTemporaryThorns(Task original, EncounterRules encounter)
    {
        await original;
        foreach (var (power, amount) in encounter.TemporaryThorns)
        {
            if (power.Owner.IsAlive && power.Owner.Powers.Contains(power))
                await PowerCmd.ModifyAmount(new ThrowingPlayerChoiceContext(), power, -Math.Min(amount, power.Amount),
                    power.Owner, null);
        }
        encounter.TemporaryThorns.Clear();
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
            !SupportsRandomMoves(machine, configured.Rules))
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
            ? (entry, rules.PoolSettings.Adjust(entry.Profile, RunManager.Instance.HasAscension(AscensionLevel.DeadlyEnemies)))
            : null;

    private static bool IsOrdinaryMove(MoveState move) => move.Intents.Count > 0 &&
        move.Intents.All(intent => intent.IntentType is IntentType.Attack or IntentType.Buff or IntentType.Debuff or
            IntentType.DebuffStrong or IntentType.Defend or IntentType.Heal or IntentType.StatusCard or IntentType.CardDebuff);

    private static bool SupportsRandomMoves(MonsterMoveStateMachine machine, MonsterRules rules)
    {
        if (rules.RandomizeAllMoves)
            return true;
        var moves = machine.States.Values.OfType<MoveState>();
        return rules.RandomMoves is { } allowed
            ? moves.Any(move => allowed.Contains(move.Id) && !rules.KeepMoves.Contains(move.Id) && IsOrdinaryMove(move))
            : moves.All(move => rules.KeepMoves.Contains(move.Id) || IsOrdinaryMove(move));
    }

    private static bool CanReplaceMove(Creature creature, EnemyRules enemy, MoveState move, int round) =>
        !creature.IsStunned && (enemy.Rules.RandomizeAllMoves || move.CanTransitionAway && IsOrdinaryMove(move)) &&
        !KeepsNativeMove(creature, enemy, move, round);

    private static bool KeepsNativeMove(Creature creature, EnemyRules enemy, MoveState move, int round) =>
        (enemy.Rules.FixedOpening && round == 1) || enemy.Rules.KeepMoves.Contains(move.Id) ||
        (enemy.Rules.RandomMoves is { } allowed && !allowed.Contains(move.Id)) ||
        ProtectedByAlly(creature, enemy.Rules) ||
        (enemy.Rules.RandomizeWhenPower?.Trim() is { Length: > 0 } id &&
            !creature.Powers.Any(power => MatchesPower(power.Id.Entry, id)));

    private static bool ProtectedByAlly(Creature creature, MonsterRules rules) =>
        rules.KeepVanillaWhileAllyAlive is { Length: > 0 } id && creature.CombatState is { } combat &&
        combat.Enemies.Any(ally => ally != creature && ally.IsAlive && ally.Monster?.Id.Entry == id);

    private static bool MatchesPower(string entry, string required) =>
        entry.Equals(required, StringComparison.OrdinalIgnoreCase) ||
        (entry.EndsWith("_POWER", StringComparison.OrdinalIgnoreCase) &&
            entry.AsSpan(0, entry.Length - "_POWER".Length).Equals(required, StringComparison.OrdinalIgnoreCase));

    private static ActionContext Snapshot(Creature self, ICombatState combat, EncounterRules encounter)
    {
        var player = combat.PlayerCreatures[0];
        return encounter.Rules.TurnPrior with
        {
            ExpectedDamageAgainstMonster = encounter.Rules.PlayerAction.At(encounter.Rooms) /
                Math.Max(1, combat.Enemies.Count(enemy => enemy.IsAlive)),
            Rooms = encounter.Rooms,
            ExistingBlock = 0, // This action's Block and the next player attack follow the enemy block reset.
            MissingHp = self.MaxHp - self.CurrentHp,
            Strength = (self.GetPower<StrengthPower>()?.Amount ?? 0) + (self.GetPower<VigorPower>()?.Amount ?? 0),
            Weakened = self.HasPower<WeakPower>(),
            PlayerHasArtifact = player.HasPower<ArtifactPower>(),
            PlayerAlreadyRestricted = player.HasPower<TangledPower>() || player.HasPower<RingingPower>() || player.HasPower<SmoggyPower>(),
            AllowHardCardLimit = combat.RoundNumber >= encounter.Rules.HardCardLimitFirstTurn,
            ActiveEffects = Enum.GetValues<EffectKind>().Where(kind =>
                !CanUseEffect(combat, self, player, kind) ||
                (encounter.UniquePlans.TryGetValue(kind, out var reserved) && reserved.Round == combat.RoundNumber &&
                    reserved.Owner != self && reserved.Owner.IsAlive)).ToHashSet(),
            SpecialDebuffRoll = RandomFor(combat, (self.CombatId ?? 0) ^ 0x0BC16431u, combat.RoundNumber).NextDouble(),
            SpecialDebuffAvailable = !encounter.SpecialDebuffSpent &&
                (encounter.SpecialDebuffRound != combat.RoundNumber || encounter.SpecialDebuffOwner == self)
        };
    }

    private static MonsterProfile WithSurvival(MonsterProfile profile, Creature self, ICombatState combat, EncounterRules encounter) =>
        profile with
        {
            BaseExpectedSurvivalTurns = encounter.Rules.PlayerAction.Survival(self.CurrentHp,
                combat.Enemies.Where(enemy => enemy != self && enemy.IsAlive).Sum(enemy => (double)enemy.CurrentHp), encounter.Rooms),
            PlayerActionValue = encounter.Rules.PlayerAction.At(encounter.Rooms),
            CurrentHp = self.CurrentHp,
            RoomsEntered = encounter.Rooms + 1
        };


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
