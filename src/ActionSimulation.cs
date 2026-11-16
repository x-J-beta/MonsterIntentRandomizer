namespace MonsterIntentRandomizer;

internal sealed record SimulatedTurn(int Round, ActionPlan Plan, bool Imbalanced, string? OriginalMove)
{
    public double Cost => Plan.Action?.Value ?? 0;
    public int Hits => Plan.Action?.AttackHits ?? 0;
}

internal static class ActionSimulation
{
    internal static IReadOnlyList<SimulatedTurn> Trace(RuleConfiguration rules, string monsterId, MonsterProfile profile,
        IReadOnlyList<OpeningPick> opening, Random random, int rounds,
        int firstEnemyAction = 1, int rooms = 0, int enemyCount = 1, string? phaseId = null)
    {
        int specialStatusesSpent = opening.Count(pick => pick.State.Unique);
        var usedPlayerEffects = new HashSet<EffectKind>();
        opening = opening.Where(pick => pick.MonsterId == monsterId).ToArray();
        var monster = rules.Monsters[monsterId];
        var fixedStatuses = rules.FixedKnightStatuses(enemyCount > 1 &&
            monsterId is "MAGI_KNIGHT" or "SPECTRAL_KNIGHT" or "FLAIL_KNIGHT"
                ? ["MAGI_KNIGHT", "SPECTRAL_KNIGHT"] : [monsterId]);
        var sequence = rules.MovePresets.SequenceFor(monsterId, phaseId);
        var balances = rules.Planner.Start(profile, opening.Sum(pick => pick.Value));
        var activeEffects = new Dictionary<EffectKind, int>();
        var thresholds = new Dictionary<EffectKind, int>();
        var generatedCards = new Dictionary<EffectKind, EffectKind>();
        int vigor = 0;
        foreach (var pick in opening)
            if (EffectPolicy.TryGetKind(pick.State.Id, out var kind) &&
                ActionEffect.TargetFor(kind) == EffectTarget.Self)
                RecordEffect(kind, pick.State.Amount, 0, pick.State.GeneratedCard);
        var history = new ActionHistory();
        bool previousAttack = false;
        bool imbalanced = opening.Any(pick => pick.State.Id == "IMBALANCED");
        int completedMoves = 0;
        var turns = new List<SimulatedTurn>(rounds);
        for (int turn = 1; turn <= rounds; turn++)
        {
            var prior = rules.TurnContext(rooms, enemyCount, turn);
            int strength = prior.Strength + history.AddedStrength + history.RitualStrength + opening.Sum(pick =>
                pick.State.Id switch
                {
                    "STRENGTH" or "STRENGTH_DOWN" => pick.State.Amount,
                    "RITUAL" => (turn - 1) * pick.State.Amount,
                    _ => 0
                });
            bool skipFromImbalance = imbalanced && previousAttack && random.NextDouble() < monster.FullBlockChance;
            var active = activeEffects.Where(entry => entry.Value >= turn).Select(entry => entry.Key)
                .Concat(usedPlayerEffects.Where(kind => !rules.Actions.PolicyFor(kind, monsterId).Stackable)).ToHashSet();
            active.UnionWith(fixedStatuses);
            var context = prior with
            {
                Strength = strength + vigor,
                Weakened = prior.Weakened || opening.Any(pick => pick.State.Id == "WEAK" && turn <= pick.State.Amount),
                SpecialStatusesRemaining = Math.Max(0, rules.PoolSettings.SpecialStatusLimit - specialStatusesSpent),
                Thresholds = thresholds,
                GeneratedCards = generatedCards,
                ActiveEffects = active
            };
            bool hasMove = turn >= firstEnemyAction && !skipFromImbalance && !opening.Any(pick => pick.State.Id == "STUN" &&
                    turn < firstEnemyAction + pick.State.Amount);
            var originalMove = hasMove ? sequence?.At(completedMoves++) : null;
            bool canRandomize = hasMove && monster.AllowsMove(originalMove);
            var plan = rules.PlanMove(monsterId, originalMove, balances, profile, context, random, canRandomize);
            balances = plan.After;
            turns.Add(new SimulatedTurn(turn, plan, skipFromImbalance, originalMove));

            previousAttack = plan.Action?.IsAttack == true;
            if (previousAttack)
            {
                vigor = 0;
                activeEffects.Remove(EffectKind.Vigor);
            }
            if (plan.Action is { } action)
            {
                history = history.Record(action);
                foreach (var effect in action.Effects!)
                {
                    RecordEffect(effect.Kind, effect.Amount, turn, effect.GeneratedCard);
                    if (effect.Unique)
                        specialStatusesSpent++;
                }
            }
            else
                history = history with { RitualStrength = history.RitualStrength + history.AddedRitual };
        }
        return turns;

        void RecordEffect(EffectKind kind, int amount, int round, EffectKind? card)
        {
            if (ActionEffect.IsPlayerEffect(kind))
            {
                usedPlayerEffects.Add(kind);
                return;
            }
            if (ActionEffect.UsesThreshold(kind))
                thresholds[kind] = amount;
            if (card.HasValue)
                generatedCards[kind] = card.Value;
            if (kind == EffectKind.Vigor)
                vigor += amount;
            if (rules.Actions.PolicyFor(kind, monsterId).Stackable)
                return;
            if (kind is EffectKind.Block or EffectKind.Heal or EffectKind.Attack)
                return;
            activeEffects[kind] = kind switch
            {
                EffectKind.Regen or EffectKind.Plating or EffectKind.Intangible or EffectKind.Weak or
                    EffectKind.Vulnerable or EffectKind.Frail => round + amount,
                EffectKind.Tangled or EffectKind.Ringing or EffectKind.TemporaryThorns => round + 1,
                _ => int.MaxValue
            };
        }
    }
}
