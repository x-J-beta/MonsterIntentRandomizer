namespace MonsterIntentRandomizer;

internal static class PrototypeChecks
{
    public static void Run()
    {
        var scenario = new[] { new TargetingScenario(1, 1, 1) };
        var target = new EnemySnapshot("test", 8, 8, 0, 0);
        var attack = new DamageEstimate(3, 0, 1);
        var baseline = SurvivalForecast.Predict([target], target.Id, attack, scenario, 4);
        Equal(2, baseline.Sum(window => window.AliveAtAction), "baseline actions");
        var guarded = SurvivalForecast.Predict([target, new EnemySnapshot("ally", 9, 9, 0, 1)],
            target.Id, attack, scenario, 8);
        Equal(5, guarded.Sum(window => window.AliveAtAction), "ally diverts focus");
        var late = SurvivalForecast.Predict([target with { FirstEnemyAction = 2 }], target.Id, attack,
            scenario, 4, firstPlayerWindowFraction: 0);
        Equal(0, late[0].AliveAtAction, "no action on spawn round");
        Equal(1, late[1].AliveAtAction, "late spawn survives next window");

        var estimate = new MonsterProfile(10, 0.15, 1.5, 0.35, 4, new Dictionary<IntentStyle, double>());
        Equal(-3, OpeningValuation.Evaluate(new StatusValue(1, -0.3), estimate, 1), "opening vulnerable");
        Equal(9, OpeningValuation.Evaluate(new StatusValue(1, 0.9), estimate, 1), "opening intangible");
        Equal(-3.6, OpeningValuation.Evaluate(new StatusValue(-2, -1.2, Basis: OpeningBasis.AttackFrequency), estimate, -2),
            "negative strength uses hit frequency");
        Equal(-2, OpeningValuation.Evaluate(new StatusValue(5, -0.4, Basis: OpeningBasis.Flat), estimate, 5), "flat doom");
        Equal(2.7, OpeningValuation.Evaluate(new StatusValue(3, 0.225, Basis: OpeningBasis.SurvivalTurns), estimate, 3),
            "regen uses lifetime");
        Equal(3.2, OpeningValuation.Evaluate(new StatusValue(4, 0.2, Basis: OpeningBasis.SurvivalTurns),
            estimate with { ActionValue = 100 }, 4), "plating ignores action value");
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

        var openingOptions = new Dictionary<string, IReadOnlyList<OpeningOption>>
        {
            ["first"] = [new(new OpeningState("STRENGTH", 1), 2)],
            ["second"] = [new(new OpeningState("WEAK", 1), -2)]
        };
        var openingActionValues = new Dictionary<string, double> { ["first"] = 3, ["second"] = 3 };
        var draws = new OpeningDraws(0, 1, 1, 12, 2, 0.5);
        var result = OpeningValuation.Draw(openingOptions, openingActionValues, draws, new Random(11));
        if (result.Count != 2 || Math.Abs(result.Sum(pick => pick.Value)) > 3)
            throw new InvalidOperationException("Opposite opening values should cancel within the net budget.");
        if (OpeningValuation.Draw(openingOptions, openingActionValues,
                draws with { MaxEncounterValueMultiplier = 0.25 }, new Random(11)).Count != 2)
            throw new InvalidOperationException("Large opposite effects should cancel before the budget is checked.");
        if (OpeningValuation.Draw(openingOptions, openingActionValues,
                draws with { Negative = 0, MaxEncounterValueMultiplier = 0.25 }, new Random(11)).Count != 0)
            throw new InvalidOperationException("An unbalanced opening exceeded the encounter multiplier.");
        var variable = OpeningValuation.Option("REGEN",
            new StatusValue(1, 0.225, [1, 2, 3, 4], Basis: OpeningBasis.SurvivalTurns), estimate);
        var rolledDoses = new HashSet<int>();
        var doseRandom = new Random(123);
        for (int i = 0; i < 50; i++)
        {
            var pick = OpeningValuation.Draw(new Dictionary<string, IReadOnlyList<OpeningOption>> { ["first"] = [variable] },
                new Dictionary<string, double> { ["first"] = 10 }, new OpeningDraws(0, 1, 0, 1, 1, 1), doseRandom).Single();
            rolledDoses.Add(pick.State.Amount);
            Equal(pick.State.Amount * 0.225 * 4, pick.Value, "rolled dose is priced before budget");
        }
        if (!rolledDoses.SetEquals([1, 2, 3, 4]))
            throw new InvalidOperationException("Opening dose range was ignored.");

        var planner = new ActionPlanner(new PoolSettings(0.75, 0.9));
        var profile = new MonsterProfile(8, 0.15, 0.67, 0.35, 2,
            new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 1, [IntentStyle.Defend] = 1 });
        var adjusted = new PoolSettings(0.75, 0.9, ActionValueMultiplier: 2, ChaosMultiplier: 0).Adjust(profile);
        Equal(16, adjusted.ActionValue, "global action value multiplier");
        Equal(0, adjusted.Volatility, "zero chaos removes grant jitter");
        Equal(profile.AttackFrequency, adjusted.AttackFrequency, "multipliers preserve the configured attack cadence");
        var random = new Random(2026);
        Equal(3, planner.Plan(new Pools(4, 1), profile, [], random, canRandomize: false).After.ActionValue,
            "skipped turns only decay");
        var choices = new[]
        {
            new ActionCandidate("attack", IntentStyle.Attack, true, 6, 6),
            new ActionCandidate("defend", IntentStyle.Defend, false, 2, 0, Effects: [new(EffectKind.Block, 5)])
        };
        var amplified = ActionCandidate.Attack("multi", 3, 3, 2, weak: false, 1);
        var multiProfile = profile with { AttackFrequency = 1.5, HitWeights = new Dictionary<int, double> { [3] = 1 } };
        var boosted = planner.Plan(new Pools(8, 4), multiProfile, [amplified], random);
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
        var overdrawn = planner.Settle(new Pools(1, 0), 2, 0);
        Equal(-1, overdrawn.ActionValue, "overspending puts the balance into debt");
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
        Equal(cheap.Granted.ActionValue - cheap.Action!.Value, cheap.After.ActionValue, "unused refund carries forward");

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
            [EffectKind.Vulnerable] = [1], [EffectKind.Dazed] = [1], [EffectKind.Wound] = [1],
            [EffectKind.Heal] = [3], [EffectKind.Intangible] = [1], [EffectKind.Beckon] = [1],
            [EffectKind.Tangled] = [1], [EffectKind.StrengthDown] = [1, 2], [EffectKind.DexterityDown] = [1, 2],
            [EffectKind.Soulbind] = [1, 2, 3], [EffectKind.DrawDown] = [1], [EffectKind.EnergyDown] = [1]
        };
        var valuation = new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>>
        {
            [EffectKind.Attack] = new() { ["amount*hits"] = 1 },
            [EffectKind.Block] = new() { ["effectiveAmount"] = 0.4 },
            [EffectKind.Heal] = new() { ["effectiveAmount"] = 0.45 },
            [EffectKind.Strength] = new() { ["amount*attackFrequency"] = 1.2 },
            [EffectKind.Ritual] = new() { ["amount*attackFrequency*survivalTurns"] = 0.8 },
            [EffectKind.Weak] = new() { ["amount*actionValue"] = 0.15 },
            [EffectKind.Vulnerable] = new() { ["amount*actionValue"] = 0.3 },
            [EffectKind.Intangible] = new() { ["amount*actionValue"] = 0.9 },
            [EffectKind.Ringing] = new() { ["playerTurnValue"] = 0.7 },
            [EffectKind.Tangled] = new() { ["amount*playerTurnValue"] = 0.4 },
            [EffectKind.Smoggy] = new() { ["playerTurnValue"] = 0.85 },
            [EffectKind.Soulbind] = new() { ["amount*playerTurnValue"] = 0.6, ["playerTurnValue"] = -0.36 },
            [EffectKind.DrawDown] = new() { ["amount*playerTurnValue"] = 0.2 },
            [EffectKind.EnergyDown] = new() { ["amount*playerTurnValue"] = 0.33 },
            [EffectKind.StrengthDown] = new() { ["fixed"] = 2, ["amount"] = 0.5 },
            [EffectKind.DexterityDown] = new() { ["fixed"] = 1.5, ["amount"] = 0.4 },
            [EffectKind.Dazed] = new() { ["amount*draws"] = 1.5 },
            [EffectKind.Wound] = new() { ["amount*draws"] = 2.5 },
            [EffectKind.Beckon] = new() { ["amount*draws"] = 2.5, ["amount*triggers"] = 3 }
        }, new PlayerTurnValues(10, 20, 30));
        var catalogSettings = new ActionValuationSettings(valuation, 0.25, 1, amounts);
        var catalog = new ActionCatalog(catalogSettings);
        var varied = profile with
        {
            HitWeights = new Dictionary<int, double> { [1] = 1, [3] = 1 },
            EffectWeights = amounts.Keys.ToDictionary(kind => kind, _ => 1.0),
            IntentWeights = Enum.GetValues<IntentStyle>().ToDictionary(style => style, _ => 1.0)
        };
        var context = new ActionContext(6, DazedDraws: 0.6, WoundDraws: 0.8, MissingHp: 3);
        var generated = catalog.Generate(varied, context, new ActionHistory(), 1).ToArray();
        ActionCandidate Pure(EffectKind kind) => generated.First(action => !action.IsAttack &&
            action.Effects![0].Kind == kind);
        Equal(1.2 * profile.AttackFrequency, Pure(EffectKind.Strength).Value, "strength uses attack frequency");
        Equal(0.9 * profile.ActionValue, Pure(EffectKind.Intangible).Value, "intangible equivalent turns");
        Equal(3 * 0.45, Pure(EffectKind.Heal).Value, "heal coefficient exceeds block");
        Equal(0.6 * (2.5 + 3), Pure(EffectKind.Beckon).Value, "beckon in draw pile");
        Equal(0.9, Pure(EffectKind.Dazed).Value, "reduced dazed draw valuation");
        Equal(1.2, Pure(EffectKind.Wound).Value, "wound draw valuation in the discard pile");
        Equal(4, Pure(EffectKind.Tangled).Value, "tangled uses player turn value");
        Equal(25.5, valuation.Evaluate(EffectKind.Smoggy, varied, 1, act: 3), "smoggy uses the act-specific player turn value");
        var later = catalog.Generate(varied, context, new ActionHistory(), 2).ToArray();
        Equal(3, later.Single(action => action.Id == "StrengthDown 2").Value, "strength reduction costs fixed plus stacks");
        Equal(2.3, later.Single(action => action.Id == "DexterityDown 2").Value, "dexterity reduction costs fixed plus stacks");
        var secondAct = catalog.Generate(varied with { ActionValue = 80 }, context with { Act = 2 }, new ActionHistory(), 2).ToArray();
        Equal(8, secondAct.Single(action => action.Id == "Tangled 1").Value, "tangled ignores monster action value");
        for (int stacks = 1; stacks <= 3; stacks++)
            Equal((stacks - 0.6) * 0.6 * 20, secondAct.Single(action => action.Id == $"Soulbind {stacks}").Value, "soulbind stack curve");
        Equal(4, secondAct.Single(action => action.Id == "DrawDown 1").Value, "draw reduction uses player turn value");
        Equal(6.6, secondAct.Single(action => action.Id == "EnergyDown 1").Value, "energy reduction uses player turn value");
        if (generated.Any(action => action.Effects!.Any(effect => effect.IsSpecialDebuff)) ||
            catalog.Generate(varied, context with { SpecialDebuffRoll = 0.2 }, new ActionHistory(), 2)
                .Any(action => action.Effects!.Any(effect => effect.IsSpecialDebuff)) ||
            catalog.Generate(varied, context with { SpecialDebuffAvailable = false }, new ActionHistory(), 2)
                .Any(action => action.Effects!.Any(effect => effect.IsSpecialDebuff)))
            throw new InvalidOperationException("Special debuffs bypassed the first-turn, chance or encounter restriction.");
        Equal(0.3 * profile.ActionValue, Pure(EffectKind.Vulnerable).Value, "vulnerable equivalent turns");
        Equal(Pure(EffectKind.Block).Value, Pure(EffectKind.Block).ActualScore, "block contributes to actual score");
        var mixedProfile = varied with { AttackSpend = 0.5, Volatility = 0,
            HitWeights = new Dictionary<int, double> { [1] = 1 } };
        var weakenedMixed = Enumerable.Range(0, 50).Select(seed => planner.Plan(new Pools(0, 2), mixedProfile,
            [Pure(EffectKind.Weak)], new Random(seed),
            buildAttack: (hits, target) => catalog.BuildAttack(mixedProfile, context with { Weakened = true }, hits, target)))
            .First(plan => plan.Action?.Effects is [_, { Kind: EffectKind.Weak }]).Action!;
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
        Equal(2 * 0.15 * profile.ActionValue, tuned.Single(action => action.Id == "Weak 2").Value, "configured debuff duration");
        if (!tuned.Any(action => action.Id == "Strength 2") ||
            tunedCatalog.BuildAttack(varied, context, 1, 1)?.Effects?[0].Amount != 2)
            throw new InvalidOperationException("Configured dosages or minimum base damage were ignored.");
        Equal(9, new ActionPlanner(new PoolSettings(0.75, 0.9, 1, 3)).Start(profile, -1).ActionValue,
            "configured starting pool");
        if (Pure(EffectKind.Dazed).Effects![0].Pile != StatusPile.Draw ||
            Pure(EffectKind.Wound).Effects![0].Pile != StatusPile.Discard)
            throw new InvalidOperationException("Generated effects or ordering are incomplete.");
        var cardPositions = varied with
        {
            CardPlacements = Enum.GetValues<StatusPile>().Select(pile => new StatusPlacement(EffectKind.Dazed, pile))
                .Append(new StatusPlacement(EffectKind.Wound, StatusPile.Bottom)).ToArray()
        };
        var placed = catalog.Generate(cardPositions, context, new ActionHistory(), 1).ToArray();
        var dazedPiles = placed.SelectMany(action => action.Effects!).Where(effect => effect.Kind == EffectKind.Dazed)
            .Select(effect => effect.Pile).ToHashSet();
        if (!dazedPiles.SetEquals([StatusPile.Top, StatusPile.Draw, StatusPile.Discard]) ||
            !placed.Any(action => action.Id == "Wound 1 @Bottom"))
            throw new InvalidOperationException("Dazed placement restrictions should only affect Dazed.");
        if (catalog.BuildAttack(varied, context, 1, 0)?.Effects is not [{ Amount: 1, Hits: 1 }])
            throw new InvalidOperationException("The catalog omitted the lowest positive base damage.");
        var exhausted = new ActionHistory(AddedStrength: 5, HardCardLimits: 1);
        var limited = catalog.Generate(varied, context, exhausted, 2).ToArray();
        if (!limited.Any(action => action.Id == "Strength 1") || !limited.Any(action => action.Id == "Dazed 1 @Draw"))
            throw new InvalidOperationException("Repeatable actions were removed from future turns.");

        var protectedPlayer = catalog.Generate(varied, context with { PlayerHasArtifact = true }, new ActionHistory(), 1);
        if (protectedPlayer.Any(action => action.Effects!.Any(effect => effect.IsPlayerDebuff)))
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
        Equal(0.8 * specialized.AttackFrequency * specialized.BaseExpectedSurvivalTurns, ritual.Value,
            "ritual uses attack frequency and expected survival");
        var history = new ActionHistory().Record(ritual);
        Equal(0, history.RitualStrength, "newly cast ritual does not trigger immediately");
        var ringing = specializedActions.Single(action => action.Effects is [{ Kind: EffectKind.Ringing }]);
        Equal(1, history.Record(ringing).RitualStrength, "ritual triggers after later turns");
        if (specializedActions.Single(action => action.Id == "Wound 1 @Top").Value <=
            specializedActions.Single(action => action.Id == "Wound 1 @Discard").Value ||
            specializedActions.Any(action => action.IsAttack && action.Effects!.Any(effect => effect.Kind == EffectKind.Ringing)))
            throw new InvalidOperationException("Placement values or hard card limits were ignored.");
        Console.WriteLine("Prototype checks passed.");
        Console.WriteLine($"Single-hit sample: {attacks / 2000:F3} hits/turn (target {profile.AttackFrequency:F2}).");
        Console.WriteLine($"Three-hit sample: {totalHits / 2000:F3} hits/turn (target {multiProfile.AttackFrequency:F2}).");

    }

    private static void Equal(double expected, double actual, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}
