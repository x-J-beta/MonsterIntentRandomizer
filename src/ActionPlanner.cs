namespace MonsterIntentRandomizer;

public enum IntentStyle { Attack, Buff, Defend, PlayerDebuff }

public sealed record ActionCandidate(
    string Id, double Value, double Damage, IReadOnlyList<ActionEffect> Effects, double? ResolvedValue = null)
{
    public double ActualScore => ResolvedValue ?? (IsAttack ? Damage : Value);
    public int AttackHits => Effects.Where(effect => effect.Kind == EffectKind.Attack).Sum(effect => effect.Hits);
    public bool IsAttack => Effects.Any(effect => effect.Kind == EffectKind.Attack);

    public static ActionCandidate Attack(
        string id, int hitCount, int baseDamagePerHit, int strength, bool weak,
        double weakDamageReduction = 0.25)
    {
        double baseDamage = hitCount * baseDamagePerHit;
        double actualDamage = hitCount * CombatMath.AttackDamagePerHit(baseDamagePerHit, strength, weak, weakDamageReduction);
        return new ActionCandidate(id, baseDamage, actualDamage,
            [new ActionEffect(EffectKind.Attack, baseDamagePerHit, hitCount)]);
    }
}

internal static class CombatMath
{
    public static double AttackDamagePerHit(double baseDamage, int strength = 0, bool weak = false,
        double weakDamageReduction = 0.25) =>
        Math.Floor(Math.Max(0, baseDamage + strength) * (weak ? 1 - weakDamageReduction : 1));

    internal static int AttackAnimationCount(int hits)
    {
        long squared = (long)hits * hits;
        long groups = (long)Math.Cbrt(squared);
        return (int)(groups * groups * groups >= squared ? groups : groups + 1);
    }

    internal static int[]? DrawAttackAnimationHits(int hits, Random random)
    {
        int animationCount = AttackAnimationCount(hits);
        if (animationCount == hits)
            return null;

        var positions = new int[animationCount];
        for (int hit = 0, placed = 0; placed < animationCount; hit++)
            if (random.Next(hits - hit) < animationCount - placed)
                positions[placed++] = hit;
        return positions;
    }
}

public sealed record PoolSettings(
    double InitialActionValueShare = 0.5,
    double InitialAttackDesire = 0, double BalanceTargetWeight = 0.5,
    double ActionValueMultiplier = 1, double Chaos = 0,
    int NonAttackCandidates = 5, double FullChaosBudgetAt = 1,
    IReadOnlyDictionary<IntentStyle, double>? ChaosBudgetWeights = null, double OpeningStrengthAtMax = 1.5,
    double HighAscensionActionValueMultiplier = 1.15,
    double FavoriteDirectBaseShare = 0.275, double FavoriteDirectStrengthMultiplier = 0.055,
    bool ExternalMoveRandomSkip = true)
{
    internal double ChaosMultiplier => 1 + 4 * Chaos;
    internal double BudgetSpread => 0.15 * ChaosMultiplier;
    internal double SharedNonAttackWeight => 0.05 + 0.20 * Chaos;
    internal double OpeningStrength => 1 + (OpeningStrengthAtMax - 1) * Chaos;
    internal int SpecialStatusLimit => 1 + (Chaos >= 0.67 ? 1 : 0) + (Chaos >= 1 ? 1 : 0);
    private double BudgetChaos => Math.Clamp(Chaos / FullChaosBudgetAt, 0, 1);

    private static readonly IReadOnlyDictionary<IntentStyle, double> DefaultChaosWeights =
        new Dictionary<IntentStyle, double>
        {
            [IntentStyle.Attack] = 40, [IntentStyle.Buff] = 20, [IntentStyle.Defend] = 20, [IntentStyle.PlayerDebuff] = 20
        };

    public MonsterProfile Adjust(MonsterProfile profile, bool highAscension = false)
    {
        var adjusted = profile with
        {
            ActionValue = profile.ActionValue * ActionValueMultiplier * (highAscension ? HighAscensionActionValueMultiplier : 1)
        };
        double mix = BudgetChaos;
        if (mix == 0)
            return adjusted;

        var target = ChaosBudgetWeights ?? DefaultChaosWeights;
        var styles = Enum.GetValues<IntentStyle>();
        double configuredTotal = styles.Sum(style => Math.Max(0, profile.IntentWeights.GetValueOrDefault(style)));
        double targetTotal = styles.Sum(style => target.GetValueOrDefault(style));
        return adjusted with
        {
            IntentWeights = styles.ToDictionary(style => style, style =>
                (1 - mix) * Math.Max(0, profile.IntentWeights.GetValueOrDefault(style)) / Math.Max(configuredTotal, double.Epsilon)
                + mix * target.GetValueOrDefault(style) / targetTotal)
        };
    }
}

public sealed record MonsterProfile(
    double ActionValue, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights,
    IReadOnlyList<StatusPlacement>? CardPlacements = null, double PlayerActionValue = 0,
    double CurrentHp = 0, int RoomsEntered = 0,
    IReadOnlyDictionary<EffectKind, double>? Favorites = null, IReadOnlyList<EffectKind>? AllowedBuffs = null,
    int MonstersBehind = 0, double ExpectedCombatTurns = 5)
{
    internal double LaterMonsterMultiplier(double percent) => Math.Pow(1 + percent / 100, MonstersBehind);

    public double BudgetedAttackShare
    {
        get
        {
            double total = IntentWeights.Values.Where(weight => weight > 0).Sum();
            return total > 0 && AttackFrequency > 0
                ? Math.Max(0, IntentWeights.GetValueOrDefault(IntentStyle.Attack)) / total : 0;
        }
    }
}

public readonly record struct Pools(double ActionValue, double AttackDesire)
{
    internal int CompletedClawTurns { get; init; }
}

public sealed record ActionPlan(ActionCandidate? Action, Pools Granted, Pools After)
{
    internal int[]? AnimatedAttackHits { get; init; }
    internal double AttackBudgetChange { get; init; }
    internal double AttackBudgetPerNonAttackPoint { get; init; }
}

public sealed class ActionPlanner(PoolSettings settings)
{
    public Pools Start(MonsterProfile profile, double openingValue = 0) =>
        new(profile.ActionValue * settings.InitialActionValueShare - openingValue, settings.InitialAttackDesire);

    private sealed record Choice(ActionOffer Offer, ActionCandidate Action, bool Ordinary)
    {
        internal double Budget { get; init; }
        internal double Weight => Math.Min(1, Budget / Action.Value);
    }

    internal ActionPlan Plan(Pools previous, MonsterProfile profile, IEnumerable<ActionOffer> options,
        Random random, Func<int, double, ActionCandidate?> buildAttack,
        int extraClawDesire = 0, int specialStatusesRemaining = 1, double sharedAttackBudget = 0, int enemyCount = 1)
    {
        double turnShare = Math.Clamp(profile.AttackFrequency, 0, 1);
        double income = profile.ActionValue * (turnShare + (1 - turnShare) * profile.NonAttackMultiplier);
        var granted = new Pools(previous.ActionValue + income, previous.AttackDesire + profile.AttackFrequency + extraClawDesire);
        double target = Math.Max(0, profile.ActionValue + previous.ActionValue * settings.BalanceTargetWeight);
        double strength = profile.Favorites?.Values.Sum() ?? 0;
        double direct = strength > 0
            ? Math.Clamp(settings.FavoriteDirectBaseShare + strength * settings.FavoriteDirectStrengthMultiplier, 0, 1)
            : 0;
        double favoriteBudget = target * direct;
        double attackFraction = profile.BudgetedAttackShare;
        double attackBudget = (target - favoriteBudget) * attackFraction;
        double ordinaryBudget = (target - favoriteBudget) * (1 - attackFraction);
        double chance = attackFraction > 0 && (attackBudget > 0 || sharedAttackBudget > 0) && granted.AttackDesire >= 0
            ? 1 - 5 / (granted.AttackDesire + 10) : 0;
        double attackTarget = Jitter(attackBudget, random);
        int hits = DrawHits(granted.AttackDesire, chance, random);
        double withdrawal = hits > 0 ? sharedAttackBudget * Math.Pow(chance, enemyCount) : 0;
        var attack = hits > 0 ? buildAttack(hits, attackTarget + withdrawal) : null;
        double nonAttackAllocation = ordinaryBudget + favoriteBudget;
        double otherBudget = nonAttackAllocation;
        int extraActions = ExtraActions(previous.ActionValue, profile.ActionValue);
        int actionLimit = 2 + extraActions;
        var parts = new List<ActionCandidate>(2);
        if (attack != null)
            parts.Add(attack);

        var extras = otherBudget > 0
            ? Shortlist(options.Where(offer => specialStatusesRemaining > 0 || !offer.Policy.Unique),
                profile, ordinaryBudget, favoriteBudget, strength, (long)settings.NonAttackCandidates + extraActions, random) : [];
        while (parts.Count < actionLimit && otherBudget > 0 && extras.Count > 0)
        {
            var extra = Draw(extras.Select(candidate => (candidate, candidate.Weight)), 0, random);
            if (extra == null)
                break;
            double cost = extra.Action.Value;
            if (cost > otherBudget && random.NextDouble() >= otherBudget / cost)
                break;
            parts.Add(extra.Action);
            otherBudget -= cost;
            extras.Remove(extra);
            if (extra.Offer.Policy.Unique && --specialStatusesRemaining == 0)
                extras.RemoveAll(candidate => candidate.Offer.Policy.Unique);
        }

        var action = Compose(parts);
        double transferRate = nonAttackAllocation > 0 ? attackBudget / nonAttackAllocation : 0;
        return new ActionPlan(action, granted, Settle(granted, action.Value, action.AttackHits))
        {
            AttackBudgetChange = (attack == null ? attackBudget : -withdrawal) - transferRate * otherBudget,
            AttackBudgetPerNonAttackPoint = transferRate
        };
    }

    public Pools Settle(Pools granted, double value, int attackHits) =>
        new(granted.ActionValue - value, granted.AttackDesire - attackHits);

    internal int ExtraActions(double carriedBalance, double baseActionValue)
    {
        double ratio = carriedBalance / baseActionValue;
        double scale = 1 - settings.Chaos * 2 / 3;
        if (ratio <= 1 + scale)
            return 0;

        int count = (int)Math.Min(int.MaxValue - 2, Math.Sqrt((ratio - 1) / scale));
        return ratio > 1 + (double)count * count * scale ? count : count - 1;
    }

    private static int DrawHits(double desire, double chance, Random random)
    {
        if (chance <= 0 || random.NextDouble() < 1 - chance)
            return 0;
        int desireHits = (int)Math.Clamp(Math.Floor(desire), 1, int.MaxValue);
        return random.NextDouble() < 0.5 ? 1 : desireHits;
    }

    private List<Choice> Shortlist(IEnumerable<ActionOffer> options, MonsterProfile profile,
        double ordinaryBudget, double favoriteBudget, double favoriteStrength, long ordinaryLimit, Random random)
    {
        var offers = options.ToArray();
        var ordinary = offers.Where(offer => offer.Weight > 0).ToList();
        var selected = new HashSet<EffectKind>();
        while (ordinaryBudget > 0 && selected.Count < ordinaryLimit && ordinary.Count > 0)
        {
            var offer = Draw(ordinary.Select(offer => (offer, offer.Weight)), 0, random)!;
            selected.Add(offer.Kind);
            ordinary.Remove(offer);
        }
        var choices = new List<Choice>();
        foreach (var offer in offers)
        {
            bool fromOrdinary = selected.Contains(offer.Kind);
            if (!fromOrdinary && (favoriteBudget <= 0 || offer.Favorite <= 0))
                continue;
            var minimum = offer.Quote(0, offer.MaximumCost, null, random);
            if (minimum == null)
                continue;
            double rolled = minimum.Value + (offer.MaximumCost - minimum.Value) * random.NextDouble();
            var action = offer.Quote(rolled, rolled, null, random) ?? minimum;
            choices.Add(new Choice(offer, action, fromOrdinary));
        }
        var counts = choices.Where(choice => choice.Ordinary).GroupBy(choice => choice.Offer.Style)
            .ToDictionary(group => group.Key, group => group.Count());
        int ordinaryCount = counts.Values.Sum();
        double totalIntent = profile.IntentWeights.Where(entry => entry.Key != IntentStyle.Attack).Sum(entry => entry.Value);
        double shared = settings.SharedNonAttackWeight;
        for (int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            double budget = favoriteStrength > 0 ? favoriteBudget * choice.Offer.Favorite / favoriteStrength : 0;
            if (choice.Ordinary)
            {
                double share = shared / ordinaryCount;
                if (totalIntent > 0)
                    share += (1 - shared) * profile.IntentWeights.GetValueOrDefault(choice.Offer.Style) /
                        (totalIntent * counts[choice.Offer.Style]);
                budget += ordinaryBudget * share;
            }
            choices[i] = choice with { Budget = budget };
        }
        return choices;
    }

    private double Jitter(double target, Random random)
    {
        return target * (1 + settings.BudgetSpread * (random.NextDouble() + random.NextDouble() - 1));
    }

    internal static double MatchingWeight(double value, double target, double spread)
    {
        double distance = (value - target) / spread;
        return 1 / (1 + distance * distance);
    }

    private static ActionCandidate Compose(List<ActionCandidate> parts)
    {
        if (parts.Count == 0)
            return new ActionCandidate("Wait", 0, 0, []);
        if (parts.Count == 1)
            return parts[0];
        return new ActionCandidate(string.Join(" + ", parts.Select(part => part.Id)),
            parts.Sum(part => part.Value), parts.Sum(part => part.Damage),
            parts.SelectMany(part => part.Effects).ToArray(), parts.Sum(part => part.ActualScore));
    }

    internal static T? Draw<T>(IEnumerable<(T Choice, double Weight)> options, double skipWeight, Random random)
    {
        var entries = options.ToArray();
        double roll = random.NextDouble() * (entries.Sum(entry => entry.Weight) + skipWeight) - skipWeight;
        if (roll >= 0)
        {
            foreach (var entry in entries)
                if ((roll -= entry.Weight) < 0)
                    return entry.Choice;
        }
        return default;
    }
}
