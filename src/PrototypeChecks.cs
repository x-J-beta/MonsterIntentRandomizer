namespace MonsterIntentRandomizer;

internal static class PrototypeChecks
{
    public static void Run()
    {
        var profile = new MonsterProfile(8, 0.67, 0.35, 2,
            new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 1, [IntentStyle.Defend] = 1 });
        CheckPools(profile);
        CheckWeights();
        CheckOpening(profile);
        CheckCurse(profile);
        CheckSpendLimits(profile);
        CheckBudgetTransfers(profile);
        CheckCombatHorizon(profile);
        CheckExtraActions(profile);
        CheckAnimationHits();
        Console.WriteLine("Prototype checks passed.");
    }

    private static void CheckPools(MonsterProfile profile)
    {
        var planner = new ActionPlanner(new PoolSettings());
        var random = new Random(2026);

        Equal(6, planner.Start(profile, -2).ActionValue, "negative opening credited in full");
        Equal(-1, planner.Settle(new Pools(1, 0), 2, 0).ActionValue, "overspending puts the pool into debt");
        Equal(1, planner.Settle(new Pools(12, 4), 9, 3).AttackDesire, "each attack hit spends one desire");

        var weakened = ActionCandidate.Attack("multi", 3, 3, 0, weak: true);
        Equal(9, weakened.Value, "weak preserves attack cost");
        Equal(6, weakened.Damage, "weak changes actual damage");
        var negative = planner.Plan(new Pools(0, -2), profile, [], random, (_, _) => weakened);
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
        Equal(2, (new PoolSettings(Chaos: 0.67)).SpecialStatusLimit,
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

        var weak = new StatusValue(new Dictionary<string, double> { ["amount*actionValue"] = -0.15 }, MaxCostMultiplier: 1.2);
        var opening = OpeningValuation.Option("WEAK", weak, profile);
        Equal(8, opening.Doses!.Single().State.Amount, "a nonstackable opening state gets its quantity from its cost");
    }

    private static void CheckCurse(MonsterProfile profile)
    {
        var valuation = new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>>
        {
            [EffectKind.Debt] = new() { ["fixed"] = 10 }
        });
        var settings = new ActionValuationSettings(valuation, 0.25, 1,
            Policies: new Dictionary<EffectKind, EffectPolicy> { [EffectKind.Debt] = new(Stackable: false, Unique: true) });
        var catalog = new ActionCatalog(settings);
        var context = new ActionContext(10);
        var curse = catalog.Generate(profile with { ActionValue = 20 }, context).Single().Quote(20, 20, null, new Random(1))!;
        Equal(20, curse.Value, "two curse cards cost twice the unit value");
        if (curse.Effects is not [{ Unique: true, Pile: StatusPile.Hand, Amount: 2 }])
            throw new InvalidOperationException("A single nonstackable Debt action can insert multiple copies into the hand.");

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

    private static void CheckSpendLimits(MonsterProfile original)
    {
        var profile = original with
        {
            ActionValue = 20,
            AttackFrequency = 0,
            Favorites = new Dictionary<EffectKind, double> { [EffectKind.Strength] = 4, [EffectKind.Ritual] = 6 }
        };
        var seen = new Dictionary<EffectKind, double>();
        ActionOffer Offer(EffectKind kind, double weight, double favorite, double maximum) =>
            new(kind, EffectPolicy.Default(kind), weight, favorite, maximum, (target, _, _, _) =>
            {
                seen[kind] = target;
                return new ActionCandidate(kind.ToString(), 1, 0, [new ActionEffect(kind, 1)]);
            });
        var planner = new ActionPlanner(new PoolSettings(NonAttackCandidates: 1));
        planner.Plan(default, profile,
            [Offer(EffectKind.Block, 1, 0, 12), Offer(EffectKind.Strength, 0, 4, 2), Offer(EffectKind.Ritual, 0, 6, 10)],
            new HalfRandom(), (_, _) => null);
        Equal(3, seen.Count, "each favorite is evaluated beyond the ordinary quota");
        Equal(6.5, seen[EffectKind.Block], "ordinary nominal price is sampled between the minimum and the cap");
        Equal(1.5, seen[EffectKind.Strength], "favorite nominal price respects the per-effect cap");
        Equal(5.5, seen[EffectKind.Ritual], "favorite nominal price is sampled before allocating budgets");
        var valuation = new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>>
        {
            [EffectKind.DrawDown] = new() { ["amount"] = 2 }
        });
        var catalog = new ActionCatalog(new ActionValuationSettings(valuation, 0.25, 1,
            Weights: new Dictionary<EffectKind, RoomWeight> { [EffectKind.DrawDown] = new(-0.21, 0.008) }));
        var favorite = catalog.Generate(profile with { Favorites = new Dictionary<EffectKind, double>
            { [EffectKind.DrawDown] = 1 } }, new ActionContext(10, Rooms: 0)).Single();
        Equal(0, favorite.Weight, "unopened room weight excludes the ordinary candidate");
        Equal(1, favorite.Favorite, "unopened room weight leaves the favorite candidate available");
        Equal(32, SpendAmount.Closest(1, int.MaxValue, 16, 17, amount => amount * 0.5), "variable stacks can exceed a former fixed cap");
        Equal(9, SpendAmount.Closest(1, int.MaxValue, 10, 20, amount => Math.Min(amount, 9)),
            "saturated utility selects the least quantity at that price");
        Equal(5, SpendAmount.Closest(1, 20, 4, 5, amount => 20.0 / amount, descending: true),
            "thresholds are selected by price");
    }

    private static void CheckBudgetTransfers(MonsterProfile profile)
    {
        var planner = new ActionPlanner(new PoolSettings());
        var attacker = profile with
        {
            ActionValue = 8, AttackFrequency = 1,
            IntentWeights = new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 1 }
        };
        ActionCandidate Attack(int hits, double budget) =>
            new("Attack", budget, budget, [new ActionEffect(EffectKind.Attack, 1, hits)]);
        var wait = planner.Plan(default, attacker, [], new SequenceRandom(0.5, 0.5, 0), Attack, enemyCount: 2);
        Equal(8, wait.AttackBudgetChange, "the full attack allocation is deposited after a nonattack roll");
        Equal(wait.Granted.ActionValue, wait.After.ActionValue, "depositing does not charge action power");
        var strike = planner.Plan(default, attacker, [], new SequenceRandom(0.5, 0.5, 0.9, 0.1), Attack,
            sharedAttackBudget: wait.AttackBudgetChange, enemyCount: 2);
        double withdrawal = 8 * Math.Pow(6.0 / 11, 2);
        Equal(-withdrawal, strike.AttackBudgetChange, "a second enemy withdraws using attack probability squared");
        Equal(8 + withdrawal, strike.Granted.ActionValue - strike.After.ActionValue,
            "withdrawn budget is included in the attack's normal action cost");

        var support = profile with { ActionValue = 10, AttackFrequency = 0 };
        ActionOffer expensive = new(EffectKind.Strength, EffectPolicy.Default(EffectKind.Strength), 1, 0, 20,
            (_, _, _, _) => new ActionCandidate("Strength", 20, 0, [new ActionEffect(EffectKind.Strength, 1)]));
        var excluded = planner.Plan(default, support, [expensive], new SequenceRandom(0.5, 0.5, 0.5, 0.5, 0.5, 0.5), Attack);
        var confirmed = planner.Plan(default, support, [expensive], new SequenceRandom(0.5, 0.5, 0.5, 0.5, 0.5, 0.49), Attack);
        Equal(0, excluded.Action!.Value, "an over-budget candidate fails at the acceptance boundary");
        Equal(20, confirmed.Granted.ActionValue - confirmed.After.ActionValue,
            "an accepted over-budget candidate charges the entire amount");

        var mixed = attacker with
        {
            ActionValue = 20,
            IntentWeights = new Dictionary<IntentStyle, double> { [IntentStyle.Attack] = 2, [IntentStyle.Defend] = 1 }
        };
        var midTurn = new Pools(0, -2);
        var miss = planner.Plan(midTurn, mixed, [expensive], new SequenceRandom(0.5, 0.5, 0.5, 0.5, 0.5, 0.5), Attack);
        var accepted = planner.Plan(midTurn, mixed, [expensive], new SequenceRandom(0.5, 0.5, 0.5, 0.5, 0.5, 0.1), Attack);
        Equal(0, miss.AttackBudgetChange, "zero nonattack spend cancels the missed attack budget");
        Equal(40, accepted.AttackBudgetChange, "nonattack overspending adds proportional attack credit");
        var down = planner.Plan(default, attacker, [], new SequenceRandom(0.5, 0.5, 0.9, 0.1), Attack, sharedAttackBudget: -4);
        Equal(24.0 / 11, down.AttackBudgetChange, "a negative withdrawal repays part of the shared debt");
        Equal(8 - 24.0 / 11, down.Action!.Value, "negative withdrawal lowers the base attack target");
    }

    private sealed class SequenceRandom(params double[] rolls) : Random
    {
        private int index;
        public override double NextDouble() => rolls[index++];
    }

    private static void CheckCombatHorizon(MonsterProfile profile)
    {
        var adjusted = new PlayerActionCurve().WithCombatEstimates(profile, 16, 32, 0);
        Equal(2, adjusted.BaseExpectedSurvivalTurns, "monster survival applies half of the allied HP");
        Equal(3, adjusted.ExpectedCombatTurns, "combat duration counts the full allied HP");
        var placement = new StatusPlacementValues(Draw: 1, Discard: 1, Top: 1);
        Equal(1, placement.For(StatusPile.Top, 1), "immediate draw has full value");
        Equal(0.60, placement.For(StatusPile.Draw, 1), "short fight discounts random insertion");
        Equal(0.20, placement.For(StatusPile.Discard, 1), "short fight discounts delayed insertion");
        Equal(0.60, placement.For(StatusPile.Discard, 3), "discard valuation interpolates");
        Equal(1, placement.For(StatusPile.Draw, 5), "random insertion reaches full value at five rounds");
        Equal(1, placement.For(StatusPile.Discard, 5), "discard reaches full value at five rounds");
        var valuation = new ActionValuation(new Dictionary<EffectKind, Dictionary<string, double>>
        {
            [EffectKind.StrengthDown] = new() { ["amount*combatTurns"] = 0.5 }
        });
        Equal(3, valuation.Evaluate(EffectKind.StrengthDown, adjusted, 2), "debuff formulas can use combat duration");
    }

    private static void CheckExtraActions(MonsterProfile profile)
    {
        var planner = new ActionPlanner(new PoolSettings(NonAttackCandidates: 2));
        Equal(0, planner.ExtraActions(20, 10), "the first threshold is strict");
        Equal(1, planner.ExtraActions(20.01, 10), "above twice the base budget earns one extra action");
        Equal(1, planner.ExtraActions(50, 10), "the second threshold is strict");
        Equal(2, planner.ExtraActions(50.01, 10), "above five times the base budget earns two extra actions");
        var chaotic = new ActionPlanner(new PoolSettings(Chaos: 1));
        Equal(2, chaotic.ExtraActions(24, 10), "chaos lowers the thresholds");

        var attacker = profile with
        {
            ActionValue = 10, AttackFrequency = 1,
            IntentWeights = new Dictionary<IntentStyle, double>
                { [IntentStyle.Attack] = 1, [IntentStyle.Defend] = 1, [IntentStyle.Buff] = 1 }
        };
        var offers = new[] { EffectKind.Block, EffectKind.Heal, EffectKind.Strength }.Select(kind =>
            new ActionOffer(kind, EffectPolicy.Default(kind), 1, 0, 1,
                (_, _, _, _) => new ActionCandidate(kind.ToString(), 1, 0, [new ActionEffect(kind, 1)])));
        var withAttack = planner.Plan(new Pools(21, 0), attacker, offers, new HalfRandom(),
            (hits, _) => ActionCandidate.Attack("test", hits, 1, 0, false));
        Equal(3, withAttack.Action!.Effects.Count, "a whole attack uses one of the three action slots");
        var withoutAttack = planner.Plan(new Pools(21, -2), attacker, offers, new HalfRandom(), (_, _) => null);
        Equal(3, withoutAttack.Action!.Effects.Count, "the extra ordinary slot and candidate are both available");
    }

    private sealed class HalfRandom : Random
    {
        public override double NextDouble() => 0.5;
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}
