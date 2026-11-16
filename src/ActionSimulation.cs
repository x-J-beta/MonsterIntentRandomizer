namespace MonsterIntentRandomizer;

internal sealed record SimulatedTurn(int Round, ActionPlan Plan, bool Imbalanced, string? OriginalMove)
{
    public double Cost => Plan.Action?.Value ?? 0;
    public int Hits => Plan.Action?.AttackHits ?? 0;
}

internal static class ActionSimulation
{
    internal static IReadOnlyList<SimulatedTurn> Trace(RuleConfiguration rules, string monsterId, MonsterProfile profile,
        IReadOnlyList<OpeningPick> opening, int seed, int rounds,
        int firstEnemyAction = 1, int rooms = 0, int enemyCount = 1, string? phaseId = null)
    {
        bool specialStatusSpent = opening.Any(pick => pick.State.IsSpecialStatus);
        opening = opening.Where(pick => pick.MonsterId == monsterId).ToArray();
        var monster = rules.Monsters[monsterId];
        var sequence = rules.MovePresets.SequenceFor(monsterId, phaseId);
        var imbalanceRandom = new Random(seed ^ 0x4B82C097);
        var balances = rules.Planner.Start(profile, opening.Sum(pick => pick.Value));
        var activeEffects = new Dictionary<EffectKind, int>();
        var thresholds = new Dictionary<EffectKind, int>();
        var generatedCards = new Dictionary<EffectKind, EffectKind>();
        var usedEffects = new HashSet<EffectKind>();
        int vigor = 0;
        int drawDown = 0, energyDown = 0;
        foreach (var pick in opening)
            if (Enum.TryParse<EffectKind>(pick.State.Id.Replace("_", ""), ignoreCase: true, out var kind) &&
                ActionEffect.TargetFor(kind) == EffectTarget.Self)
                RecordEffect(kind, pick.State.Amount, 0, pick.State.GeneratedCard);
        var history = new ActionHistory();
        bool previousAttack = false;
        bool imbalanced = opening.Any(pick => pick.State.Id == "IMBALANCED");
        int completedMoves = 0;
        var turns = new List<SimulatedTurn>(rounds);
        for (int turn = 1; turn <= rounds; turn++)
        {
            drawDown = Math.Max(0, drawDown - 5);
            energyDown = Math.Max(0, energyDown - 3);
            var prior = rules.TurnContext(rooms, enemyCount, turn);
            int strength = prior.Strength + history.AddedStrength + history.RitualStrength + opening.Sum(pick =>
                pick.State.Id switch
                {
                    "STRENGTH" or "STRENGTH_DOWN" => pick.State.Amount,
                    "RITUAL" => (turn - 1) * pick.State.Amount,
                    _ => 0
                });
            bool skipFromImbalance = imbalanced && previousAttack && imbalanceRandom.NextDouble() < monster.FullBlockChance;
            var active = activeEffects.Where(entry => entry.Value >= turn).Select(entry => entry.Key)
                .Concat(usedEffects.Where(kind => rules.UniqueAction(kind) && !thresholds.ContainsKey(kind))).ToHashSet();
            if (drawDown > 0 && !rules.Actions.PolicyFor(EffectKind.DrawDown).Stackable)
                active.Add(EffectKind.DrawDown);
            if (energyDown > 0 && !rules.Actions.PolicyFor(EffectKind.EnergyDown).Stackable)
                active.Add(EffectKind.EnergyDown);
            var context = prior with
            {
                Strength = strength + vigor,
                Weakened = prior.Weakened || opening.Any(pick => pick.State.Id == "WEAK" && turn <= pick.State.Amount),
                SpecialStatusAvailable = !specialStatusSpent,
                Thresholds = thresholds,
                GeneratedCards = generatedCards,
                ActiveEffects = active
            };
            bool hasMove = turn >= firstEnemyAction && !skipFromImbalance && !opening.Any(pick => pick.State.Id == "STUN" &&
                    turn < firstEnemyAction + pick.State.Amount);
            var originalMove = hasMove ? sequence?.At(completedMoves++) : null;
            bool canRandomize = hasMove && monster.AllowsMove(originalMove);
            var plan = rules.PlanMove(monsterId, originalMove, balances, profile, context,
                MoveRandom.For(unchecked((uint)seed), rooms + 1, 1, turn), canRandomize);
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
                    RecordEffect(effect.Kind, effect.Amount, turn, effect.GeneratedCard);
                specialStatusSpent |= action.Effects!.Any(effect => effect.IsSpecialStatus);
            }
            else
                history = history with { RitualStrength = history.RitualStrength + history.AddedRitual };
        }
        return turns;

        void RecordEffect(EffectKind kind, int amount, int round, EffectKind? card)
        {
            usedEffects.Add(kind);
            if (kind == EffectKind.DrawDown)
            {
                drawDown += amount;
                return;
            }
            if (kind == EffectKind.EnergyDown)
            {
                energyDown += amount;
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
