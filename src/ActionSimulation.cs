namespace MonsterIntentRandomizer;

internal sealed record SimulatedTurn(int Round, ActionPlan Plan, Pools After, bool Imbalanced,
    bool UsedFallback, double Cost, int Hits);

internal static class ActionSimulation
{
    internal static IReadOnlyList<SimulatedTurn> Trace(RuleConfiguration rules, MonsterProfile profile,
        IReadOnlyList<OpeningPick> opening, int act, int seed, int rounds, double fullBlockChance,
        bool fixedOpening = false, bool phaseAllowsRandomization = true, int firstEnemyAction = 1,
        double? fallbackValue = null, int? fallbackHits = null)
    {
        var planner = new ActionPlanner(rules.PoolSettings);
        var catalog = new ActionCatalog(rules.Actions);
        var actionRandom = new Random(seed ^ 0x649AAB81);
        var rareBuffRandom = new Random(seed ^ 0x13C96303);
        var specialDebuffRandom = new Random(seed ^ 0x0BC16431);
        var imbalanceRandom = new Random(seed ^ 0x4B82C097);
        var balances = planner.Start(profile, opening.Sum(pick => pick.Value));
        var activeEffects = Enum.GetValues<EffectKind>().Where(kind => ActionEffect.IsRareBuff(kind) &&
            opening.Any(pick => pick.State.Id.Replace("_", "").Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase)))
            .ToHashSet();
        var prior = rules.TurnPrior with { Act = act, ActiveEffects = activeEffects };
        var history = new ActionHistory();
        int intangibleTurns = opening.Where(pick => pick.State.Id == "INTANGIBLE").Sum(pick => pick.State.Amount);
        bool previousAttack = false;
        bool specialDebuffSpent = false;
        bool imbalanced = opening.Any(pick => pick.State.Id == "IMBALANCED");
        var turns = new List<SimulatedTurn>(rounds);
        for (int turn = 1; turn <= rounds; turn++)
        {
            int strength = prior.Strength + history.AddedStrength + history.RitualStrength + opening.Sum(pick =>
                pick.State.Id switch
                {
                    "STRENGTH" or "STRENGTH_DOWN" => pick.State.Amount,
                    "RITUAL" => (turn - 1) * pick.State.Amount,
                    _ => 0
                });
            bool skipFromImbalance = imbalanced && previousAttack && imbalanceRandom.NextDouble() < fullBlockChance;
            var context = prior with
            {
                Strength = strength,
                Weakened = prior.Weakened || opening.Any(pick => pick.State.Id == "WEAK" && turn <= pick.State.Amount),
                RareBuffRoll = rareBuffRandom.NextDouble(),
                SpecialDebuffAvailable = !specialDebuffSpent,
                SpecialDebuffRoll = specialDebuffRandom.NextDouble(),
                AlreadyIntangible = prior.AlreadyIntangible || intangibleTurns > 0,
                AllowHardCardLimit = prior.AllowHardCardLimit && turn >= rules.HardCardLimitFirstTurn
            };
            bool canRandomize = phaseAllowsRandomization && !(fixedOpening && turn == 1) &&
                turn >= firstEnemyAction && !skipFromImbalance && !opening.Any(pick => pick.State.Id == "STUN" &&
                    turn < firstEnemyAction + pick.State.Amount);
            var plan = planner.Plan(balances, profile, catalog.Generate(profile, context, history, turn),
                actionRandom, canRandomize);
            bool fallback = plan.CanRandomize && plan.Action is null && fallbackValue.HasValue;
            double cost = plan.Action?.Value ?? (fallback ? fallbackValue!.Value : 0);
            int hits = plan.Action?.AttackHits ?? (fallback ? fallbackHits.GetValueOrDefault() : 0);
            balances = fallback ? planner.Settle(plan.Granted, cost, hits) : plan.After;
            turns.Add(new SimulatedTurn(turn, plan, balances, skipFromImbalance, plan.CanRandomize && plan.Action is null, cost, hits));

            if (plan.Action is { } action)
            {
                history = history.Record(action, turn);
                activeEffects.UnionWith(action.Effects!.Select(effect => effect.Kind).Where(ActionEffect.IsRareBuff));
                specialDebuffSpent |= action.Effects!.Any(effect => effect.IsSpecialDebuff);
            }
            else
                history = history with { RitualStrength = history.RitualStrength + history.AddedRitual };
            previousAttack = hits > 0;
            intangibleTurns = Math.Max(0, intangibleTurns - 1) +
                (plan.Action?.Effects?.Where(effect => effect.Kind == EffectKind.Intangible).Sum(effect => effect.Amount) ?? 0);
        }
        return turns;
    }
}
