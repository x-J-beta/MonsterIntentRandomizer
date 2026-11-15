namespace MonsterIntentRandomizer;

public enum IntentStyle { Attack, Buff, Defend, PlayerDebuff }

public sealed record ActionCandidate(
    string Id, IntentStyle Style, bool IsAttack, double Value, double Damage, double Weight = 1,
    IReadOnlyList<ActionEffect>? Effects = null, double? ResolvedValue = null)
{
    public double ActualScore => ResolvedValue ?? (IsAttack ? Damage : Value);

    public int AttackHits => Effects?.Where(effect => effect.Kind == EffectKind.Attack).Sum(effect => effect.Hits)
        ?? (IsAttack ? 1 : 0);

    public static ActionCandidate Attack(
        string id, int hitCount, int baseDamagePerHit, int strength, bool weak, double weight,
        double weakDamageReduction = 0.25)
    {
        double baseDamage = hitCount * baseDamagePerHit;
        double actualDamage = hitCount * CombatMath.AttackDamagePerHit(baseDamagePerHit, strength, weak, weakDamageReduction);
        return new ActionCandidate(id, IntentStyle.Attack, true, baseDamage, actualDamage, weight,
            [new ActionEffect(EffectKind.Attack, baseDamagePerHit, hitCount)]);
    }
}

internal static class CombatMath
{
    public static double AttackDamagePerHit(double baseDamage, int strength = 0, bool weak = false,
        double weakDamageReduction = 0.25) =>
        Math.Floor(Math.Max(0, baseDamage + strength) * (weak ? 1 - weakDamageReduction : 1));
}

public sealed record PoolSettings(
    double ActionValueRetention, double AttackDesireRetention, double InitialActionValueShare = 0.5,
    double InitialAttackDesire = 0, double BalanceTargetWeight = 0.5, double MinimumSelectionSpread = 0.5,
    double ActionValueMultiplier = 1, double ChaosMultiplier = 1)
{
    public MonsterProfile Adjust(MonsterProfile profile) => profile with
    {
        ActionValue = profile.ActionValue * ActionValueMultiplier,
        Volatility = profile.Volatility * ChaosMultiplier
    };
}

public sealed record MonsterProfile(
    double ActionValue, double Volatility, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights,
    IReadOnlyDictionary<int, double>? HitWeights = null, IReadOnlyDictionary<EffectKind, double>? EffectWeights = null,
    IReadOnlyList<StatusPlacement>? CardPlacements = null, double? AttackSpend = null, double PlayerActionValue = 0)
{
    internal static IReadOnlyDictionary<int, double> DefaultHitWeights { get; } =
        new Dictionary<int, double> { [1] = 1 };

    internal IEnumerable<KeyValuePair<int, double>> AttackHitWeights =>
        (HitWeights ?? DefaultHitWeights).Where(pair => pair.Key > 0 && pair.Value > 0);

    public double ExpectedHitsPerAttack
    {
        get
        {
            var weights = AttackHitWeights.ToArray();
            return weights.Length == 0 ? 0 : weights.Sum(pair => pair.Key * pair.Value) / weights.Sum(pair => pair.Value);
        }
    }

    public double ExpectedAttackTurnShare => ExpectedHitsPerAttack > 0
        ? Math.Clamp(AttackFrequency / ExpectedHitsPerAttack, 0, 1) : 0;

    public double ExpectedAttackHitsPerTurn => ExpectedAttackTurnShare * ExpectedHitsPerAttack;

    public double BudgetedAttackShare
    {
        get
        {
            if (AttackSpend is double fraction)
                return fraction;
            double total = IntentWeights.Values.Where(weight => weight > 0).Sum();
            return total > 0 && AttackFrequency > 0
                ? Math.Max(0, IntentWeights.GetValueOrDefault(IntentStyle.Attack)) / total : 0;
        }
    }
}

public readonly record struct Pools(double ActionValue, double AttackDesire);

public sealed record ActionPlan(ActionCandidate? Action, Pools Granted, Pools After, bool CanRandomize);

public sealed class ActionPlanner(PoolSettings settings)
{
    public Pools Start(MonsterProfile profile, double openingValue = 0) =>
        new(profile.ActionValue * settings.InitialActionValueShare - openingValue, settings.InitialAttackDesire);

    public ActionPlan Plan(Pools previous, MonsterProfile profile, IEnumerable<ActionCandidate> candidates,
        Random random, bool canRandomize = true, Func<int, double, ActionCandidate?>? buildAttack = null)
    {
        double retainedAction = previous.ActionValue * settings.ActionValueRetention;
        double retainedDesire = previous.AttackDesire * settings.AttackDesireRetention;
        if (!canRandomize)
        {
            var aged = new Pools(retainedAction, retainedDesire);
            return new ActionPlan(null, aged, aged, false);
        }

        double turnShare = profile.ExpectedAttackTurnShare;
        double income = profile.ActionValue * (turnShare + (1 - turnShare) * profile.NonAttackMultiplier);
        var granted = new Pools(retainedAction + income, retainedDesire + profile.AttackFrequency);
        double target = Math.Max(0, profile.ActionValue + retainedAction * settings.BalanceTargetWeight);
        double attackFraction = profile.BudgetedAttackShare;
        double attackTarget = Jitter(target * attackFraction, profile.Volatility, random);
        double otherTarget = Jitter(target * (1 - attackFraction), profile.Volatility, random);
        var eligible = candidates.Where(candidate => candidate.Value > 0 && candidate.Weight > 0).ToArray();
        int hits = DrawHits(profile, granted.AttackDesire, attackFraction, attackTarget, random);
        var attack = hits > 0 ? (buildAttack != null ? buildAttack(hits, attackTarget) :
            Pick(eligible.Where(candidate => candidate.AttackHits == hits), profile, attackTarget, random)) : null;
        var parts = new List<ActionCandidate>(2);
        if (attack != null)
            parts.Add(attack);

        var extras = eligible.Where(candidate => !candidate.IsAttack && candidate.Effects is { Count: 1 } &&
            (attack == null || CanAttach(candidate.Effects[0]))).ToArray();
        while (parts.Count < 2 && otherTarget > 0)
        {
            var extra = Pick(extras, profile, otherTarget, random, maySkip: true);
            if (extra == null)
                break;
            parts.Add(extra);
            otherTarget -= extra.Value;
            var chosen = extra.Effects![0];
            extras = extras.Where(candidate => candidate.Effects![0].Kind != chosen.Kind &&
                (!chosen.IsSpecialDebuff || !candidate.Effects[0].IsSpecialDebuff)).ToArray();
        }

        var action = Compose(parts);
        return new ActionPlan(action, granted, Settle(granted, action.Value, action.AttackHits), true);
    }

    public Pools Settle(Pools granted, double value, int attackHits) =>
        new(granted.ActionValue - value, granted.AttackDesire - attackHits);

    private static int DrawHits(MonsterProfile profile, double desire, double attackFraction, double target, Random random)
    {
        if (desire <= 0 || attackFraction <= 0 || profile.AttackFrequency <= 0 ||
            profile.IntentWeights.GetValueOrDefault(IntentStyle.Attack) <= 0)
            return 0;
        var allowed = profile.AttackHitWeights.OrderBy(pair => pair.Key).ToArray();
        int large = allowed.Where(pair => pair.Key > 1 && pair.Key <= Math.Floor(desire))
            .Select(pair => pair.Key).LastOrDefault();
        var choices = allowed.Where(pair => pair.Key == 1 || pair.Key == large).ToArray();
        if (choices.Length == 0)
            return 0;

        double budgetSignal = target / (target + profile.ActionValue);
        double bias = 1 + 0.2 * (budgetSignal - 0.5);
        double chance = Math.Min(1, desire / choices[0].Key * bias);
        if (random.NextDouble() >= chance)
            return 0;
        return Draw(choices.Select(pair => (pair.Key, pair.Value / (1 + Math.Pow(pair.Key - desire, 2)))), 0, random);
    }

    private static bool CanAttach(ActionEffect effect) => !effect.IsHardCardLimit && effect.Kind != EffectKind.Intangible &&
        !ActionEffect.IsRareBuff(effect.Kind);

    private static double Jitter(double target, double volatility, Random random)
    {
        double spread = Math.Min(volatility, 1);
        return target * (1 + spread * (random.NextDouble() + random.NextDouble() - 1));
    }

    internal static double MatchingWeight(double value, double target, double spread)
    {
        double distance = (value - target) / spread;
        return 1 / (1 + distance * distance);
    }

    private ActionCandidate? Pick(IEnumerable<ActionCandidate> eligible, MonsterProfile profile, double target,
        Random random, bool maySkip = false)
    {
        double scale = Math.Max(1e-6, Math.Max(settings.MinimumSelectionSpread * settings.ChaosMultiplier,
            target * profile.Volatility));
        var candidates = eligible.Select(candidate => (Candidate: candidate,
            Preference: candidate.Weight * profile.IntentWeights.GetValueOrDefault(candidate.Style))).Where(x => x.Preference > 0).ToArray();
        if (candidates.Length == 0)
            return null;
        var weighted = candidates.Select(x => (x.Candidate, x.Preference * MatchingWeight(x.Candidate.Value, target, scale)));
        var weights = weighted.ToArray();
        double skip = maySkip ? weights.Sum(x => x.Item2) * candidates.Min(x => x.Candidate.Value) / target : 0;
        return Draw(weights, skip, random);
    }

    private static ActionCandidate Compose(List<ActionCandidate> parts)
    {
        if (parts.Count == 0)
            return new ActionCandidate("Wait", IntentStyle.Buff, false, 0, 0, Effects: []);
        if (parts.Count == 1)
            return parts[0];
        return new ActionCandidate(string.Join(" + ", parts.Select(part => part.Id)), parts[^1].Style,
            parts.Any(part => part.IsAttack), parts.Sum(part => part.Value), parts.Sum(part => part.Damage),
            Effects: parts.SelectMany(part => part.Effects ?? []).ToArray(), ResolvedValue: parts.Sum(part => part.ActualScore));
    }

    private static T? Draw<T>(IEnumerable<(T Choice, double Weight)> options, double skipWeight, Random random)
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
