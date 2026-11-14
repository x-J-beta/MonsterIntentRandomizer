namespace MonsterIntentRandomizer;

public sealed record OpeningOption(OpeningState State, double Value);
public sealed record OpeningPick(string MonsterId, OpeningState State, double Value);
public sealed record OpeningDraws(double None, double Positive, double Negative, int Attempts,
    int MaxPerMonster, double MaxEncounterValueMultiplier);
public sealed record StatusValue(int Amount, double Turns);

public static class OpeningValuation
{
    public static double Evaluate(StatusValue state, double actionValue) =>
        Math.Abs(state.Amount) * state.Turns * actionValue;

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
                    var options = pair.Value.Where(option => previous.Length < settings.MaxPerMonster &&
                        (positive ? option.Value > 0 : option.Value < 0) &&
                        previous.All(pick => !Conflicts(pick.State.Id, option.State.Id))).ToArray();
                    return (pair.Key, Options: options);
                }).Where(pair => pair.Options.Length > 0).ToArray();
            if (available.Length == 0)
                continue;
            var (id, optionsForMonster) = available[random.Next(available.Length)];
            var chosen = optionsForMonster[random.Next(optionsForMonster.Length)];
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
