namespace MonsterIntentRandomizer;

internal static class PrototypeChecks
{
    public static void Run()
    {
        var scenario = new[] { new TargetingScenario(1, 1, 1) };
        var target = new EnemySnapshot("test", 8, 8, 0, 0);
        var attack = new DamageEstimate(3, 0, 1);
        var expectedMoves = new[] { new ExpectedMove(7, 1, 7, 1) };
        var baseline = SurvivalForecast.Predict([target], target.Id, attack, scenario, 4);
        Equal(2, baseline.Sum(window => window.AliveAtAction), "baseline actions");
        var guarded = SurvivalForecast.Predict([target, new EnemySnapshot("ally", 9, 9, 0, 1)],
            target.Id, attack, scenario, 8);
        Equal(5, guarded.Sum(window => window.AliveAtAction), "ally diverts focus");
        var late = SurvivalForecast.Predict([target with { FirstEnemyAction = 2 }], target.Id, attack,
            scenario, 4, firstPlayerWindowFraction: 0);
        Equal(0, late[0].AliveAtAction, "no action on spawn round");
        Equal(1, late[1].AliveAtAction, "late spawn survives next window");

        CheckOpening("DEMISE", 1, -0.72);
        CheckOpening("DEMISE", 2, -5.6);
        CheckOpening("DOOM", 5, -5.6);
        CheckOpening("DOOM", 1, 0);
        CheckOpening("STUN", 1, -7);
        CheckOpening("WEAK", 1, -2);
        CheckOpening("RITUAL", 1, 0.8);
        CheckOpening("REGEN", 1, 0.4);
        // The first two points of Block let this monster survive to act a third time.
        CheckOpening("PLATING", 1, 4.48);
        CheckOpening("ARTIFACT", 1, 2, artifactValue: 2, artifactChance: 1);
        Equal(1.5, SurvivalForecast.FromExpectedSurvival(1.5, 4).Sum(window => window.AliveAtAction),
            "profile survival fallback");
        try
        {
            SurvivalForecast.Predict([target with { FixedOpening = true }], target.Id, attack, scenario, 4,
                opening: new OpeningState("STUN", 1));
            throw new InvalidOperationException("Stun should be rejected for a fixed opening.");
        }
        catch (ArgumentException error) when (error.ParamName == "opening")
        {
        }

        var variant = new OpeningState("DEMISE", 1);
        var onlyNegatives = new Dictionary<string, IReadOnlyList<OpeningOption>>
        {
            [target.Id] = [new OpeningOption(variant, -1), new OpeningOption(new OpeningState("STUN", 1), -7)]
        };
        var budgets = new Dictionary<string, double> { [target.Id] = 2 };
        if (OpeningValuation.ChooseTarget(onlyNegatives, budgets, 100, 0, 0, new Random(1)) is not null ||
            OpeningValuation.ChooseTarget(onlyNegatives, budgets, 0, 100, 0, new Random(1))?.State != variant)
        {
            throw new InvalidOperationException("Opening selection violated polarity or budget.");
        }

        var planner = new ActionPlanner(new PoolSettings(0.75, 0.9, 1.6, 2));
        var profile = new MonsterProfile(8, 0.15, 0.67, 0.35, 2,
            new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 1, [IntentStyle.Defend] = 1 });
        var random = new Random(2026);
        Equal(3, planner.Plan(new Pools(4, 1), profile, [], 6, random, canRandomize: false).After.ActionValue,
            "skipped turns only decay");
        var choices = new[]
        {
            new ActionCandidate("attack", IntentStyle.Attack, true, 6, 6),
            new ActionCandidate("defend", IntentStyle.Defend, false, 2, 0)
        };
        var capped = planner.Plan(new Pools(5, 2), profile, choices, 5, random);
        if (capped.Action?.Id != "defend")
            throw new InvalidOperationException("Damage cap did not remove the attack.");
        var weakened = ActionCandidate.Attack("multi", 3, 3, 0, weak: true, 1);
        Equal(9, weakened.Value, "weak does not refill pool spending");
        Equal(6, weakened.Damage, "weak changes the damage cap check");
        double attacks = 0;
        Pools pools = default;
        for (int i = 0; i < 2000; i++)
        {
            var plan = planner.Plan(pools, profile, choices, 6, random);
            if (plan.Action is null || plan.After.ActionValue < 0 || plan.Action.Value > plan.Granted.ActionValue)
                throw new InvalidOperationException("Pool overspent or failed to select from an affordable catalog.");
            pools = plan.After;
            attacks += plan.Action.IsAttack ? 1 : 0;
        }
        if (Math.Abs(attacks / 2000 - profile.AttackFrequency) > 0.06)
            throw new InvalidOperationException($"Unexpected long-run attack frequency: {attacks / 2000:P1}");
        Console.WriteLine($"Prototype checks passed. Attack sample: {attacks / 2000:P1}.");

        void CheckOpening(string id, int amount, double expected, double artifactValue = 0, double artifactChance = 0)
        {
            var state = new OpeningState(id, amount);
            var modified = SurvivalForecast.Predict([target], target.Id, attack, scenario, 4, opening: state);
            Equal(expected, OpeningValuation.Evaluate(state, baseline, modified, expectedMoves,
                0.8, artifactValue, artifactChance) ?? double.NaN, id);
        }
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}
