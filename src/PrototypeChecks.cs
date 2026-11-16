namespace MonsterIntentRandomizer;

internal static class PrototypeChecks
{
    public static void Run()
    {
        var profile = new MonsterProfile(8, 0.15, 0.67, 0.35, 2,
            new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 1, [IntentStyle.Defend] = 1 });
        CheckPools(profile);
        CheckWeights();
        CheckOpening(profile);
        CheckCurse(profile);
        CheckAnimationHits();
        Console.WriteLine("Prototype checks passed.");
    }

    private static void CheckPools(MonsterProfile profile)
    {
        var planner = new ActionPlanner(new PoolSettings(0.75, 0.9));
        var previous = new Pools(4, 1);
        var random = new Random(2026);
        if (planner.Plan(previous, profile, [], random, canRandomize: false).After != previous)
            throw new InvalidOperationException("A preserved move changed the pools.");

        Equal(6, planner.Start(profile, -2).ActionValue, "negative opening credited in full");
        Equal(-1, planner.Settle(new Pools(1, 0), 2, 0).ActionValue, "overspending puts the pool into debt");
        Equal(1, planner.Settle(new Pools(12, 4), 9, 3).AttackDesire, "each attack hit spends one desire");

        var weakened = ActionCandidate.Attack("multi", 3, 3, 0, weak: true, 1);
        Equal(9, weakened.Value, "weak preserves attack cost");
        Equal(6, weakened.Damage, "weak changes actual damage");
        var negative = planner.Plan(new Pools(0, -2), profile, [weakened], random);
        Equal(0, negative.Action!.AttackHits, "negative attack desire prevents attacks");
    }

    private static void CheckWeights()
    {
        var slow = new RoomWeight(-0.1, 0.003);
        Equal(0, slow.At(32, 0.5), "moderate chaos keeps the slow weight locked");
        Equal(Math.Sqrt(2.8) - 1.05, new RoomWeight(-0.8, 0.05).At(20, 0.5),
            "moderate chaos advances steeper weights");
        Equal(1, slow.At(90, 1), "full chaos sets enabled weights to one");
        Equal(0, new RoomWeight(RoomWeight.DisabledBase, 0.02).At(90, 1), "the disabled sentinel stays disabled");
        Equal(2, (new PoolSettings(0.75, 0.9, Chaos: 0.67)).SpecialStatusLimit,
            "the second unique allowance unlocks at 0.67");
    }

    private static void CheckOpening(MonsterProfile profile)
    {
        var options = new Dictionary<string, IReadOnlyList<OpeningOption>>
        {
            ["first"] = [new(new OpeningState("STRENGTH", 1), 2)],
            ["second"] = [new(new OpeningState("WEAK", 1), -2)]
        };
        var actionValues = new Dictionary<string, double> { ["first"] = 3, ["second"] = 3 };
        var draws = new OpeningDraws(0, 1, 1, 12, 2, 0.25);
        var paired = OpeningValuation.Draw(options, actionValues, draws, new Random(11));
        Equal(2, paired.Count, "opposite opening values fit the net cap together");
        Equal(0, paired.Sum(pick => pick.Value), "opening values offset each other");

        var unique = new OpeningOption(new OpeningState("ENVENOM", 1, Unique: true), 2);
        options["first"] = options["second"] = [unique];
        var positives = draws with { Negative = 0, MaxEncounterValueMultiplier = 1 };
        Equal(1, OpeningValuation.Draw(options, actionValues, positives, new Random(11)).Count,
            "unique effects share an encounter allowance");
        Equal(2, OpeningValuation.Draw(options, actionValues, positives with { SpecialStatusLimit = 2 }, new Random(11)).Count,
            "two targets can receive the same nonstackable unique effect");

        var threshold = new StatusValue(new Dictionary<string, double> { ["survivalTurns*inverseAmount"] = 18 });
        Equal(6, OpeningValuation.ThresholdCost(threshold, profile, 6, 3), "a threshold upgrade charges the difference");
    }

    private static void CheckCurse(MonsterProfile profile)
    {
        var valuation = new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>>
        {
            [EffectKind.Debt] = new() { ["fixed"] = 10 }
        });
        var settings = new ActionValuationSettings(valuation, 0.25, 1,
            new Dictionary<EffectKind, int[]> { [EffectKind.Debt] = [1] },
            Policies: new Dictionary<EffectKind, EffectPolicy> { [EffectKind.Debt] = new(Unique: true) });
        var catalog = new ActionCatalog(settings);
        var context = new ActionContext(10);
        var curse = catalog.Generate(profile, context).Single();
        Equal(10, curse.Value, "curse value ignores ordinary card placement modifiers");
        if (curse.Effects is not [{ Unique: true, Pile: StatusPile.Hand }])
            throw new InvalidOperationException("Debt should be inserted into the hand as a unique effect.");

        var spent = context with { SpecialStatusesRemaining = 0 };
        if (catalog.Generate(profile, spent).Any())
            throw new InvalidOperationException("A unique effect was offered after the allowance was spent.");
        Equal(1, new ActionCatalog(settings with { Policies = null }).Generate(profile, spent).Count(),
            "turning off unique releases the effect from the allowance");
    }

    private static void CheckAnimationHits()
    {
        var hits = CombatMath.DrawAttackAnimationHits(27, new Random(11))!;
        if (hits.Length != 9 || hits.Any(hit => hit is < 0 or >= 27) ||
            !hits.SequenceEqual(hits.Distinct().Order()))
            throw new InvalidOperationException("Attack animations must occupy distinct hits in attack order.");
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}
