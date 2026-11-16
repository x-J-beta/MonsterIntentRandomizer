namespace MonsterIntentRandomizer;

internal static class PrototypeChecks
{
    public static void Run()
    {
        Equal(3, CombatMath.AttackAnimationCount(4), "multi-hit animation rounds up");
        Equal(4, CombatMath.AttackAnimationCount(8), "perfect cubes do not add an animation");
        Equal(9, CombatMath.AttackAnimationCount(27), "animation count follows two-thirds power");
        var roomCurve = new PlayerActionCurve(Base: 20, Exponent: 1);
        var combatCurve = roomCurve with { UseCombatDamage = true };
        Equal(20, roomCurve.At(0, 50), "combat damage is opt-in");
        Equal(20, combatCurve.At(0), "the room curve is used before a turn completes");
        Equal(23, combatCurve.At(0, 50), "observed damage contributes ten percent");
        Equal(18, combatCurve.At(0, 0), "a completed turn with zero damage contributes zero");
        Equal(2, combatCurve.Survival(36, 20, 0, 50), "survival uses the same adjusted value");
        var estimate = new MonsterProfile(10, 0.15, 1.5, 0.35, 4, new Dictionary<IntentStyle, double>());
        Equal(-3, OpeningValuation.Evaluate(Status("amount*actionValue", -0.3), estimate, 1), "opening vulnerable");
        Equal(9, OpeningValuation.Evaluate(Status("amount*actionValue", 0.9), estimate, 1), "opening intangible");
        Equal(-3.6, OpeningValuation.Evaluate(Status("amount*attackFrequency", -1.2) with { Amount = -1 }, estimate, -2),
            "negative strength uses hit frequency");
        Equal(-2, OpeningValuation.Evaluate(Status("amount", -0.4), estimate, 5), "flat doom");
        Equal(2.7, OpeningValuation.Evaluate(Status("amount*survivalTurns", 0.225), estimate, 3),
            "regen uses lifetime");
        Equal(3.2, OpeningValuation.Evaluate(Status("amount*survivalTurns", 0.2),
            estimate with { ActionValue = 100 }, 4), "plating ignores action value");
        var shell = Status("actionValue*hpPerSurvivalTurn*inverseAmount", 1, 6) with { Stackable = true };
        var shellProfile = estimate with { CurrentHp = 40 };
        var scroll = estimate with { ActionValue = 42, CurrentHp = 34, BaseExpectedSurvivalTurns = 0.49 };
        Equal(27.38, Math.Round(OpeningValuation.Evaluate(shell, scroll, 35), 2), "cap pricing for a short-lived enemy");
        Equal(80.0 / 3 - 80.0 / 5, OpeningValuation.ThresholdCost(shell, shellProfile, 5, 3), "lower cap costs the difference");
        Equal(0, OpeningValuation.ThresholdCost(shell, shellProfile, 3, 5), "a weaker cap has no cost");
        var shellOption = OpeningValuation.Option("HARDENED_SHELL", shell, shellProfile, previousThreshold: 5);
        var improved = OpeningValuation.Draw(
            new Dictionary<string, IReadOnlyList<OpeningOption>> { ["test"] = [shellOption] },
            new Dictionary<string, double> { ["test"] = 10 }, new OpeningDraws(0, 1, 0, 20, 1, 10), new Random(11)).Single();
        Equal(80.0 / improved.State.Amount - 16, improved.Value, "opening upgrades are charged from the native cap");
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
        var regen = Status("amount*survivalTurns", 0.225, 4);
        var variable = OpeningValuation.Option("REGEN", regen, estimate);
        var normalChaos = new PoolSettings(0.75, 0.9);
        var maxChaos = normalChaos with { ChaosMultiplier = 5 };
        Equal(0.25, normalChaos.SharedNonAttackWeight, "normal chaos shares a quarter of non-attack weight");
        Equal(1, maxChaos.SharedNonAttackWeight, "full chaos shares all non-attack weight");
        Equal(1.5, maxChaos.OpeningStrength, "full chaos opening strength");
        Equal(6, OpeningValuation.Option("REGEN", regen, estimate, strength: maxChaos.OpeningStrength)
            .Doses!.Max(dose => dose.State.Amount), "chaos raises the opening layer cap");
        Equal(3, new RoomWeight(4, 0.1).At(5, 0.5), "chaos attenuates base weight linearly");
        var lateWeight = new RoomWeight(-0.3, 0.02);
        Equal(0, lateWeight.At(7), "normal chaos keeps the late effect locked");
        Equal(0, lateWeight.At(6, 0.125), "moderate chaos still needs room progression");
        Equal(0.0025, lateWeight.At(7, 0.125), "chaos unlocks negative base weights earlier");
        Equal(1, lateWeight.At(0, 1), "full chaos can unlock a late effect at the start");
        Equal(0, new RoomWeight(-1, 0.02).At(90, 1), "explicitly disabled weights stay disabled");
        if (OpeningValuation.Draw(openingOptions, openingActionValues,
                draws with { Negative = 0, MaxEncounterValueMultiplier = 0.25, Strength = maxChaos.OpeningStrength },
                new Random(11)).Count != 1)
            throw new InvalidOperationException("Chaos did not raise the opening encounter cap.");
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

        var planner = new ActionPlanner(new PoolSettings(0.75, 0.9,
            DrawBudgetMultiplier: 1, UnusedAttackBudgetShare: 0));
        var profile = new MonsterProfile(8, 0.15, 0.67, 0.35, 2,
            new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 1, [IntentStyle.Defend] = 1 });
        var adjusted = new PoolSettings(0.75, 0.9, ActionValueMultiplier: 2, ChaosMultiplier: 0).Adjust(profile);
        Equal(16, adjusted.ActionValue, "global action value multiplier");
        Equal(9.2, new PoolSettings(0.75, 0.9).Adjust(profile, highAscension: true).ActionValue,
            "high ascension scales the same monster profile");
        Equal(0, adjusted.Volatility, "zero chaos removes grant jitter");
        Equal(profile.AttackFrequency, adjusted.AttackFrequency, "multipliers preserve the configured attack cadence");
        var random = new Random(2026);
        var balance = new Pools(4, 1);
        if (planner.Plan(balance, profile, [], random, canRandomize: false).After != balance)
            throw new InvalidOperationException("A preserved move changed the pools.");
        var choices = new[]
        {
            ActionCandidate.Attack("attack", 1, 6, 0, weak: false, 1),
            new ActionCandidate("defend", IntentStyle.Defend, false, 2, 0, Effects: [new(EffectKind.Block, 5)])
        };
        var amplified = ActionCandidate.Attack("multi", 3, 3, 2, weak: false, 1);
        var multiProfile = profile with { AttackFrequency = 1.5 };
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
        if (attacks / 2000 is < 0.45 or > 0.65)
            throw new InvalidOperationException($"Unexpected long-run attack frequency: {attacks / 2000:P1}");
        var fixedDesire = new Pools(0, (3 - profile.AttackFrequency) / 0.9);
        var attackRolls = new Random(51);
        double waitRate = Enumerable.Range(0, 2400).Count(_ =>
            planner.Plan(fixedDesire, profile, choices, attackRolls).Action!.AttackHits == 0) / 2400.0;
        if (Math.Abs(waitRate - 5.0 / 13) > 0.035 || new RoomWeight(-1, 10).At(20) != 0)
            throw new InvalidOperationException("Wait probability or explicitly disabled weights were ignored.");
        ActionCandidate BuildAttack(int hits, double _) => ActionCandidate.Attack("dynamic", hits, 2, 0, weak: false, 1);
        var forcedMulti = profile with { SingleHitPreference = 0 };
        int highest = Enumerable.Range(0, 32).Max(seed => planner.Plan(new Pools(0, 5), forcedMulti, [],
            new Random(seed), buildAttack: BuildAttack).Action!.AttackHits);
        Equal(5, highest, "dynamic attacks use the full floored desire");
        int negative = Enumerable.Range(0, 32).Max(seed => planner.Plan(new Pools(0, -2), profile, [],
            new Random(seed), buildAttack: BuildAttack).Action!.AttackHits);
        Equal(0, negative, "negative granted desire forbids attacks");

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
        if (totalHits / 2000 is < 1.2 or > 1.65)
            throw new InvalidOperationException($"Unexpected multihit rate: {totalHits / 2000:F3}.");

        var amounts = new Dictionary<EffectKind, int[]>
        {
            [EffectKind.Block] = [3, 5, 8], [EffectKind.Weak] = [1],
            [EffectKind.Vulnerable] = [1], [EffectKind.Dazed] = [1], [EffectKind.Wound] = [1],
            [EffectKind.Heal] = [3], [EffectKind.Beckon] = [1],
            [EffectKind.Tangled] = [1], [EffectKind.StrengthDown] = [1, 2], [EffectKind.DexterityDown] = [1, 2],
            [EffectKind.Soulbind] = [1, 2, 3], [EffectKind.DrawDown] = [1], [EffectKind.EnergyDown] = [1]
        };
        var valuation = new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>>
        {
            [EffectKind.Attack] = new() { ["amount*hits"] = 1 },
            [EffectKind.Block] = new() { ["effectiveAmount"] = 0.4 },
            [EffectKind.Heal] = new() { ["effectiveAmount"] = 0.45 },
            [EffectKind.Weak] = new() { ["amount*actionValue"] = 0.15 },
            [EffectKind.Vulnerable] = new() { ["amount*actionValue"] = 0.3 },
            [EffectKind.Ringing] = new() { ["playerActionValue"] = 0.7 },
            [EffectKind.Tangled] = new() { ["amount*playerActionValue"] = 0.4 },
            [EffectKind.Smoggy] = new() { ["playerActionValue"] = 0.85 },
            [EffectKind.Soulbind] = new() { ["amount*playerActionValue"] = 0.6, ["playerActionValue"] = -0.36 },
            [EffectKind.DrawDown] = new() { ["amount*playerActionValue"] = 0.2 },
            [EffectKind.EnergyDown] = new() { ["amount*playerActionValue"] = 0.33 },
            [EffectKind.StrengthDown] = new() { ["fixed"] = 2, ["amount"] = 0.5 },
            [EffectKind.DexterityDown] = new() { ["fixed"] = 1.5, ["amount"] = 0.4 },
            [EffectKind.Dazed] = new() { ["amount"] = 1.5 },
            [EffectKind.Wound] = new() { ["amount"] = 2.5 },
            [EffectKind.Wither] = new() { ["amount"] = 4 },
            [EffectKind.Beckon] = new() { ["amount"] = 6.5 }
        });
        var buffs = new Dictionary<EffectKind, StatusValue>
        {
            [EffectKind.Strength] = Status("amount*attackFrequency", 1.2),
            [EffectKind.Intangible] = Status("amount*actionValue", 0.9)
        };
        var catalogSettings = new ActionValuationSettings(valuation, 0.25, 1, amounts, Buffs: buffs, CardStartingPlayerActionValue: 10);
        var catalog = new ActionCatalog(catalogSettings);
        var clawBuffs = catalog.Generate(estimate, new ActionContext(6), extraBuffHits: estimate.BaseExpectedSurvivalTurns / 2).ToArray();
        Equal(4.2, clawBuffs.Single(action => action.Id == "Strength 1").Value, "claw valuation uses the extra estimated hits");
        Equal(9, clawBuffs.Single(action => action.Id == "Intangible 1").Value, "buffs without a hit factor retain their price");
        var rush = new MonsterPhase("Rush", ["PLOW_MOVE"], AllowedBuffs: [EffectKind.Strength]);
        var rushBuffs = catalog.Generate(rush.Apply(estimate), new ActionContext(6)).SelectMany(action => action.Effects!)
            .Where(effect => ActionEffect.IsMonsterBuff(effect.Kind)).Select(effect => effect.Kind).ToHashSet();
        if (!rushBuffs.SetEquals([EffectKind.Strength]))
            throw new InvalidOperationException("Phase buff restrictions must also exclude defensive buffs.");
        var hive = Status("amount*survivalTurns", 1.5) with
        {
            GeneratedCards = new Dictionary<EffectKind, double> { [EffectKind.Wound] = 1 }
        };
        var hiveSettings = catalogSettings with { Buffs = new Dictionary<EffectKind, StatusValue>
            { [EffectKind.PersonalHive] = hive } };
        var hiveCatalog = new ActionCatalog(hiveSettings);
        var nativeHive = hiveCatalog.Generate(estimate, new ActionContext(6), "ENTOMANCER")
            .Single(action => action.Effects is [{ Kind: EffectKind.PersonalHive }]);
        var customHive = hiveCatalog.Generate(estimate, new ActionContext(6), "FLYCONID")
            .Single(action => action.Effects is [{ Kind: EffectKind.PersonalHive }]);
        if (nativeHive.Effects![0].GeneratedCard != EffectKind.Dazed || customHive.Effects![0].GeneratedCard != EffectKind.Wound ||
            !hiveSettings.PolicyFor(EffectKind.PersonalHive, "ENTOMANCER").Stackable ||
            hiveSettings.PolicyFor(EffectKind.PersonalHive, "FLYCONID").Stackable)
            throw new InvalidOperationException("Entomancer stacks the native hive while other monsters use the configured hive.");
        var steam = Status("amount", 0.6, 6);
        var steamCatalog = new ActionCatalog(catalogSettings with { Buffs = new Dictionary<EffectKind, StatusValue>
        {
            [EffectKind.SteamEruption] = steam with { Weight = -1 },
            [EffectKind.Regen] = Status("amount", 0.25, 5)
        } });
        var steamProfile = estimate with { Favorites = new Dictionary<EffectKind, double> { [EffectKind.SteamEruption] = 4 } };
        var steamActions = steamCatalog.Generate(steamProfile, new ActionContext(6), "WATERFALL_GIANT").ToArray();
        var steamChoices = steamActions.Where(action => action.Effects is [{ Kind: EffectKind.SteamEruption }]).ToArray();
        Equal(0, steamChoices.Sum(action => action.Weight), "disabled Steam has zero base weight");
        Equal(4, steamChoices.Sum(action => action.SharedWeightBonus), "explicit Steam preference overrides the disabled base weight");
        if (steamActions.Single(action => action.Effects is [{ Kind: EffectKind.Regen, Amount: 5 }]).Style != IntentStyle.Defend)
            throw new InvalidOperationException("Regen must use the defense pool.");
        if (steamCatalog.Generate(estimate, new ActionContext(6), "WATERFALL_GIANT")
                .Any(action => action.Effects is [{ Kind: EffectKind.SteamEruption }]) ||
            steamCatalog.Generate(steamProfile, new ActionContext(6), "FLYCONID")
                .Any(action => action.Effects is [{ Kind: EffectKind.SteamEruption }]))
            throw new InvalidOperationException("Disabled Steam needs an explicit preference on its native monster.");
        var shellCatalog = new ActionCatalog(catalogSettings with
        {
            Buffs = new Dictionary<EffectKind, StatusValue> { [EffectKind.HardenedShell] = shell }
        });
        var upgrades = shellCatalog.Generate(shellProfile, new ActionContext(6,
                Thresholds: new Dictionary<EffectKind, int> { [EffectKind.HardenedShell] = 4 }))
            .Where(action => action.Effects is [{ Kind: EffectKind.HardenedShell }]).ToArray();
        if (upgrades.Length != 3)
            throw new InvalidOperationException("Only stronger damage caps should be offered.");
        Equal(80.0 / 3 - 20, upgrades.Single(action => action.Effects is [{ Amount: 3 }]).Value,
            "combat upgrades are charged from the active cap");
        var varied = profile with
        {
            PlayerActionValue = 10,
            IntentWeights = Enum.GetValues<IntentStyle>().ToDictionary(style => style, _ => 1.0)
        };
        var context = new ActionContext(6, MissingHp: 3);
        var withering = Status("survivalTurns*inverseAmount", 18, 6) with
        {
            MinAmount = 2, Stackable = true,
            GeneratedCards = new Dictionary<EffectKind, double> { [EffectKind.Wither] = 1, [EffectKind.Wound] = 1 }
        };
        var generators = catalogSettings with { Buffs = new Dictionary<EffectKind, StatusValue>
            { [EffectKind.WitheringPresence] = withering } };
        var generatorCatalog = new ActionCatalog(generators);
        var generatorProfile = varied with { BaseExpectedSurvivalTurns = 4 };
        var generatedChoices = generatorCatalog.Generate(generatorProfile, context).Where(action =>
            action.Effects is [{ Kind: EffectKind.WitheringPresence, Amount: 6 }]).ToArray();
        Equal(12, generatedChoices.Single(action => action.Effects is [{ GeneratedCard: EffectKind.Wither }]).Value,
            "the native card and threshold keep the original price");
        Equal(12 * 2.5 / 4, generatedChoices.Single(action => action.Effects is [{ GeneratedCard: EffectKind.Wound }]).Value,
            "generated status card changes the price");
        var witheringUpgrade = generatorCatalog.Generate(generatorProfile, context with
        {
            Thresholds = new Dictionary<EffectKind, int> { [EffectKind.WitheringPresence] = 6 },
            GeneratedCards = new Dictionary<EffectKind, EffectKind> { [EffectKind.WitheringPresence] = EffectKind.Wound }
        }).Single(action => action.Effects is [{ Kind: EffectKind.WitheringPresence, Amount: 4, GeneratedCard: EffectKind.Wound }]);
        Equal((18 - 12) * 2.5 / 4, witheringUpgrade.Value, "an upgrade charges the difference for the assigned card");
        var witheringOpening = OpeningValuation.Option("WITHERING_PRESENCE", withering, generatorProfile, actions: generators,
            previousThreshold: 6, existingCard: EffectKind.Wound);
        var openedWithering = OpeningValuation.Draw(
            new Dictionary<string, IReadOnlyList<OpeningOption>> { ["test"] = [witheringOpening] },
            new Dictionary<string, double> { ["test"] = 40 }, new OpeningDraws(0, 1, 0, 12, 1, 3), new Random(11)).Single();
        if (openedWithering.State.GeneratedCard != EffectKind.Wound || openedWithering.State.Amount >= 6)
            throw new InvalidOperationException("Opening upgrades must retain the original card and lower the threshold.");
        Equal((72.0 / openedWithering.State.Amount - 12) * 2.5 / 4, openedWithering.Value,
            "opening and combat share the generated card price");
        var generated = catalog.Generate(varied, context).ToArray();
        ActionCandidate Pure(EffectKind kind) => generated.First(action => !action.IsAttack &&
            action.Effects![0].Kind == kind && (!ActionEffect.IsCard(kind) || action.Effects[0].Pile == StatusPile.Draw));
        Equal(1.2 * profile.AttackFrequency, Pure(EffectKind.Strength).Value, "strength uses attack frequency");
        Equal(0.9 * profile.ActionValue, Pure(EffectKind.Intangible).Value, "intangible equivalent turns");
        Equal(3 * 0.45, Pure(EffectKind.Heal).Value, "heal coefficient exceeds block");
        Equal(6.5, Pure(EffectKind.Beckon).Value, "beckon in draw pile");
        Equal(1.5, Pure(EffectKind.Dazed).Value, "flat dazed value");
        var highPlayerAction = catalog.Generate(varied with { PlayerActionValue = 40 }, context);
        Equal(3 * 1.33, highPlayerAction.Single(action => action.Id == "Dazed 1 @Top").Value,
            "player action value replaces a portion of starting card value before pile placement");
        Equal(2.5 * 0.75, generated.Single(action => action.Id == "Wound 1 @Discard").Value, "wound value in the discard pile");
        Equal(4, Pure(EffectKind.Tangled).Value, "tangled uses player turn value");
        Equal(25.5, valuation.Evaluate(EffectKind.Smoggy, varied with { PlayerActionValue = 30 }, 1),
            "smoggy uses the player action value");
        Equal(3, generated.Single(action => action.Id == "StrengthDown 2").Value, "strength reduction costs fixed plus stacks");
        Equal(2.3, generated.Single(action => action.Id == "DexterityDown 2").Value, "dexterity reduction costs fixed plus stacks");
        var secondAct = catalog.Generate(varied with { ActionValue = 80, PlayerActionValue = 20 }, context).ToArray();
        Equal(8, secondAct.Single(action => action.Id == "Tangled 1").Value, "tangled ignores monster action value");
        for (int stacks = 1; stacks <= 3; stacks++)
            Equal((stacks - 0.6) * 0.6 * 20, secondAct.Single(action => action.Id == $"Soulbind {stacks}").Value, "soulbind stack curve");
        Equal(4, secondAct.Single(action => action.Id == "DrawDown 1").Value, "draw reduction uses player turn value");
        Equal(6.6, secondAct.Single(action => action.Id == "EnergyDown 1").Value, "energy reduction uses player turn value");
        if (!generated.Any(action => action.Effects!.Any(effect => effect.IsSpecialDebuff)) ||
            catalog.Generate(varied, context with { SpecialDebuffAvailable = false })
                .Any(action => action.Effects!.Any(effect => effect.IsSpecialDebuff)))
            throw new InvalidOperationException("Special debuffs should be available until the encounter limit is reached.");
        Equal(0.3 * profile.ActionValue, Pure(EffectKind.Vulnerable).Value, "vulnerable equivalent turns");
        Equal(Pure(EffectKind.Block).Value, Pure(EffectKind.Block).ActualScore, "block contributes to actual score");
        var mixedProfile = varied with
        {
            IntentWeights = varied.IntentWeights.ToDictionary(entry => entry.Key,
                entry => entry.Key == IntentStyle.Attack ? 3 : entry.Value),
            Volatility = 0,
            DesireHitPreference = 0
        };
        var weakenedMixed = Enumerable.Range(0, 50).Select(seed => planner.Plan(new Pools(0, 2), mixedProfile,
            [Pure(EffectKind.Weak)], new Random(seed),
            buildAttack: (hits, target) => catalog.BuildAttack(mixedProfile, context with { Weakened = true }, hits, target)))
            .First(plan => plan.Action?.Effects is [_, { Kind: EffectKind.Weak }]).Action!;
        Equal(3, weakenedMixed.Damage, "panel reflects weak");
        Equal(3 + Pure(EffectKind.Weak).Value, weakenedMixed.ActualScore, "resolved score reflects weak and the side effect");
        Equal(4 + Pure(EffectKind.Weak).Value, weakenedMixed.Value, "pool cost retains base attack value");
        var weighted = new ActionCatalog(catalogSettings with
        {
            Weights = new Dictionary<EffectKind, RoomWeight> { [EffectKind.Weak] = new(2), [EffectKind.Vulnerable] = new(1) }
        });
        var preferred = weighted.Generate(varied, context)
            .Where(action => !action.IsAttack).ToDictionary(action => action.Id);
        Equal(2 * preferred["Vulnerable 1"].Weight, preferred["Weak 1"].Weight, "global effect weights");
        var favorites = varied with
        {
            AttackFrequency = 0,
            Favorites = new Dictionary<EffectKind, double> { [EffectKind.Weak] = 5 }
        };
        var favored = weighted.Generate(favorites, context).ToArray();
        Equal(preferred["Weak 1"].Weight, favored.Single(action => action.Id == "Weak 1").Weight,
            "favorites preserve directional weights");
        Equal(5 * preferred["Weak 1"].Weight,
            favored.Where(action => action.Effects![0].Kind == EffectKind.Weak).Sum(action => action.SharedWeightBonus),
            "doses share the bonus from the global status weight");
        var directPlanner = new ActionPlanner(new PoolSettings(0.75, 0.9, NonAttackCandidates: 1,
            FavoriteDirectBaseShare: 0.25, FavoriteDirectStrengthMultiplier: 0.15));
        var picked = Enumerable.Range(0, 30).Select(seed => directPlanner.Plan(new Pools(0, 0), favorites,
            favored, new Random(seed)).Action!).Where(action => action.Effects!.Count > 0).ToArray();
        if (picked.Length == 0 || picked.Any(action => action.Effects![0].Kind != EffectKind.Weak))
            throw new InvalidOperationException("A full direct preference share should select available favorites.");
        var customAmounts = new Dictionary<EffectKind, int[]>(amounts)
        {
            [EffectKind.Block] = [4], [EffectKind.Weak] = [2], [EffectKind.Wound] = [3]
        };
        var tunedCatalog = new ActionCatalog(catalogSettings with { Amounts = customAmounts, MinimumBaseDamagePerHit = 2,
            Buffs = new Dictionary<EffectKind, StatusValue>(buffs) { [EffectKind.Strength] = buffs[EffectKind.Strength] with { MaxAmount = 2 } } });
        var tuned = tunedCatalog.Generate(varied, context with { Weakened = true })
            .ToArray();
        Equal(4 * 0.4, tuned.Single(action => action.Id == "Block 4").Value, "configured block dose");
        Equal(2 * 0.15 * profile.ActionValue, tuned.Single(action => action.Id == "Weak 2").Value, "configured debuff duration");
        var split = tuned.Single(action => action.Id == "Wound 3 @Top:2+Discard:1");
        Equal(2.5 * (2 * 1.33 + 0.75), split.Value, "split cards use the price of each destination");
        if (split.Effects is not [{ Amount: 3, CardDestinations: [{ Pile: StatusPile.Top, Amount: 2 },
                { Pile: StatusPile.Discard, Amount: 1 }] }])
            throw new InvalidOperationException("Cards should be distributed as one action with multiple destinations.");
        if (!tuned.Any(action => action.Id == "Strength 2") ||
            tunedCatalog.BuildAttack(varied, context, 1, 1)?.Effects?[0].Amount != 2)
            throw new InvalidOperationException("Configured dosages or minimum base damage were ignored.");
        Equal(9, new ActionPlanner(new PoolSettings(0.75, 0.9, 1, 3)).Start(profile, -1).ActionValue,
            "configured starting pool");
        if (generated.Where(action => action.Effects![0].Kind == EffectKind.Dazed)
                .Select(action => action.Effects![0].Pile).Distinct().Count() != 3)
            throw new InvalidOperationException("Generated effects or ordering are incomplete.");
        var cardPositions = varied with
        {
            CardPlacements = Enum.GetValues<StatusPile>().Select(pile => new StatusPlacement(EffectKind.Dazed, pile))
                .Append(new StatusPlacement(EffectKind.Wound, StatusPile.Bottom)).ToArray()
        };
        var placed = catalog.Generate(cardPositions, context).ToArray();
        var dazedPiles = placed.SelectMany(action => action.Effects!).Where(effect => effect.Kind == EffectKind.Dazed)
            .Select(effect => effect.Pile).ToHashSet();
        if (!dazedPiles.SetEquals([StatusPile.Top, StatusPile.Draw, StatusPile.Discard]) ||
            !placed.Any(action => action.Id == "Wound 1 @Bottom"))
            throw new InvalidOperationException("Dazed placement restrictions should only affect Dazed.");
        if (catalog.BuildAttack(varied, context, 1, 0)?.Effects is not [{ Amount: 1, Hits: 1 }])
            throw new InvalidOperationException("The catalog omitted the lowest positive base damage.");
        var protectedPlayer = catalog.Generate(varied, context with { PlayerHasArtifact = true });
        if (protectedPlayer.Any(action => action.Effects!.Any(effect => effect.IsPlayerDebuff)))
            throw new InvalidOperationException("The catalog offered a debuff blocked by known Artifact.");

        var specialized = varied with
        {
            BaseExpectedSurvivalTurns = 5,
            CardPlacements = [new(EffectKind.Wound, StatusPile.Top), new(EffectKind.Wound, StatusPile.Discard)]
        };
        var expandedSettings = catalogSettings with
        {
            Amounts = new Dictionary<EffectKind, int[]>(amounts) { [EffectKind.Ringing] = [1] },
            Buffs = new Dictionary<EffectKind, StatusValue>(buffs) { [EffectKind.Ritual] = Status("amount*attackFrequency*survivalTurns", 0.8) }
        };
        var expanded = new ActionCatalog(expandedSettings);
        var specializedActions = expanded.Generate(specialized, context).ToArray();
        var lockedSettings = expandedSettings with
        {
            Weights = new Dictionary<EffectKind, RoomWeight> { [EffectKind.Ringing] = new(-0.2, 0.01) }
        };
        bool OffersRinging(ActionValuationSettings settings, int rooms) =>
            new ActionCatalog(settings).Generate(specialized, context with { Rooms = rooms })
                .Any(action => action.Effects is [{ Kind: EffectKind.Ringing }]);
        if (OffersRinging(lockedSettings, 0) || !OffersRinging(lockedSettings, 25) ||
            !OffersRinging(lockedSettings with { ChaosProgress = 1 }, 0))
            throw new InvalidOperationException("Global room and chaos weights should admit special effects without monster preferences.");
        var preferredSpecial = specialized with { Favorites = new Dictionary<EffectKind, double> { [EffectKind.Ringing] = 5 } };
        if (expanded.Generate(preferredSpecial, context with { AllowHardCardLimit = false })
            .Any(action => action.Effects is [{ Kind: EffectKind.Ringing }]))
            throw new InvalidOperationException("Favorites should still respect the card limit timing.");
        var ritual = specializedActions.Single(action => !action.IsAttack && action.Effects![0].Kind == EffectKind.Ritual);
        Equal(0.8 * specialized.AttackFrequency * specialized.BaseExpectedSurvivalTurns, ritual.Value,
            "ritual uses attack frequency and expected survival");
        var history = new ActionHistory().Record(ritual);
        Equal(0, history.RitualStrength, "newly cast ritual does not trigger immediately");
        var ringing = specializedActions.Single(action => action.Effects is [{ Kind: EffectKind.Ringing }]);
        Equal(1, history.Record(ringing).RitualStrength, "ritual triggers after later turns");
        if (specializedActions.Single(action => action.Id == "Wound 1 @Top").Value <=
            specializedActions.Single(action => action.Id == "Wound 1 @Discard").Value)
            throw new InvalidOperationException("Placement values were ignored.");
        Console.WriteLine("Prototype checks passed.");
        Console.WriteLine($"Single-hit sample: {attacks / 2000:F3} hits/turn (target {profile.AttackFrequency:F2}).");
        Console.WriteLine($"Three-hit sample: {totalHits / 2000:F3} hits/turn (target {multiProfile.AttackFrequency:F2}).");

        static StatusValue Status(string term, double coefficient, int maxAmount = 1) =>
            new(new Dictionary<string, double> { [term] = coefficient }, MaxAmount: maxAmount, Common: true);
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}
