namespace MonsterIntentRandomizer;

public sealed record ExpectedMove(double Value, double AttackFrequency, double DamagePerHit, double Hits);
public sealed record OpeningOption(OpeningState State, double? Value);

public static class OpeningValuation
{
    public static double? Evaluate(
        OpeningState state, IReadOnlyList<ForecastWindow> baseline,
        IReadOnlyList<ForecastWindow> modified, IReadOnlyList<ExpectedMove> moves, double discount,
        double artifactValue = 0, double artifactChancePerTargetedWindow = 0)
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
                    (CombatMath.AttackDamagePerHit(move.DamagePerHit, weak: true) - move.DamagePerHit) *
                    before.AliveAtAction,
                "WEAK" => 0,
                "RITUAL" => HitChange(move, state.Amount * index) * before.AliveAtAction,
                "THORNS" => state.Amount * before.IncomingAttackHits,
                "REGEN" or "PLATING" or "DOOM" or "DEMISE" or "VULNERABLE" or "STUN" => gainedActionValue,
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
        if (!changedActions)
        {
            // When both projections survive the same actions, use effective HP as a proxy.
            for (int index = 0; index < modified.Count; index++)
            {
                ForecastWindow after = modified[index];
                value += Math.Pow(discount, index) * 0.4 * (state.Id switch
                {
                    "REGEN" => after.Healing,
                    "PLATING" => after.ExtraBlockUsed,
                    "DEMISE" => -after.RemovedEnemyHp,
                    "VULNERABLE" => -Math.Max(0, after.IncomingAttackDamage - baseline[index].IncomingAttackDamage),
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

    public static (string MonsterId, OpeningState State)? ChooseTarget(
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
        return (target.Key, target.Options[random.Next(target.Options.Length)].State);
    }
}
