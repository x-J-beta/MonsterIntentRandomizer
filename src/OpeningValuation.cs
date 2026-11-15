namespace MonsterIntentRandomizer;

public sealed record OpeningOption(OpeningState State, double Value, double Weight = 1,
    IReadOnlyList<OpeningOption>? Doses = null);
public sealed record OpeningPick(string MonsterId, OpeningState State, double Value);
public sealed record OpeningDraws(double None, double Positive, double Negative, int Attempts,
    int MaxPerMonster, double MaxEncounterValueMultiplier);
public enum OpeningBasis { ActionValue, Flat, SurvivalTurns, AttackFrequency, AttacksOverLifetime }

public sealed record StatusValue(int Amount, double Turns, int[]? Amounts = null, double Weight = 1,
    int MinAct = 1, OpeningBasis Basis = OpeningBasis.ActionValue);

public static class OpeningValuation
{
    public static bool IsEncounterUnique(string id) => id is "VITAL_SPARK" or "WITHERING_PRESENCE";

    public static double Evaluate(StatusValue state, MonsterProfile profile, int amount) =>
        Math.Abs((double)amount) * state.Turns * (state.Basis switch
        {
            OpeningBasis.Flat => 1,
            OpeningBasis.SurvivalTurns => profile.BaseExpectedSurvivalTurns,
            OpeningBasis.AttackFrequency => profile.AttackFrequency,
            OpeningBasis.AttacksOverLifetime => profile.AttackFrequency * profile.BaseExpectedSurvivalTurns,
            _ => profile.ActionValue
        });

    public static OpeningOption Option(string id, StatusValue state, MonsterProfile profile)
    {
        var doses = (state.Amounts ?? [state.Amount]).Distinct().Select(amount =>
            new OpeningOption(new OpeningState(id, amount), Evaluate(state, profile, amount))).ToArray();
        return doses[0] with { Weight = state.Weight, Doses = doses };
    }

    public static IReadOnlyList<OpeningPick> Draw(
        IReadOnlyDictionary<string, IReadOnlyList<OpeningOption>> candidates,
        IReadOnlyDictionary<string, double> actionValues, OpeningDraws settings, Random random)
    {
        double totalWeight = settings.None + settings.Positive + settings.Negative;
        if (totalWeight <= 0)
            return [];

        double limit = actionValues.Values.Sum() * settings.MaxEncounterValueMultiplier;
        var selected = new List<OpeningPick>();
        double total = 0;
        for (int attempt = 0; attempt < settings.Attempts; attempt++)
        {
            double roll = random.NextDouble() * totalWeight;
            if (roll < settings.None)
                continue;

            bool positive = roll < settings.None + settings.Positive;
            var available = candidates.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var previous = selected.Where(pick => pick.MonsterId == pair.Key).ToArray();
                    var options = pair.Value.Where(option => previous.Length < settings.MaxPerMonster && option.Weight > 0 &&
                        (positive ? option.Value > 0 : option.Value < 0) &&
                        previous.All(pick => !Conflicts(pick.State.Id, option.State.Id)) &&
                        (!IsEncounterUnique(option.State.Id) || selected.All(pick => pick.State.Id != option.State.Id))).ToArray();
                    return (pair.Key, Options: options);
                }).Where(pair => pair.Options.Length > 0).ToArray();
            if (available.Length == 0)
                continue;
            var (id, optionsForMonster) = available[random.Next(available.Length)];
            double weightRoll = random.NextDouble() * Math.Max(1, optionsForMonster.Sum(option => option.Weight));
            var chosen = optionsForMonster.FirstOrDefault(option => (weightRoll -= option.Weight) < 0);
            if (chosen == null)
                continue;
            if (chosen.Doses is { } doses)
                chosen = doses[random.Next(doses.Count)];
            selected.Add(new OpeningPick(id, chosen.State, chosen.Value));
            total += chosen.Value;
        }
        while (Math.Abs(total) > limit + 1e-9 && selected.Count > 0)
        {
            // Keep pairs that offset each other. Trim only after all draws are complete.
            int remove = Enumerable.Range(0, selected.Count)
                .MinBy(index => Math.Abs(total - selected[index].Value));
            total -= selected[remove].Value;
            selected.RemoveAt(remove);
        }
        return selected;
    }

    private static bool Conflicts(string existing, string added) =>
        existing == added ||
        ((existing is "STRENGTH" or "STRENGTH_DOWN") && (added is "STRENGTH" or "STRENGTH_DOWN"));
}
