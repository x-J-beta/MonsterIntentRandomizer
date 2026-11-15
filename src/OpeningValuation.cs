namespace MonsterIntentRandomizer;

public sealed record OpeningOption(OpeningState State, double Value, double Weight = 1,
    IReadOnlyList<OpeningOption>? Doses = null, bool Stackable = false, bool Unique = false,
    int? PreviousThreshold = null, double PreviousValue = 0);
public sealed record ThresholdPrice(StatusValue State, MonsterProfile Profile, double Planned);
public sealed record OpeningPick(string MonsterId, OpeningState State, double Value);
public sealed record OpeningDraws(double None, double Positive, double Negative, int Attempts,
    int MaxPerMonster, double MaxEncounterValueMultiplier, double Strength = 1);
public sealed record StatusValue(Dictionary<string, double> Formula, int Amount = 1, double Weight = 1,
    int? MaxAmount = null, double WeightPerRoom = 0, int MinAmount = 1,
    double? HpPerSurvivalTurnCap = null, bool Common = false, bool Stackable = false, bool Unique = false)
{
    internal ActionValuation CompiledFormula { get; } = new(new Dictionary<EffectKind, Dictionary<string, double>>
        { [EffectKind.Attack] = Formula ?? throw new ArgumentException("开场状态和共用强化须配置估值公式。") });
    internal RoomWeight RoomWeight => new(Weight, WeightPerRoom);

    internal IEnumerable<int> Doses(MonsterProfile profile, double strength = 1)
    {
        int configured = MaxAmount ?? (HpPerSurvivalTurnCap.HasValue ? int.MaxValue : Math.Abs(Amount));
        int maximum = profile.CapAmount(configured, HpPerSurvivalTurnCap);
        if (maximum > 1)
            maximum = (int)Math.Min(int.MaxValue, Math.Floor(maximum * strength));
        return Enumerable.Range(MinAmount, Math.Max(0, maximum - MinAmount + 1)).Select(level => level * Math.Sign(Amount));
    }
}

public static class OpeningValuation
{
    public static double Evaluate(StatusValue state, MonsterProfile profile, int amount) =>
        amount == 0 ? 0 : state.CompiledFormula.Evaluate(EffectKind.Attack, profile, Math.Abs(amount), effectiveAmount: Math.Abs(amount));

    public static double ThresholdCost(StatusValue state, MonsterProfile profile, int before, int after) =>
        after <= 0 || (before > 0 && after >= before) ? 0 :
            Math.Max(0, Evaluate(state, profile, after) - Evaluate(state, profile, before));

    public static OpeningOption Option(string id, StatusValue state, MonsterProfile profile, int rooms = 0,
        double strength = 1, double chaosProgress = 0, int? previousThreshold = null)
    {
        var doses = state.Doses(profile, strength).Select(amount =>
            new OpeningOption(new OpeningState(id, amount), Evaluate(state, profile, amount))).ToArray();
        var representative = doses.FirstOrDefault(dose => dose.Value != 0) ??
            new OpeningOption(new OpeningState(id, 0), 0);
        return representative with { Weight = state.RoomWeight.At(rooms, chaosProgress), Doses = doses,
            Stackable = state.Stackable, Unique = state.Unique, PreviousThreshold = previousThreshold,
            PreviousValue = Evaluate(state, profile, previousThreshold ?? 0) };
    }

    public static IReadOnlyList<OpeningPick> Draw(
        IReadOnlyDictionary<string, IReadOnlyList<OpeningOption>> candidates,
        IReadOnlyDictionary<string, double> actionValues, OpeningDraws settings, Random random)
    {
        double totalWeight = settings.None + settings.Positive + settings.Negative;
        if (totalWeight <= 0)
            return [];

        double limit = actionValues.Values.Sum() * settings.MaxEncounterValueMultiplier * settings.Strength;
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
                    var options = pair.Value.Select(option => PriceThreshold(option, previous))
                        .Where(option => (previous.Length < settings.MaxPerMonster || option.PreviousThreshold.HasValue &&
                            previous.Any(pick => pick.State.Id == option.State.Id)) && option.Weight > 0 &&
                        (option.Doses?.Any(dose => positive ? dose.Value > 0 : dose.Value < 0) ??
                            (positive ? option.Value > 0 : option.Value < 0)) &&
                        previous.All(pick => !Conflicts(pick.State.Id, option.State.Id, option.Stackable)) &&
                        (!option.Unique || selected.All(pick => pick.State.Id != option.State.Id ||
                            option.PreviousThreshold.HasValue && option.Stackable && pick.MonsterId == pair.Key))).ToArray();
                    return (pair.Key, Options: options);
                }).Where(pair => pair.Options.Length > 0).ToArray();
            if (available.Length == 0)
                continue;
            var (id, optionsForMonster) = available[random.Next(available.Length)];
            double weightRoll = random.NextDouble() * Math.Max(1, optionsForMonster.Sum(option => option.Weight));
            var chosen = optionsForMonster.FirstOrDefault(option => (weightRoll -= option.Weight) < 0);
            if (chosen == null)
                continue;
            bool threshold = chosen.PreviousThreshold.HasValue;
            if (chosen.Doses is { } doses)
            {
                double baseBudget = Math.Max(0, actionValues.GetValueOrDefault(id));
                double budget = baseBudget * settings.Strength;
                var ranked = doses.Where(dose => positive ? dose.Value > 0 : dose.Value < 0)
                    .Select(dose => (Dose: dose, Weight: ActionPlanner.MatchingWeight(Math.Abs(dose.Value), budget, Math.Max(1, baseBudget))))
                    .ToArray();
                double doseRoll = random.NextDouble() * ranked.Sum(dose => dose.Weight);
                foreach (var dose in ranked)
                {
                    chosen = dose.Dose;
                    if ((doseRoll -= dose.Weight) < 0)
                        break;
                }
            }
            int earlier = threshold ? selected.FindIndex(pick => pick.MonsterId == id && pick.State.Id == chosen.State.Id) : -1;
            if (earlier >= 0)
                selected[earlier] = new OpeningPick(id, chosen.State, selected[earlier].Value + chosen.Value);
            else
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

    private static OpeningOption PriceThreshold(OpeningOption option, IReadOnlyList<OpeningPick> previous)
    {
        if (option.PreviousThreshold is not int current || option.Doses is not { } doses)
            return option;
        var upgraded = previous.FirstOrDefault(pick => pick.State.Id == option.State.Id);
        double alreadyPaid = option.PreviousValue + (upgraded?.Value ?? 0);
        int threshold = upgraded?.State.Amount ?? current;
        return option with
        {
            Doses = doses.Where(dose => threshold == 0 || option.Stackable && dose.State.Amount < threshold)
                .Select(dose => dose with { Value = dose.Value - alreadyPaid }).Where(dose => dose.Value > 0).ToArray()
        };
    }

    private static bool Conflicts(string existing, string added, bool stackable) =>
        existing == added ? !stackable :
        ((existing is "THORNS" or "TEMPORARY_THORNS") && (added is "THORNS" or "TEMPORARY_THORNS")) ||
        ((existing is "STRENGTH" or "STRENGTH_DOWN") && (added is "STRENGTH" or "STRENGTH_DOWN"));
}
