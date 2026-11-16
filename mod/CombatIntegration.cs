using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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

    private sealed class EncounterRules(RuleConfiguration rules, Random random)
    {
        public RuleConfiguration Rules { get; } = rules;
        public Random Random { get; } = random;
        public Dictionary<Creature, EnemyRules> Enemies { get; } = new();
        public Dictionary<ThornsPower, int> TemporaryThorns { get; } = new();
        public HashSet<EffectKind> InsertedCurses { get; } = [];
        public HashSet<EffectKind> FixedStatuses { get; init; } = [];
        public int SpecialStatusesSpent { get; set; }
        public int? LastMoveRandomSkipRollRound { get; set; }
        public bool Ready { get; set; }
        public int Rooms { get; init; }
        public int CompletedPlayerTurns { get; set; }
        public double? AveragePlayerDamage { get; set; }
    }

    private sealed class EnemyRules(MonsterRules rules, MonsterProfile profile)
    {
        public MonsterRules Rules { get; } = rules;
        public MonsterProfile Profile { get; } = profile;
        public Pools Balance { get; set; }
        public int PlannedRound { get; set; }
        public int SettledRound { get; set; }
        public double ActualThresholdCost { get; set; }
        public MoveState? RandomMove { get; set; }
        public ActionPlan? Plan { get; set; }
        public HashSet<EffectKind> PendingEffects { get; } = [];
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
            var encounter = new EncounterRules(rules, CombatRandom.For(combat.RunState.Rng.Seed, combat.RunState.TotalFloor))
            {
                Rooms = rooms,
                FixedStatuses = rules.FixedKnightStatuses(combat.Enemies.Select(enemy => enemy.ModelId.Entry))
            };
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

                var profile = WithSurvival(state.Profile, creature, combat, encounter);
                candidates[id] = rules.OpeningStatesFor(creature.ModelId.Entry, state.Rules)
                    .Where(key => key != "STUN" || (creature.Monster?.NextMove is { } first &&
                        !creature.IsStunned && state.Rules.AllowsMove(first.Id)))
                    .Select(key => rules.OpeningOption(key, profile, rooms, NativeActions.ThresholdFor(creature, key),
                        EffectPolicy.TryGetKind(key, out var kind) ? RandomizedCardPower.Find(creature, kind)?.Card : null,
                        creature.ModelId.Entry))
                    .Where(option => NativeActions.CanApplyOpening(creature, option))
                    .ToArray();
            }

            var draws = OpeningValuation.Draw(candidates, actionValues, rules.Draws, encounter.Random);
            // A newly added Artifact should not consume a simultaneously drawn penalty.
            foreach (var pick in draws.OrderBy(pick => pick.State.Id == "ARTIFACT"))
            {
                if (!IsCurrentCombat(combat))
                    return;
                Creature target = byId[pick.MonsterId];
                int previousThreshold = NativeActions.ThresholdFor(target, pick.State.Id);
                int applied = await NativeActions.ApplyOpening(target, pick.State);
                var state = encounter.Enemies[target];
                var pricing = rules.OpeningValues[pick.State.Id];
                var profile = WithSurvival(state.Profile, target, combat, encounter);
                bool threshold = EffectPolicy.IsThreshold(pick.State.Id);
                double price = threshold
                    ? OpeningValuation.ThresholdCost(pricing, profile, previousThreshold, applied)
                    : OpeningValuation.Evaluate(pricing, profile, applied);
                if (pick.State.GeneratedCard.HasValue && EffectPolicy.TryGetKind(pick.State.Id, out var kind))
                    price *= TriggeredCardBuffs.Scale(kind, pick.State.GeneratedCard, rules.Actions, profile);
                if (pick.State.Unique &&
                    (threshold ? applied > 0 && (previousThreshold == 0 || applied < previousThreshold) : applied != 0))
                    encounter.SpecialStatusesSpent++;
                state.Balance = state.Balance with
                {
                    ActionValue = state.Balance.ActionValue - price
                };
            }
            await VitalSparkIntegration.Refresh(combat);
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
            if (enemy == null || creature.IsStunned || !enemy.Rules.AllowsMove(original.Id))
                return;
            int round = combat.RoundNumber;
            if (enemy.SettledRound >= round)
                return;
            if (enemy.PlannedRound != round)
            {
                var context = Snapshot(creature, combat, encounter);
                var phase = enemy.Rules.PhaseForMove(original.Id);
                var configured = enemy.Rules.Phases is { Count: > 0 }
                    ? Adjust(encounter.Rules, phase?.Apply(enemy.Rules.Profile) ?? enemy.Rules.Profile)
                    : enemy.Profile;
                var profile = WithSurvival(configured, creature, combat, encounter);
                SkipMoveSample(encounter, round);
                enemy.Plan = encounter.Rules.PlanMove(__instance.Id.Entry, original.Id, enemy.Balance, profile,
                    context, encounter.Random);
                enemy.RandomMove = enemy.Plan.Action is { } action
                    ? NativeActions.Build(__instance, action, enemy.Rules, enemy.Plan.AnimatedAttackHits) : null;
                enemy.PlannedRound = round;
                enemy.ActualThresholdCost = 0;
                enemy.PendingEffects.Clear();
                foreach (var effect in enemy.Plan.Action?.Effects ?? [])
                    if (effect.Unique)
                        enemy.PendingEffects.Add(effect.Kind);
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
        double plannedThresholdCost = action.Effects?.Sum(effect => effect.Threshold?.Planned ?? 0) ?? 0;
        state.Balance = plan.After with
        {
            ActionValue = plan.After.ActionValue + plannedThresholdCost - state.ActualThresholdCost
        };
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnEnd))]
    private static void RecordPlayerTurn(ICombatState __0, CombatSide __1, ref Task __result)
    {
        if (__1 == CombatSide.Player && Encounters.TryGetValue(__0, out var encounter))
            __result = CompletePlayerTurn(__result, __0, encounter);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(IntangiblePower), nameof(IntangiblePower.AfterSideTurnEnd))]
    private static bool HonorIntangibleDurationFlag(IntangiblePower __instance, CombatSide side, ref Task __result)
    {
        if (side != CombatSide.Enemy || __instance.Owner.Side != CombatSide.Enemy || !__instance.SkipNextDurationTick)
            return true;

        // Native Intangible decrements directly, bypassing the duration flag.
        __result = PowerCmd.TickDownDuration(__instance);
        return false;
    }

    internal static void TrackTemporaryThorns(ICombatState combat, ThornsPower power, int amount)
    {
        if (Encounters.TryGetValue(combat, out var encounter))
            encounter.TemporaryThorns[power] = encounter.TemporaryThorns.GetValueOrDefault(power) + amount;
    }

    internal static bool ManagesCombat(ICombatState combat) => Encounters.TryGetValue(combat, out _);

    internal static bool SpendSpecialStatus(ICombatState combat, Creature owner, EffectKind kind)
    {
        if (!Encounters.TryGetValue(combat, out var encounter) ||
            encounter.SpecialStatusesSpent >= encounter.Rules.PoolSettings.SpecialStatusLimit)
            return false;
        encounter.SpecialStatusesSpent++;
        if (encounter.Enemies.TryGetValue(owner, out var enemy))
            enemy.PendingEffects.Remove(kind);
        return true;
    }

    internal static bool CanUseEffect(ICombatState combat, Creature self, Creature player, EffectKind kind)
    {
        if (!EffectPolicy.CanTargetMonster(kind, self.Monster?.Id.Entry))
            return false;
        if (!Encounters.TryGetValue(combat, out var encounter))
            return true;
        if (encounter.FixedStatuses.Contains(kind))
            return false;
        var policy = encounter.Rules.Actions.PolicyFor(kind, self.Monster?.Id.Entry);
        var target = ActionEffect.TargetFor(kind) == EffectTarget.Self ? self : player;
        return policy.Stackable || !(ActionEffect.IsCurseCard(kind)
            ? encounter.InsertedCurses.Contains(kind) : NativeActions.HasActionEffect(kind, target));
    }

    internal static void RecordCurseInsertion(ICombatState combat, EffectKind kind)
    {
        if (Encounters.TryGetValue(combat, out var encounter))
            encounter.InsertedCurses.Add(kind);
    }

    internal static void RecordThresholdCost(ICombatState combat, Creature owner, double cost)
    {
        if (Encounters.TryGetValue(combat, out var encounter) && encounter.Enemies.TryGetValue(owner, out var state))
            state.ActualThresholdCost += cost;
    }

    private static async Task CompletePlayerTurn(Task original, ICombatState combat, EncounterRules encounter)
    {
        await original;
        if (encounter.Rules.PlayerAction.UseCombatDamage && IsCurrentCombat(combat))
        {
            double damage = CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>()
                .Where(entry => entry.Receiver.Side == CombatSide.Enemy &&
                    (entry.Dealer?.Side ?? entry.CardSource?.Owner?.Creature.Side) == CombatSide.Player)
                .Sum(entry => (double)entry.Result.TotalDamage);
            encounter.CompletedPlayerTurns++;
            encounter.AveragePlayerDamage = damage / encounter.CompletedPlayerTurns;
        }

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
            creature.Side != CombatSide.Enemy || !creature.IsAlive ||
            FindProfile(encounter.Rules, monster) is not { Rules.Enabled: true } configured ||
            !configured.Rules.HasEnabledStage ||
            monster.MoveStateMachine is not { } machine ||
            !machine.States.Values.OfType<MoveState>().Any(move => configured.Rules.AllowsMove(move.Id)))
            return null;

        var state = new EnemyRules(configured.Rules, configured.Profile)
        {
            Balance = encounter.Rules.Planner.Start(configured.Profile)
        };
        encounter.Enemies.Add(creature, state);
        return state;
    }

    private static (MonsterRules Rules, MonsterProfile Profile)? FindProfile(RuleConfiguration rules, MonsterModel monster) =>
        monster.GetType().Assembly == typeof(MonsterModel).Assembly &&
        rules.Monsters.TryGetValue(rules.MovePresets.ProfileId(monster.Id.Entry), out var entry)
            ? (entry, Adjust(rules, monster.NextMove is { } move && entry.PhaseForMove(move.Id) is { } phase
                ? phase.Apply(entry.Profile) : entry.Profile))
            : null;

    private static MonsterProfile Adjust(RuleConfiguration rules, MonsterProfile profile) =>
        rules.PoolSettings.Adjust(profile, RunManager.Instance.HasAscension(AscensionLevel.DeadlyEnemies));

    private static ActionContext Snapshot(Creature self, ICombatState combat, EncounterRules encounter)
    {
        var player = combat.PlayerCreatures[0];
        int round = combat.RoundNumber;
        var pending = encounter.Enemies.Where(entry => entry.Key != self && entry.Key.IsAlive &&
            entry.Value.PlannedRound == round && entry.Value.SettledRound < round)
            .SelectMany(entry => entry.Value.PendingEffects).ToArray();
        return encounter.Rules.TurnContext(encounter.Rooms, combat.Enemies.Count(enemy => enemy.IsAlive),
            combat.RoundNumber, encounter.AveragePlayerDamage) with
        {
            ExistingBlock = 0, // This action's Block and the next player attack follow the enemy block reset.
            MissingHp = self.MaxHp - self.CurrentHp,
            Strength = (self.GetPower<StrengthPower>()?.Amount ?? 0) + (self.GetPower<VigorPower>()?.Amount ?? 0),
            Weakened = self.HasPower<WeakPower>(),
            PlayerHasArtifact = player.HasPower<ArtifactPower>(),
            PlayerAlreadyRestricted = player.HasPower<TangledPower>() || player.HasPower<RingingPower>() || player.HasPower<SmoggyPower>(),
            Thresholds = (encounter.Rules.Actions.Buffs?.Keys ?? []).Where(ActionEffect.UsesThreshold)
                .ToDictionary(kind => kind, kind => NativeActions.ThresholdFor(self, EffectPolicy.Id(kind))),
            GeneratedCards = TriggeredCardBuffs.Types.Keys
                .Select(kind => (Kind: kind, Power: RandomizedCardPower.Find(self, kind)))
                .Where(entry => entry.Power != null).ToDictionary(entry => entry.Kind, entry => entry.Power!.Card),
            ActiveEffects = Enum.GetValues<EffectKind>().Where(kind =>
                !CanUseEffect(combat, self, player, kind) ||
                (pending.Contains(kind) && ActionEffect.TargetFor(kind) == EffectTarget.Player &&
                    !encounter.Rules.Actions.PolicyFor(kind).Stackable)).ToHashSet(),
            SpecialStatusesRemaining = Math.Max(0,
                encounter.Rules.PoolSettings.SpecialStatusLimit - encounter.SpecialStatusesSpent - pending.Length)
        };
    }

    private static MonsterProfile WithSurvival(MonsterProfile profile, Creature self, ICombatState combat, EncounterRules encounter) =>
        profile with
        {
            BaseExpectedSurvivalTurns = encounter.Rules.PlayerAction.Survival(self.CurrentHp,
                combat.Enemies.Where(enemy => enemy != self && enemy.IsAlive).Sum(enemy => (double)enemy.CurrentHp),
                encounter.Rooms, encounter.AveragePlayerDamage),
            PlayerActionValue = encounter.Rules.PlayerAction.At(encounter.Rooms, encounter.AveragePlayerDamage),
            CurrentHp = self.CurrentHp,
            RoomsEntered = encounter.Rooms + 1,
            MonstersBehind = combat.Enemies.SkipWhile(enemy => enemy != self).Skip(1).Count(enemy => enemy.IsAlive)
        };


    private static bool IsCurrentCombat(ICombatState combat) =>
        CombatManager.Instance.IsInProgress && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat);

    private static void SkipMoveSample(EncounterRules encounter, int round)
    {
        var pools = encounter.Rules.PoolSettings;
        if (!pools.ExternalMoveRandomSkip || pools.Chaos <= 0.3 || encounter.LastMoveRandomSkipRollRound == round)
            return;

        encounter.LastMoveRandomSkipRollRound = round;
        double chance = Math.Pow(pools.Chaos - 0.3, 3);
        if (RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue < chance)
            encounter.Random.NextDouble();
    }
}
