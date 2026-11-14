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
        Equal(3, planner.Plan(new Pools(4, 1), profile, [], random, canRandomize: false).After.ActionValue,
            "skipped turns only decay");
        var choices = new[]
        {
            new ActionCandidate("attack", IntentStyle.Attack, true, 6, 6),
            new ActionCandidate("defend", IntentStyle.Defend, false, 2, 0)
        };
        var amplified = ActionCandidate.Attack("multi", 3, 3, 2, weak: false, 1);
        var boosted = planner.Plan(new Pools(8, 2), profile, [amplified], random);
        Equal(15, boosted.Action?.Damage ?? 0, "actual damage is not capped");
        var weakened = ActionCandidate.Attack("multi", 3, 3, 0, weak: true, 1);
        Equal(9, weakened.Value, "weak does not refill pool spending");
        Equal(6, weakened.Damage, "weak changes actual damage");
        double attacks = 0;
        Pools pools = default;
        for (int i = 0; i < 2000; i++)
        {
            var plan = planner.Plan(pools, profile, choices, random);
            if (plan.Action is null || plan.After.ActionValue < 0 || plan.Action.Value > plan.Granted.ActionValue)
                throw new InvalidOperationException("Pool overspent or failed to select from an affordable catalog.");
            pools = plan.After;
            attacks += plan.Action.IsAttack ? 1 : 0;
        }
        if (Math.Abs(attacks / 2000 - profile.AttackFrequency) > 0.06)
            throw new InvalidOperationException($"Unexpected long-run attack frequency: {attacks / 2000:P1}");

        ActionPlan FirstTurn(double value) =>
            planner.Plan(planner.Start(profile, value), profile, [], new Random(9));
        Equal(-1, FirstTurn(2).OpeningAdjustmentApplied, "charge first installment for positive state");
        Equal(0, FirstTurn(-2).OpeningAdjustmentApplied, "keep weakened opening");
        Pools queued = FirstTurn(-2).After;
        var skipped = planner.Plan(queued, profile, [], random, canRandomize: false);
        Equal(2, skipped.After.OpeningAdjustment, "skipped turn retains opening adjustment");
        var second = planner.Plan(skipped.After with { ActionValue = 0 }, profile, [], random);
        Equal(1, second.OpeningAdjustmentApplied, "delayed opening refund");
        var third = planner.Plan(second.After with { ActionValue = 0 }, profile, [], random);
        Equal(1, third.OpeningAdjustmentApplied, "second refund installment");
        Equal(0, third.After.OpeningAdjustment, "opening refund applies once");
        var full = planner.Plan(queued with { ActionValue = 100 }, profile, [], random);
        Equal(0, full.OpeningAdjustmentApplied, "full pool cannot receive credit");
        Equal(2, full.After.OpeningAdjustment, "full pool retains queued credit");
        var cheap = planner.Plan(queued with { ActionValue = 0 }, profile, [choices[1]], random);
        Equal(1, cheap.OpeningAdjustmentApplied, "affordable light action receives credit");
        Equal(cheap.Granted.ActionValue - 2, cheap.After.ActionValue, "unused refund carries forward");

        var catalog = new ActionCatalog(new ActionValuationSettings(0.4, 0.45, 0.25, 0.5, 2.5, 0.8, 2, 2, 1));
        var varied = profile with
        {
            HitWeights = new Dictionary<int, double> { [1] = 1, [3] = 1 },
            EnabledEffects =
            [
                EffectKind.Block, EffectKind.Strength, EffectKind.Weak,
                EffectKind.Vulnerable, EffectKind.Dazed, EffectKind.Wound
            ],
            IntentWeights = Enum.GetValues<IntentStyle>().ToDictionary(style => style, _ => 1.0)
        };
        var context = new ActionContext(6, 12, 6, DazedDraws: 0.6, WoundDraws: 0.8);
        var generated = catalog.Generate(varied, context, new ActionHistory(), 1).ToArray();
        ActionCandidate Pure(EffectKind kind) => generated.First(action => !action.IsAttack &&
            action.Effects![0].Kind == kind);
        Equal(0.8 * 2 * profile.AttackFrequency, Pure(EffectKind.Strength).Value,
            "strength scales with future attacks, not this turn");
        Equal(1.5, Pure(EffectKind.Dazed).Value, "dazed draw valuation");
        Equal(2, Pure(EffectKind.Wound).Value, "wound draw valuation");
        Equal(3, Pure(EffectKind.Vulnerable).Value, "vulnerable values covered damage");
        if (Pure(EffectKind.Dazed).Effects![0].Pile != StatusPile.Draw ||
            Pure(EffectKind.Wound).Effects![0].Pile != StatusPile.Discard ||
            !generated.Any(action => action.IsAttack && action.Effects is [_, { Kind: EffectKind.Weak }]))
            throw new InvalidOperationException("Generated effects or ordering are incomplete.");
        if (generated.Any(action => action.IsAttack &&
                action.Effects![0].Amount * action.Effects[0].Hits < profile.ActionValue * 0.35))
            throw new InvalidOperationException("A token hit bypassed the attack floor.");
        var exhausted = new ActionHistory(2, 2, 1, 1);
        var limited = catalog.Generate(varied, context, exhausted, 2).ToArray();
        if (limited.Any(action => action.Effects!.Any(effect => effect.Kind is
                EffectKind.Strength or EffectKind.Dazed or EffectKind.Wound or EffectKind.Weak or EffectKind.Vulnerable)) ||
            limited.Any(action => !action.IsAttack && action.Effects!.Any(effect => effect.Kind == EffectKind.Block)))
        {
            throw new InvalidOperationException("Repeat or encounter caps were ignored.");
        }

        var protectedPlayer = catalog.Generate(varied, context with { PlayerHasArtifact = true }, new ActionHistory(), 1);
        if (protectedPlayer.Any(action => action.Effects!.Any(effect => effect.Kind is EffectKind.Weak or EffectKind.Vulnerable)))
            throw new InvalidOperationException("The catalog offered a debuff blocked by known Artifact.");
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
