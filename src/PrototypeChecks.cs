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
        var openingOptions = new Dictionary<string, IReadOnlyList<OpeningOption>>
        {
            ["first"] = [new(new OpeningState("STRENGTH", 1), 2)],
            ["second"] = [new(new OpeningState("WEAK", 1), -2), new(variant, -0.5)]
        };
        var openingBudgets = new Dictionary<string, double> { ["first"] = 3, ["second"] = 3 };
        var draws = new OpeningDraws(0, 1, 1, 12, 2, 2.5);
        var result = OpeningValuation.Draw(openingOptions, openingBudgets, draws, new Random(11));
        if (result.Count < 2 || result.Sum(pick => Math.Abs(pick.Value)) > 2.5)
            throw new InvalidOperationException("Multiple opening draws exceeded the sum-of-absolute-values budget.");

        double VulnerableValue(double potential)
        {
            var attacker = new DamageEstimate(potential, 0, 1);
            var before = SurvivalForecast.Predict([target with { Hp = 100 }], target.Id, attacker, scenario, 4);
            var state = new OpeningState("VULNERABLE", 1);
            var after = SurvivalForecast.Predict([target with { Hp = 100 }], target.Id, attacker, scenario, 4, opening: state);
            return OpeningValuation.Evaluate(state, before, after, expectedMoves, 0.8)!.Value;
        }
        if (VulnerableValue(15) >= VulnerableValue(5))
            throw new InvalidOperationException("Vulnerable did not scale with potential player damage.");

        var planner = new ActionPlanner(new PoolSettings(0.75, 0.9));
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
        Equal(3, boosted.Granted.AttackDesire - boosted.After.AttackDesire, "each segment spends one desire");
        var composite = amplified with { Effects = [amplified.Effects![0], new ActionEffect(EffectKind.Block, 3)] };
        Equal(3, composite.AttackHits, "only attack segments count in a composite action");
        var weakened = ActionCandidate.Attack("multi", 3, 3, 0, weak: true, 1);
        Equal(9, weakened.Value, "weak does not refill pool spending");
        Equal(6, weakened.Damage, "weak changes actual damage");
        double attacks = 0;
        Pools pools = default;
        for (int i = 0; i < 2000; i++)
        {
            var plan = planner.Plan(pools, profile, choices, random);
            if (plan.Action is null)
                throw new InvalidOperationException("Planner failed to select from the catalog.");
            pools = plan.After;
            attacks += plan.Action.AttackHits;
        }
        if (Math.Abs(attacks / 2000 - profile.AttackFrequency) > 0.06)
            throw new InvalidOperationException($"Unexpected long-run attack frequency: {attacks / 2000:P1}");

        Equal(2, planner.Start(profile, 2).ActionValue, "positive opening charged in full at start");
        Equal(6, planner.Start(profile, -2).ActionValue, "negative opening credited in full at start");
        Equal(-4, planner.Start(profile, 8).ActionValue, "opening value can put the pool into debt");
        var uncapped = planner.Plan(new Pools(100, 0), profile, [], new Random(7));
        if (uncapped.Granted.ActionValue < 75)
            throw new InvalidOperationException("Large balances hit a capacity limit.");
        var overdrawn = planner.Plan(new Pools(-25, 0), profile, [choices[1]], new Random(7));
        if (overdrawn.Action is null || overdrawn.After.ActionValue >= 0)
            throw new InvalidOperationException("A negative balance blocked a valid action.");
        Equal(overdrawn.Granted.ActionValue - 2, overdrawn.After.ActionValue, "overspending is recorded");
        var differentValues = new[] { choices[1], choices[1] with { Id = "heavy defend", Value = 9 } };
        int HeavySelections(double initialValue)
        {
            var sampleRandom = new Random(11);
            return Enumerable.Range(0, 500).Count(_ =>
                planner.Plan(new Pools(initialValue, 0), profile, differentValues, sampleRandom).Action?.Value == 9);
        }
        if (HeavySelections(20) <= HeavySelections(-10))
            throw new InvalidOperationException("Action balances did not influence selection weights.");
        var cheap = planner.Plan(planner.Start(profile, -2), profile, [choices[1]], new Random(7));
        Equal(cheap.Granted.ActionValue - 2, cheap.After.ActionValue, "unused refund carries forward");

        var multiProfile = profile with { AttackFrequency = 1.5, HitWeights = new Dictionary<int, double> { [3] = 1 } };
        double totalHits = 0;
        Pools multiPools = default;
        for (int i = 0; i < 2000; i++)
        {
            var plan = planner.Plan(multiPools, multiProfile, [amplified, choices[1]], random);
            multiPools = plan.After;
            totalHits += plan.Action!.AttackHits;
        }
        if (Math.Abs(totalHits / 2000 - 1.5) > 0.12)
            throw new InvalidOperationException($"Unexpected multihit rate: {totalHits / 2000:F3}.");

        var amounts = new Dictionary<EffectKind, int[]>
        {
            [EffectKind.Block] = [3, 5, 8], [EffectKind.Strength] = [1], [EffectKind.Weak] = [1],
            [EffectKind.Vulnerable] = [1], [EffectKind.Dazed] = [1], [EffectKind.Wound] = [1]
        };
        var catalogSettings = new ActionValuationSettings(0.4, 0.45, 0.25, 0.5, 2.5, 0.8, 2, 2, 1,
            8, 1, 1, amounts);
        var catalog = new ActionCatalog(catalogSettings);
        var varied = profile with
        {
            HitWeights = new Dictionary<int, double> { [1] = 1, [3] = 1 },
            EffectWeights = amounts.Keys.ToDictionary(kind => kind, _ => 1.0),
            IntentWeights = Enum.GetValues<IntentStyle>().ToDictionary(style => style, _ => 1.0)
        };
        var context = new ActionContext(6, 12, 6, DazedDraws: 0.6, WoundDraws: 0.8);
        var generated = catalog.Generate(varied, context, new ActionHistory(), 1).ToArray();
        ActionCandidate Pure(EffectKind kind) => generated.First(action => !action.IsAttack &&
            action.Effects![0].Kind == kind);
        Equal(0.8 * profile.AttackFrequency, Pure(EffectKind.Strength).Value,
            "strength scales with future attacks, not this turn");
        Equal(1.5, Pure(EffectKind.Dazed).Value, "dazed draw valuation");
        Equal(1.2, Pure(EffectKind.Wound).Value, "wound draw valuation in the discard pile");
        Equal(3, Pure(EffectKind.Vulnerable).Value, "vulnerable values covered damage");
        Equal(Pure(EffectKind.Block).Value, Pure(EffectKind.Block).ActualScore, "block contributes to actual score");
        var mixed = generated.First(action => action.IsAttack && action.Effects is [_, { Kind: EffectKind.Weak }]);
        Equal(mixed.Damage + Pure(EffectKind.Weak).Value, mixed.ActualScore, "compound score includes its debuff");
        var weakenedMixed = catalog.Generate(varied, context with { Weakened = true }, new ActionHistory(), 1)
            .First(action => action.Id == "1x4 + Weak 1");
        Equal(3, weakenedMixed.Damage, "panel reflects weak");
        Equal(3 + Pure(EffectKind.Weak).Value, weakenedMixed.ActualScore, "resolved score reflects weak and the side effect");
        Equal(4 + Pure(EffectKind.Weak).Value, weakenedMixed.Value, "pool cost retains base attack value");
        var preferences = varied with
        {
            EffectWeights = new Dictionary<EffectKind, double> { [EffectKind.Weak] = 2, [EffectKind.Vulnerable] = 1 }
        };
        var preferred = catalog.Generate(preferences, context, new ActionHistory(), 1)
            .Where(action => !action.IsAttack).ToDictionary(action => action.Id);
        Equal(2 * preferred["Vulnerable 1"].Weight, preferred["Weak 1"].Weight, "per-effect preference weights");
        var customAmounts = new Dictionary<EffectKind, int[]>(amounts)
        {
            [EffectKind.Block] = [4], [EffectKind.Strength] = [1, 2], [EffectKind.Weak] = [2]
        };
        var tunedCatalog = new ActionCatalog(catalogSettings with { Amounts = customAmounts, MinimumBaseDamagePerHit = 2 });
        var tuned = tunedCatalog.Generate(varied, context with { Weakened = true }, new ActionHistory(AddedStrength: 1), 1)
            .ToArray();
        Equal(4 * 0.4, tuned.Single(action => action.Id == "Block 4").Value, "configured block dose");
        Equal(1.35 * 1.8, tuned.Single(action => action.Id == "Weak 2").Value, "configured debuff duration");
        if (tuned.Any(action => action.Effects!.Any(effect =>
                (effect.Kind == EffectKind.Strength && effect.Amount == 2) ||
                (effect.Kind == EffectKind.Attack && effect.Amount < 2))))
            throw new InvalidOperationException("Configured dosages bypassed generation limits.");
        Equal(9, new ActionPlanner(new PoolSettings(0.75, 0.9, 1, 3)).Start(profile, -1).ActionValue,
            "configured starting pool");
        if (Pure(EffectKind.Dazed).Effects![0].Pile != StatusPile.Draw ||
            Pure(EffectKind.Wound).Effects![0].Pile != StatusPile.Discard ||
            !generated.Any(action => action.IsAttack && action.Effects is [_, { Kind: EffectKind.Weak }]))
            throw new InvalidOperationException("Generated effects or ordering are incomplete.");
        if (!generated.Any(action => action.IsAttack && action.Effects is [{ Amount: 1, Hits: 1 }]))
            throw new InvalidOperationException("The catalog omitted the lowest positive base damage.");
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

        var specialized = varied with
        {
            BaseExpectedSurvivalTurns = 5,
            EffectWeights = new Dictionary<EffectKind, double> { [EffectKind.Ritual] = 1, [EffectKind.Ringing] = 1, [EffectKind.Wound] = 1 },
            CardPlacements = [new(EffectKind.Wound, StatusPile.Top), new(EffectKind.Wound, StatusPile.Discard)]
        };
        var expanded = new ActionCatalog(catalogSettings with
        {
            Amounts = new Dictionary<EffectKind, int[]>(amounts) { [EffectKind.Ritual] = [1], [EffectKind.Ringing] = [1] }
        });
        var specializedActions = expanded.Generate(specialized, context, new ActionHistory(), 2).ToArray();
        var ritual = specializedActions.Single(action => !action.IsAttack && action.Effects![0].Kind == EffectKind.Ritual);
        var history = new ActionHistory().Record(ritual, 2);
        Equal(0, history.RitualStrength, "newly cast ritual does not trigger immediately");
        var ringing = specializedActions.Single(action => action.Effects is [{ Kind: EffectKind.Ringing }]);
        Equal(1, history.Record(ringing, 3).RitualStrength, "ritual triggers after later turns");
        if (specializedActions.Single(action => action.Id == "Wound 1 @Top").Value <=
            specializedActions.Single(action => action.Id == "Wound 1 @Discard").Value ||
            specializedActions.Any(action => action.IsAttack && action.Effects!.Any(effect => effect.Kind == EffectKind.Ringing)))
            throw new InvalidOperationException("Placement values or hard card limits were ignored.");
        Console.WriteLine("Prototype checks passed.");
        Console.WriteLine($"Single-hit sample: {attacks / 2000:F3} hits/turn (target {profile.AttackFrequency:F2}).");
        Console.WriteLine($"Three-hit sample: {totalHits / 2000:F3} hits/turn (target {multiProfile.AttackFrequency:F2}).");

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
