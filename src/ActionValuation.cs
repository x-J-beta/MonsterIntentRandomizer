namespace MonsterIntentRandomizer;

public sealed record PlayerTurnValues(double Act1 = 10, double Act2 = 17, double Act3 = 25)
{
    public double ForAct(int act) => act switch { <= 1 => Act1, 2 => Act2, _ => Act3 };
}

public sealed class ActionValuation
{
    private enum Factor
    {
        Amount, ActionValue, AttackFrequency, SurvivalTurns, EffectiveAmount, Hits, Draws, Triggers,
        PlayerTurnValue, PlayerActionValue, HpPerSurvivalTurn, InverseAmount
    }

    internal static IReadOnlyList<string> FactorNames { get; } = Enum.GetNames<Factor>();

    private sealed record Term(double Coefficient, Factor[] Factors);

    private readonly Dictionary<EffectKind, Term[]> formulas;
    private readonly PlayerTurnValues playerTurns;

    public ActionValuation(IReadOnlyDictionary<EffectKind, Dictionary<string, double>> coefficients,
        PlayerTurnValues? playerTurns = null)
    {
        this.playerTurns = playerTurns ?? new PlayerTurnValues();
        formulas = coefficients.ToDictionary(pair => pair.Key, pair => pair.Value.Select(term =>
        {
            var factors = term.Key.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? Array.Empty<Factor>() :
                term.Key.Split('*', StringSplitOptions.TrimEntries).Select(ParseFactor).ToArray();
            return new Term(term.Value, factors);
        }).ToArray());
    }

    public double Evaluate(EffectKind kind, MonsterProfile profile, int amount, int hits = 1,
        double effectiveAmount = 0, double draws = 0, double triggers = 0, int act = 1, double? playerTurnValue = null)
    {
        if (!formulas.TryGetValue(kind, out var terms))
            return 0;

        double total = 0;
        foreach (var term in terms)
        {
            double value = term.Coefficient;
            foreach (var factor in term.Factors)
            {
                value *= factor switch
                {
                    Factor.Amount => amount,
                    Factor.ActionValue => profile.ActionValue,
                    Factor.AttackFrequency => profile.AttackFrequency,
                    Factor.SurvivalTurns => profile.BaseExpectedSurvivalTurns,
                    Factor.EffectiveAmount => effectiveAmount,
                    Factor.Hits => hits,
                    Factor.Draws => draws,
                    Factor.Triggers => triggers,
                    Factor.PlayerTurnValue => playerTurnValue ?? playerTurns.ForAct(act),
                    Factor.PlayerActionValue => profile.PlayerActionValue,
                    Factor.HpPerSurvivalTurn => profile.HpPerSurvivalTurn,
                    Factor.InverseAmount => amount > 0 ? 1.0 / amount : 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(factor))
                };
            }
            total += value;
        }
        return total;
    }

    private static Factor ParseFactor(string name) =>
        Enum.TryParse<Factor>(name, ignoreCase: true, out var factor) &&
        name.Equals(factor.ToString(), StringComparison.OrdinalIgnoreCase)
            ? factor : throw new ArgumentException($"Unknown action valuation factor: '{name}'.");
}
