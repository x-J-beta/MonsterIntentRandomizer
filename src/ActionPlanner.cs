namespace MonsterIntentRandomizer;

public enum IntentStyle { Attack, Buff, Defend, PlayerDebuff }

public sealed record ActionCandidate(
    string Id, IntentStyle Style, bool IsAttack, double Value, double Damage, double Weight = 1,
    double? ActualValue = null, IReadOnlyList<ActionEffect>? Effects = null)
{
    public int AttackHits => Effects?.Where(effect => effect.Kind == EffectKind.Attack).Sum(effect => effect.Hits)
        ?? (IsAttack ? 1 : 0);

    public static ActionCandidate Attack(
        string id, int hitCount, int baseDamagePerHit, int strength, bool weak, double weight)
    {
        double baseDamage = hitCount * baseDamagePerHit;
        double actualDamage = hitCount * CombatMath.AttackDamagePerHit(baseDamagePerHit, strength, weak);
        return new ActionCandidate(id, IntentStyle.Attack, true, baseDamage, actualDamage, weight, actualDamage,
            [new ActionEffect(EffectKind.Attack, baseDamagePerHit, hitCount)]);
    }
}

internal static class CombatMath
{
    public static double AttackDamagePerHit(double baseDamage, int strength = 0, bool weak = false) =>
        Math.Floor(Math.Max(0, baseDamage + strength) * (weak ? 0.75 : 1));
}

public sealed record PoolSettings(double ActionValueRetention, double AttackDesireRetention);

public sealed record MonsterProfile(
    double ActionValue, double Volatility, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights,
    IReadOnlyDictionary<int, double>? HitWeights = null, IReadOnlyList<EffectKind>? EnabledEffects = null)
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
        new(profile.ActionValue * 0.5 - openingValue, 0);

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

    private static ActionCandidate? Pick(
        IEnumerable<ActionCandidate> eligible, bool isAttack, MonsterProfile profile,
        double available, Random random)
    {
        double tierValue = profile.ActionValue * (isAttack ? 1 : profile.NonAttackMultiplier);
        double target = Math.Max(0, (tierValue + available) / 2);
        double scale = Math.Max(0.5, tierValue * profile.Volatility);
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
