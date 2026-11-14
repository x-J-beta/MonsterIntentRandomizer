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
    double InitialAttackDesire = 0, double BalanceTargetWeight = 0.5, double MinimumSelectionSpread = 0.5);

public sealed record MonsterProfile(
    double ActionValue, double Volatility, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights,
    IReadOnlyDictionary<int, double>? HitWeights = null, IReadOnlyDictionary<EffectKind, double>? EffectWeights = null,
    IReadOnlyList<StatusPlacement>? CardPlacements = null)
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
}

public readonly record struct Pools(double ActionValue, double AttackDesire);

public sealed record ActionPlan(ActionCandidate? Action, Pools Granted, Pools After, bool CanRandomize);

public sealed class ActionPlanner(PoolSettings settings)
{
    public Pools Start(MonsterProfile profile, double openingValue = 0) =>
        new(profile.ActionValue * settings.InitialActionValueShare - openingValue, settings.InitialAttackDesire);

    public ActionPlan Plan(
        Pools previous, MonsterProfile profile, IEnumerable<ActionCandidate> candidates,
        Random random, bool canRandomize = true)
    {
        double retainedAction = previous.ActionValue * settings.ActionValueRetention;
        double retainedDesire = previous.AttackDesire * settings.AttackDesireRetention;
        // A skipped or scripted turn ages the balances without earning a new opportunity.
        if (!canRandomize)
        {
            var aged = previous with { ActionValue = retainedAction, AttackDesire = retainedDesire };
            return new ActionPlan(null, aged, aged, false);
        }

        double averageHits = profile.ExpectedHitsPerAttack;
        double attackShare = profile.ExpectedAttackTurnShare;
        double unjitteredGrant = profile.ActionValue *
            (attackShare + (1 - attackShare) * profile.NonAttackMultiplier);
        double variation = (random.NextDouble() + random.NextDouble() - 1) * profile.Volatility;
        var granted = new Pools(
            retainedAction + unjitteredGrant * (1 + variation),
            retainedDesire + profile.AttackFrequency);

        double attackProbability = profile.AttackFrequency > 0 && averageHits > 0
            ? Math.Clamp(granted.AttackDesire / averageHits, 0, 1) : 0;
        bool preferAttack = random.NextDouble() < attackProbability;
        var eligible = candidates.Where(candidate =>
            candidate.Value > 0 && candidate.Weight > 0 &&
            profile.IntentWeights.GetValueOrDefault(candidate.Style) > 0 &&
            (profile.AttackFrequency > 0 || !candidate.IsAttack)).ToArray();

        ActionCandidate? action = Pick(eligible, preferAttack, profile, granted.ActionValue, random)
            ?? Pick(eligible, !preferAttack, profile, granted.ActionValue, random);
        if (action is null)
        {
            return new ActionPlan(null, granted, granted, true);
        }

        return new ActionPlan(action, granted, Settle(granted, action.Value, action.AttackHits), true);
    }

    // Also settles the actual original move after a fallback.
    public Pools Settle(Pools granted, double value, int attackHits) =>
        new(granted.ActionValue - value, granted.AttackDesire - attackHits);

    private ActionCandidate? Pick(
        IEnumerable<ActionCandidate> eligible, bool isAttack, MonsterProfile profile,
        double available, Random random)
    {
        double tierValue = profile.ActionValue * (isAttack ? 1 : profile.NonAttackMultiplier);
        double target = Math.Max(0, tierValue + (available - tierValue) * settings.BalanceTargetWeight);
        double scale = Math.Max(settings.MinimumSelectionSpread, tierValue * profile.Volatility);
        var weighted = eligible.Where(candidate => candidate.IsAttack == isAttack).Select(candidate =>
        {
            double distance = Math.Abs(candidate.Value - target);
            double normalizedDistance = distance / scale;
            double weight = candidate.Weight * profile.IntentWeights[candidate.Style] /
                (1 + normalizedDistance * normalizedDistance);
            return (Candidate: candidate, Weight: weight);
        }).ToArray();
        double roll = random.NextDouble() * weighted.Sum(entry => entry.Weight);
        foreach (var entry in weighted)
        {
            roll -= entry.Weight;
            if (roll < 0)
            {
                return entry.Candidate;
            }
        }
        return weighted.LastOrDefault().Candidate;
    }
}
