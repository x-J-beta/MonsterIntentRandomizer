namespace MonsterIntentRandomizer;

public enum IntentStyle { Attack, Buff, Defend, PlayerDebuff }

public sealed record ActionCandidate(
    string Id, IntentStyle Style, bool IsAttack, double Value, double Damage, double Weight = 1,
    double? ActualValue = null, IReadOnlyList<ActionEffect>? Effects = null)
{
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

public sealed record PoolSettings(
    double ActionValueRetention, double AttackDesireRetention,
    double ActionCapacityInTurns, double AttackDesireCapacity,
    double OpeningPositiveCharge = 1, double OpeningNegativeRefund = 1,
    int OpeningAdjustmentTurns = 2, int OpeningRefundDelayTurns = 1);

public sealed record MonsterProfile(
    double ActionValue, double Volatility, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights,
    IReadOnlyDictionary<int, double>? HitWeights = null, IReadOnlyList<EffectKind>? EnabledEffects = null);

public readonly record struct Pools(double ActionValue, double AttackDesire,
    double OpeningAdjustment = 0, int AdjustmentTurns = 0, int AdjustmentDelay = 0);

public sealed record ActionPlan(ActionCandidate? Action, Pools Granted, Pools After, bool CanRandomize,
    double OpeningAdjustmentApplied = 0);

public sealed class ActionPlanner(PoolSettings settings)
{
    public Pools Start(MonsterProfile profile, double openingValue = 0)
    {
        double adjustment = -openingValue *
            (openingValue > 0 ? settings.OpeningPositiveCharge : settings.OpeningNegativeRefund);
        // An allowance for a weakened monster starts after its first eligible turn.
        return new Pools(profile.ActionValue * 0.5, 0, adjustment, settings.OpeningAdjustmentTurns,
            openingValue < 0 ? settings.OpeningRefundDelayTurns : 0);
    }

    public ActionPlan Plan(
        Pools previous, MonsterProfile profile, IEnumerable<ActionCandidate> candidates,
        Random random, bool canRandomize = true)
    {
        double actionCapacity = profile.ActionValue * settings.ActionCapacityInTurns;
        double retainedAction = previous.ActionValue * settings.ActionValueRetention;
        double retainedDesire = previous.AttackDesire * settings.AttackDesireRetention;
        // A skipped or scripted turn ages the balances without earning a new opportunity.
        if (!canRandomize)
        {
            var aged = previous with { ActionValue = retainedAction, AttackDesire = retainedDesire };
            return new ActionPlan(null, aged, aged, false);
        }

        double unjitteredGrant = profile.ActionValue *
            (profile.AttackFrequency + (1 - profile.AttackFrequency) * profile.NonAttackMultiplier);
        double variation = (random.NextDouble() + random.NextDouble() - 1) * profile.Volatility;
        double baseGrant = Math.Clamp(retainedAction + unjitteredGrant * (1 + variation), 0, actionCapacity);
        double adjustment = previous is { AdjustmentDelay: 0, AdjustmentTurns: > 0 }
            ? previous.OpeningAdjustment / previous.AdjustmentTurns : 0;
        double available = Math.Clamp(baseGrant + adjustment, 0, actionCapacity);
        double applied = available - baseGrant;
        // Keep any installment that would overflow queued until there is space.
        double remainingAdjustment = previous.OpeningAdjustment - applied;
        if (Math.Abs(remainingAdjustment) < 1e-9)
            remainingAdjustment = 0;
        int remainingTurns = previous.AdjustmentDelay > 0 ? previous.AdjustmentTurns :
            Math.Max(remainingAdjustment == 0 ? 0 : 1, previous.AdjustmentTurns - 1);
        var granted = new Pools(
            available,
            Math.Clamp(retainedDesire + profile.AttackFrequency,
                -settings.AttackDesireCapacity, settings.AttackDesireCapacity),
            remainingAdjustment,
            remainingTurns,
            Math.Max(0, previous.AdjustmentDelay - 1));

        double attackProbability = profile.AttackFrequency switch
        {
            <= 0 => 0,
            >= 1 => 1,
            _ => Math.Clamp(granted.AttackDesire, 0, 1)
        };
        bool preferAttack = random.NextDouble() < attackProbability;
        var eligible = candidates.Where(candidate =>
            candidate.Value > 0 && candidate.Value <= granted.ActionValue + 1e-9 && candidate.Weight > 0 &&
            profile.IntentWeights.GetValueOrDefault(candidate.Style) > 0 &&
            (profile.AttackFrequency > 0 || !candidate.IsAttack) &&
            (profile.AttackFrequency < 1 || candidate.IsAttack)).ToArray();

        ActionCandidate? action = Pick(eligible, preferAttack, profile, granted.ActionValue, random)
            ?? Pick(eligible, !preferAttack, profile, granted.ActionValue, random);
        if (action is null)
        {
            return new ActionPlan(null, granted, granted, true, applied);
        }

        return new ActionPlan(action, granted, Settle(granted, action.Value, action.IsAttack), true, applied);
    }

    // The caller uses this when a random opportunity fell back to a vanilla move.
    // A native move may spend more than the available balance, but this never creates negative credit.
    public Pools Settle(Pools granted, double value, bool isAttack) => granted with
    {
        ActionValue = Math.Max(0, granted.ActionValue - value),
        AttackDesire = Math.Clamp(granted.AttackDesire - (isAttack ? 1 : 0),
            -settings.AttackDesireCapacity, settings.AttackDesireCapacity)
    };

    private static ActionCandidate? Pick(
        IEnumerable<ActionCandidate> eligible, bool isAttack, MonsterProfile profile,
        double available, Random random)
    {
        double tierValue = profile.ActionValue * (isAttack ? 1 : profile.NonAttackMultiplier);
        double target = Math.Min(tierValue, available);
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
