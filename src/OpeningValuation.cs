namespace MonsterIntentRandomizer;

public sealed record OpeningOption(OpeningState State, double Value, double Weight = 1,
    IReadOnlyList<OpeningOption>? Doses = null, bool Stackable = false, bool Unique = false);
public sealed record OpeningPick(string MonsterId, OpeningState State, double Value);
public sealed record OpeningDraws(double None, double Positive, double Negative, int Attempts,
    int MaxPerMonster, double MaxEncounterValueMultiplier);
public enum OpeningBasis { ActionValue, Flat, SurvivalTurns, AttackFrequency, AttacksOverLifetime }

public sealed record StatusValue(int Amount = 1, double Turns = 0, int[]? Amounts = null, double Weight = 1,
    int MinAct = 1, OpeningBasis Basis = OpeningBasis.ActionValue, int? MaxAmount = null,
    double WeightPerRoom = 0, Dictionary<string, double>? Formula = null, int MinAmount = 1,
    double? HpPerSurvivalTurnCap = null, bool Common = false, bool Stackable = false, bool Unique = false)
{
    internal ActionValuation? CompiledFormula { get; } = Formula == null ? null :
        new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>> { [EffectKind.Attack] = Formula });

    internal IEnumerable<int> Doses(MonsterProfile profile)
    {
        int configured = MaxAmount ?? (HpPerSurvivalTurnCap.HasValue ? int.MaxValue :
            Amounts?.Select(Math.Abs).DefaultIfEmpty(0).Max() ?? Math.Abs(Amount));
        int maximum = profile.CapAmount(configured, HpPerSurvivalTurnCap);
        return Enumerable.Range(MinAmount, Math.Max(0, maximum - MinAmount + 1)).Select(level => level * Math.Sign(Amount));
    }
}

public static class OpeningValuation
{
    public static double Evaluate(StatusValue state, MonsterProfile profile, int amount) =>
        amount == 0 ? 0 : state.CompiledFormula?.Evaluate(EffectKind.Attack, profile, Math.Abs(amount)) ??
        Math.Abs((double)amount) * state.Turns * (state.Basis switch
        {
            OpeningBasis.Flat => 1,
            OpeningBasis.SurvivalTurns => profile.BaseExpectedSurvivalTurns,
            OpeningBasis.AttackFrequency => profile.AttackFrequency,
            OpeningBasis.AttacksOverLifetime => profile.AttackFrequency * profile.BaseExpectedSurvivalTurns,
            _ => profile.ActionValue
        });

    public static OpeningOption Option(string id, StatusValue state, MonsterProfile profile, int rooms = 0)
    {
        var doses = state.Doses(profile).Select(amount =>
            new OpeningOption(new OpeningState(id, amount), Evaluate(state, profile, amount))).ToArray();
        var representative = doses.FirstOrDefault(dose => dose.Value != 0) ??
            new OpeningOption(new OpeningState(id, 0), 0);
        return representative with { Weight = new RoomWeight(state.Weight, state.WeightPerRoom).At(rooms), Doses = doses,
            Stackable = state.Stackable, Unique = state.Unique };
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
                        (option.Doses?.Any(dose => positive ? dose.Value > 0 : dose.Value < 0) ??
                            (positive ? option.Value > 0 : option.Value < 0)) &&
                        previous.All(pick => !Conflicts(pick.State.Id, option.State.Id, option.Stackable)) &&
                        (!option.Unique || selected.All(pick => pick.State.Id != option.State.Id))).ToArray();
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
            {
                double budget = Math.Max(0, actionValues.GetValueOrDefault(id));
                var ranked = doses.Where(dose => positive ? dose.Value > 0 : dose.Value < 0)
                    .Select(dose => (Dose: dose, Weight: ActionPlanner.MatchingWeight(Math.Abs(dose.Value), budget, Math.Max(1, budget))))
                    .ToArray();
                double doseRoll = random.NextDouble() * ranked.Sum(dose => dose.Weight);
                foreach (var dose in ranked)
                {
                    chosen = dose.Dose;
                    if ((doseRoll -= dose.Weight) < 0)
                        break;
                }
            }
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

    private static bool Conflicts(string existing, string added, bool stackable) =>
        existing == added ? !stackable :
        ((existing is "THORNS" or "TEMPORARY_THORNS") && (added is "THORNS" or "TEMPORARY_THORNS")) ||
        ((existing is "STRENGTH" or "STRENGTH_DOWN") && (added is "STRENGTH" or "STRENGTH_DOWN")) ||
        ((existing is "DEXTERITY" or "DEXTERITY_DOWN") && (added is "DEXTERITY" or "DEXTERITY_DOWN"));
}
