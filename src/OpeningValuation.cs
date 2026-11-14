namespace MonsterIntentRandomizer;

public sealed record ExpectedMove(double Value, double AttackFrequency, double DamagePerHit, double Hits);
public sealed record OpeningOption(OpeningState State, double? Value);
public sealed record OpeningPick(string MonsterId, OpeningState State, double Value);
public sealed record OpeningDraws(double None, double Positive, double Negative, int Attempts,
    int MaxPerMonster, double MaxTotalAbsoluteValue);

public static class OpeningValuation
{
    public static double? Evaluate(
        OpeningState state, IReadOnlyList<ForecastWindow> baseline,
        IReadOnlyList<ForecastWindow> modified, IReadOnlyList<ExpectedMove> moves, double discount,
        double artifactValue = 0, double artifactChancePerTargetedWindow = 0,
        double weakDamageReduction = 0.25, double healCoefficient = 0.4,
        double blockCoefficient = 0.4, double effectiveHpCoefficient = 0.4,
        double vulnerableHpCoefficient = 0.7, double fullBlockChance = 0)
    {
        double value = 0;
        double artifactRemaining = 1;
        for (int index = 0; index < baseline.Count; index++)
        {
            ForecastWindow before = baseline[index];
            ForecastWindow after = modified[index];
            ExpectedMove move = moves[Math.Min(index, moves.Count - 1)];
            double windowDiscount = Math.Pow(discount, index);
            double gainedActionValue = (after.AliveAtAction - before.AliveAtAction) * move.Value;
            value += windowDiscount * (state.Id switch
            {
                "NONE" => 0,
                "STRENGTH" => HitChange(move, state.Amount) * before.AliveAtAction,
                "STRENGTH_DOWN" => HitChange(move, -Math.Abs(state.Amount)) * before.AliveAtAction,
                "WEAK" when index < state.Amount =>
                    move.AttackFrequency * move.Hits *
                    (CombatMath.AttackDamagePerHit(move.DamagePerHit, weak: true,
                        weakDamageReduction: weakDamageReduction) - move.DamagePerHit) *
                    before.AliveAtAction,
                "WEAK" => 0,
                "RITUAL" => HitChange(move, state.Amount * index) * before.AliveAtAction,
                "THORNS" => state.Amount * before.IncomingAttackHits,
                "REGEN" or "PLATING" or "DOOM" or "DEMISE" or "VULNERABLE" or "STUN" => gainedActionValue,
                "IMBALANCED" when index > 0 => -fullBlockChance * moves[Math.Min(index - 1, moves.Count - 1)].AttackFrequency *
                    before.AliveAtAction * move.Value,
                "IMBALANCED" => 0,
                "ARTIFACT" => 0,
                _ => double.NaN
            });
            if (state.Id == "ARTIFACT")
            {
                double exposure = index == 0 ? 1 : baseline[index - 1].AliveAtAction;
                double chance = Math.Clamp(artifactChancePerTargetedWindow * exposure, 0, 1);
                value += windowDiscount * artifactRemaining * chance * artifactValue;
                artifactRemaining *= 1 - chance;
            }
        }

        bool changedActions = baseline.Zip(modified).Any(pair =>
            Math.Abs(pair.First.AliveAtAction - pair.Second.AliveAtAction) > 1e-9);
        if (state.Id == "VULNERABLE")
        {
            double extraHp = baseline.Zip(modified).Select((pair, index) => Math.Pow(discount, index) *
                Math.Max(0, pair.Second.VulnerableBonusDamage - pair.First.VulnerableBonusDamage)).Sum();
            value = Math.Min(value, -vulnerableHpCoefficient * extraHp);
        }
        else if (!changedActions)
        {
            // When both projections survive the same actions, use effective HP as a proxy.
            for (int index = 0; index < modified.Count; index++)
            {
                ForecastWindow after = modified[index];
                value += Math.Pow(discount, index) * (state.Id switch
                {
                    "REGEN" => healCoefficient * Math.Max(0, after.Healing - baseline[index].Healing),
                    "PLATING" => blockCoefficient * Math.Max(0, after.ExtraBlockUsed - baseline[index].ExtraBlockUsed),
                    "DEMISE" => -effectiveHpCoefficient * Math.Max(0, after.RemovedEnemyHp - baseline[index].RemovedEnemyHp),
                    _ => 0
                });
            }
        }

        // A projection ending with a living monster does not capture Ritual's growing tail.
        return double.IsFinite(value) && !(state.Id == "RITUAL" && baseline[^1].Truncated) ? value : null;
    }

    private static double HitChange(ExpectedMove move, int amount) =>
        move.AttackFrequency * move.Hits *
        (CombatMath.AttackDamagePerHit(move.DamagePerHit, amount) - move.DamagePerHit);

    public static IReadOnlyList<OpeningPick> Draw(
        IReadOnlyDictionary<string, IReadOnlyList<OpeningOption>> candidatesByMonster,
        IReadOnlyDictionary<string, double> budgets, OpeningDraws settings, Random random,
        Func<string, OpeningState, IReadOnlyList<OpeningPick>, double?>? revalue = null)
    {
        var selected = new List<OpeningPick>();
        for (int attempt = 0; attempt < settings.Attempts; attempt++)
        {
            double remaining = settings.MaxTotalAbsoluteValue - selected.Sum(pick => Math.Abs(pick.Value));
            if (remaining <= 0)
                break;
            var candidates = new Dictionary<string, IReadOnlyList<OpeningOption>>();
            var available = new Dictionary<string, double>();
            foreach (var (id, options) in candidatesByMonster)
            {
                var previous = selected.Where(pick => pick.MonsterId == id).ToArray();
                if (previous.Length >= settings.MaxPerMonster)
                    continue;
                available[id] = Math.Min(remaining, budgets[id] - previous.Sum(pick => Math.Abs(pick.Value)));
                if (available[id] <= 0)
                    continue;
                var states = previous.Select(pick => pick.State).ToArray();
                candidates[id] = options.Where(option => states.All(state =>
                    state.Id != option.State.Id &&
                    !((state.Id is "STRENGTH" or "STRENGTH_DOWN") &&
                        (option.State.Id is "STRENGTH" or "STRENGTH_DOWN"))))
                    .Select(option => selected.Count > 0 && revalue is not null
                        ? option with { Value = revalue(id, option.State, selected) }
                        : option).ToArray();
            }
            var choice = ChooseTarget(candidates, available, settings.Positive, settings.Negative, settings.None, random);
            if (choice is { } pick)
                selected.Add(new OpeningPick(pick.MonsterId, pick.State, pick.Value));
        }
        return selected;
    }

    public static (string MonsterId, OpeningState State, double Value)? ChooseTarget(
        IReadOnlyDictionary<string, IReadOnlyList<OpeningOption>> candidatesByMonster,
        IReadOnlyDictionary<string, double> budgets, double positiveWeight, double negativeWeight,
        double noneWeight, Random random)
    {
        double roll = random.NextDouble() * (noneWeight + positiveWeight + negativeWeight);
        if (roll < noneWeight)
        {
            return null;
        }

        bool positive = roll < noneWeight + positiveWeight;
        var eligible = candidatesByMonster.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, Options: pair.Value.Where(option => option.Value is double value &&
                Math.Abs(value) <= budgets[pair.Key] && (positive ? value > 0 : value < 0)).ToArray()))
            .Where(pair => pair.Options.Length > 0).ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }
        var target = eligible[random.Next(eligible.Length)];
        var choice = target.Options[random.Next(target.Options.Length)];
        return (target.Key, choice.State, choice.Value!.Value);
    }
}
