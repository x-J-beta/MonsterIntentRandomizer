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
        public int PlannedRound { get; set; }
        public int SettledRound { get; set; }
        public double ActualThresholdCost { get; set; }
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
                if (state == null)
                    continue;

                var profile = WithSurvival(state.Profile, creature, combat, encounter);
                candidates[id] = rules.OpeningStatesFor(creature.ModelId.Entry, state.Rules)
                    .Where(key => key != "STUN" || (creature.Monster?.NextMove is { } first &&
                        !creature.IsStunned && state.Rules.AllowsMove(first.Id)))
                    .Select(key => rules.OpeningOption(key, profile, rooms, NativeActions.ThresholdFor(creature, key),
                        EffectPolicy.TryGetKind(key, out var kind) ? RandomizedCardPower.Find(creature, kind)?.Card : null))
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
                if (threshold ? applied > 0 && (previousThreshold == 0 || applied < previousThreshold) : applied != 0)
                    encounter.UsedEffects.Add((EffectTarget.Self, pick.State.Id));
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
                enemy.Plan = encounter.Rules.PlanMove(__instance.Id.Entry, enemy.Balance, profile,
                    context, RandomFor(combat, creature.CombatId ?? 0, round));
                enemy.RandomMove = enemy.Plan.Action is { } action ? NativeActions.Build(__instance, action, enemy.Rules) : null;
                enemy.PlannedRound = round;
                enemy.ActualThresholdCost = 0;
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
            __result = ExpireTemporaryThorns(__result, encounter);
    }

    internal static void TrackTemporaryThorns(ICombatState combat, ThornsPower power, int amount)
    {
        if (Encounters.TryGetValue(combat, out var encounter))
            encounter.TemporaryThorns[power] = encounter.TemporaryThorns.GetValueOrDefault(power) + amount;
    }

    internal static bool ManagesCombat(ICombatState combat) => Encounters.TryGetValue(combat, out _);

    internal static bool SpendSpecialDebuff(ICombatState combat)
    {
        if (!Encounters.TryGetValue(combat, out var encounter) || encounter.SpecialDebuffSpent)
            return false;
        encounter.SpecialDebuffSpent = true;
        return true;
    }

    internal static bool CanUseEffect(ICombatState combat, Creature self, Creature player, EffectKind kind)
    {
        if (!EffectPolicy.CanTargetMonster(kind, self.Monster?.Id.Entry))
            return false;
        if (!Encounters.TryGetValue(combat, out var encounter))
            return true;
        var targetKind = ActionEffect.TargetFor(kind);
        var target = targetKind == EffectTarget.Self ? self : player;
        bool stackable = encounter.Rules.Actions.PolicyFor(kind).Stackable;
        if (!stackable && NativeActions.HasActionEffect(kind, target))
            return false;
        if (ActionEffect.UsesThreshold(kind) && NativeActions.ThresholdFor(self, EffectPolicy.Id(kind)) > 0)
            return stackable;
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

    internal static void RecordThresholdCost(ICombatState combat, Creature owner, double cost)
    {
        if (Encounters.TryGetValue(combat, out var encounter) && encounter.Enemies.TryGetValue(owner, out var state))
            state.ActualThresholdCost += cost;
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
        rules.Monsters.TryGetValue(monster.Id.Entry, out var entry)
            ? (entry, Adjust(rules, monster.NextMove is { } move && entry.PhaseForMove(move.Id) is { } phase
                ? phase.Apply(entry.Profile) : entry.Profile))
            : null;

    private static MonsterProfile Adjust(RuleConfiguration rules, MonsterProfile profile) =>
        rules.PoolSettings.Adjust(profile, RunManager.Instance.HasAscension(AscensionLevel.DeadlyEnemies));

    private static ActionContext Snapshot(Creature self, ICombatState combat, EncounterRules encounter)
    {
        var player = combat.PlayerCreatures[0];
        return encounter.Rules.TurnContext(encounter.Rooms, combat.Enemies.Count(enemy => enemy.IsAlive), combat.RoundNumber) with
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
                (encounter.UniquePlans.TryGetValue(kind, out var reserved) && reserved.Round == combat.RoundNumber &&
                    reserved.Owner != self && reserved.Owner.IsAlive)).ToHashSet(),
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

    private static Random RandomFor(ICombatState combat, uint creatureId, int round) =>
        MoveRandom.For(combat.RunState.Rng.Seed, combat.RunState.TotalFloor, creatureId, round);
}
