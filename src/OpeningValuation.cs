namespace MonsterIntentRandomizer;

public sealed record OpeningState(string Id, int Amount, EffectKind? GeneratedCard = null, bool Unique = false);
public sealed record OpeningOption(OpeningState State, double Value, double Weight = 1,
    IReadOnlyList<OpeningOption>? Doses = null, bool Stackable = false)
{
    internal Func<OpeningPick?, double, IReadOnlyList<OpeningOption>>? DoseFactory { get; init; }
    internal double MaximumCost { get; init; }
    internal bool IsThreshold { get; init; }
}
public sealed record ThresholdPrice(StatusValue State, MonsterProfile Profile, double Planned, double Scale = 1);
public sealed record OpeningPick(string MonsterId, OpeningState State, double Value);
public sealed record OpeningDraws(double None, double Positive, double Negative, int Attempts,
    int MaxPerMonster, double MaxEncounterValueMultiplier, double Strength = 1, int SpecialStatusLimit = 1);
public sealed record StatusValue(Dictionary<string, double> Formula, int Amount = 1, double Weight = 1,
    double WeightPerRoom = 0, int MinAmount = 1, bool Common = false, bool Stackable = false, bool Unique = false,
    Dictionary<EffectKind, double>? GeneratedCards = null, double LaterMonsterPercent = 0,
    double? MaxCostMultiplier = null)
{
    internal ActionValuation CompiledFormula { get; } = new(new Dictionary<EffectKind, Dictionary<string, double>>
        { [EffectKind.Attack] = Formula ?? throw new ArgumentException("开场状态和共用强化须配置估值公式。") });
    internal RoomWeight RoomWeight => new(Weight, WeightPerRoom);

    internal double MaximumCost(MonsterProfile profile) => profile.ActionValue * (MaxCostMultiplier ?? 1);
}

public static class OpeningValuation
{
    public static double Evaluate(StatusValue state, MonsterProfile profile, int amount) =>
        amount == 0 ? 0 : state.CompiledFormula.Evaluate(EffectKind.Attack, profile, Math.Abs(amount),
            effectiveAmount: Math.Abs(amount)) * profile.LaterMonsterMultiplier(state.LaterMonsterPercent);

    public static double ThresholdCost(StatusValue state, MonsterProfile profile, int before, int after) =>
        after <= 0 || (before > 0 && after >= before) ? 0 :
            Math.Max(0, Evaluate(state, profile, after) - Evaluate(state, profile, before));

    public static OpeningOption Option(string id, StatusValue state, MonsterProfile profile, int rooms = 0,
        double strength = 1, double chaosProgress = 0, int? previousThreshold = null,
        ActionValuationSettings? actions = null, EffectKind? existingCard = null, string? monsterId = null)
    {
        EffectPolicy.TryGetKind(id, out var kind);
        bool threshold = previousThreshold.HasValue;
        bool stackable = state.Stackable || TriggeredCardBuffs.UsesNative(kind, monsterId);
        double weight = state.RoomWeight.At(rooms, chaosProgress);
        double limit = state.MaximumCost(profile) * strength;
        return new OpeningOption(new OpeningState(id, 0, Unique: state.Unique), 0, weight,
            Quote(null, limit), Stackable: stackable)
        {
            DoseFactory = Quote,
            MaximumCost = limit,
            IsThreshold = threshold
        };

        IReadOnlyList<OpeningOption> Quote(OpeningPick? earlier, double target)
        {
            int before = earlier?.State.Amount ?? previousThreshold.GetValueOrDefault();
            if (!stackable && (earlier != null || before != 0))
                return [];

            int lastImprovement = threshold && before > 0 ? before - 1 : int.MaxValue;
            int sign = Math.Sign(state.Amount);
            var variants = actions == null ? [(Card: (EffectKind?)null, Weight: 1.0)] :
                TriggeredCardBuffs.Variants(kind, state, actions, rooms, earlier?.State.GeneratedCard ?? existingCard, monsterId);
            var choices = new List<OpeningOption>();
            foreach (var variant in variants)
            {
                double scale = actions == null ? 1 : TriggeredCardBuffs.Scale(kind, variant.Card, actions, profile);
                double Value(int amount) => scale * (threshold
                    ? ThresholdCost(state, profile, before, amount) : Evaluate(state, profile, amount));
                int amount = SpendAmount.Closest(state.MinAmount, lastImprovement, target, limit,
                    count => Math.Abs(Value(count)), descending: threshold);
                if (amount != 0)
                    choices.Add(new OpeningOption(new OpeningState(id, amount * sign, variant.Card, Unique: state.Unique),
                        Value(amount), variant.Weight));
            }
            return choices;
        }
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
            int uniqueSpent = selected.Count(pick => pick.State.Unique);
            var available = candidates.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var previous = selected.Where(pick => pick.MonsterId == pair.Key).ToArray();
                    var options = pair.Value.Select(option => RefreshDoses(option, previous))
                        .Where(option => (previous.Length < settings.MaxPerMonster || option.IsThreshold &&
                            previous.Any(pick => pick.State.Id == option.State.Id)) && option.Weight > 0 &&
                        (!option.State.Unique || uniqueSpent < settings.SpecialStatusLimit) &&
                        (option.Doses?.Any(dose => positive ? dose.Value > 0 : dose.Value < 0) ??
                            (positive ? option.Value > 0 : option.Value < 0)) &&
                        previous.All(pick => !Conflicts(pick.State.Id, option.State.Id, option.Stackable))).ToArray();
                    return (pair.Key, Options: options);
                }).Where(pair => pair.Options.Length > 0).ToArray();
            if (available.Length == 0)
                continue;
            var (id, optionsForMonster) = available[random.Next(available.Length)];
            var chosen = ActionPlanner.Draw(optionsForMonster.Select(option => (option, option.Weight)), 0, random);
            if (chosen == null)
                continue;
            bool threshold = chosen.IsThreshold;
            if (chosen.Doses is { } doses)
            {
                double baseBudget = Math.Max(0, actionValues.GetValueOrDefault(id));
                double budget = baseBudget * settings.Strength;
                if (chosen.DoseFactory is { } quote)
                {
                    budget = chosen.MaximumCost * random.NextDouble();
                    var previous = selected.LastOrDefault(pick => pick.MonsterId == id && pick.State.Id == chosen.State.Id);
                    doses = quote(previous, budget);
                }
                var ranked = doses.Where(dose => positive ? dose.Value > 0 : dose.Value < 0)
                    .Select(dose => (Choice: dose, Weight: dose.Weight *
                        ActionPlanner.MatchingWeight(Math.Abs(dose.Value), budget, Math.Max(1, baseBudget))))
                    .ToArray();
                if (ranked.Length == 0)
                    continue;
                chosen = ActionPlanner.Draw(ranked, 0, random) ?? ranked[^1].Choice;
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

    private static OpeningOption RefreshDoses(OpeningOption option, IReadOnlyList<OpeningPick> previous)
    {
        if (option.DoseFactory is not { } quote)
            return option;
        var applied = previous.LastOrDefault(pick => pick.State.Id == option.State.Id);
        return applied == null ? option : option with { Doses = quote(applied, option.MaximumCost) };
    }

    private static bool Conflicts(string existing, string added, bool stackable) =>
        existing == added ? !stackable :
        ((existing is "THORNS" or "TEMPORARY_THORNS") && (added is "THORNS" or "TEMPORARY_THORNS")) ||
        ((existing is "STRENGTH" or "STRENGTH_DOWN") && (added is "STRENGTH" or "STRENGTH_DOWN"));
}
