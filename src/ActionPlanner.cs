namespace MonsterIntentRandomizer;

public enum IntentStyle { Attack, Buff, Defend, PlayerDebuff }

public sealed record ActionCandidate(
    string Id, IntentStyle Style, bool IsAttack, double Value, double Damage, double Weight = 1,
    double? ActualValue = null)
{
    public static ActionCandidate Attack(
        string id, int hitCount, int baseDamagePerHit, int strength, bool weak, double weight)
    {
        double baseDamage = hitCount * baseDamagePerHit;
        double actualDamage = hitCount * CombatMath.AttackDamagePerHit(baseDamagePerHit, strength, weak);
        return new ActionCandidate(id, IntentStyle.Attack, true, baseDamage, actualDamage, weight, actualDamage);
    }
}

internal static class CombatMath
{
    public static double AttackDamagePerHit(double baseDamage, int strength = 0, bool weak = false) =>
        Math.Floor(Math.Max(0, baseDamage + strength) * (weak ? 0.75 : 1));
}

public sealed record PoolSettings(
    double ActionValueRetention, double AttackDesireRetention,
    double ActionCapacityInTurns, double AttackDesireCapacity);

public sealed record MonsterProfile(
    double ActionValue, double Volatility, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights);

public readonly record struct Pools(double ActionValue, double AttackDesire);

public sealed record ActionPlan(ActionCandidate? Action, Pools Granted, Pools After, bool CanRandomize);

public sealed class ActionPlanner(PoolSettings settings)
{
    public ActionPlan Plan(
        Pools previous, MonsterProfile profile, IEnumerable<ActionCandidate> candidates,
        double damageLimit, Random random, bool canRandomize = true)
    {
        double actionCapacity = profile.ActionValue * settings.ActionCapacityInTurns;
        double retainedAction = previous.ActionValue * settings.ActionValueRetention;
        double retainedDesire = previous.AttackDesire * settings.AttackDesireRetention;
        // A skipped or scripted turn ages the balances without earning a new opportunity.
        if (!canRandomize)
        {
            var aged = new Pools(retainedAction, retainedDesire);
            return new ActionPlan(null, aged, aged, false);
        }

        double unjitteredGrant = profile.ActionValue *
            (profile.AttackFrequency + (1 - profile.AttackFrequency) * profile.NonAttackMultiplier);
        double variation = (random.NextDouble() + random.NextDouble() - 1) * profile.Volatility;
        var granted = new Pools(
            Math.Clamp(retainedAction + unjitteredGrant * (1 + variation), 0, actionCapacity),
            Math.Clamp(retainedDesire + profile.AttackFrequency,
                -settings.AttackDesireCapacity, settings.AttackDesireCapacity));

        double attackProbability = profile.AttackFrequency switch
        {
            <= 0 => 0,
            >= 1 => 1,
            _ => Math.Clamp(granted.AttackDesire, 0, 1)
        };
        bool preferAttack = random.NextDouble() < attackProbability;
        var eligible = candidates.Where(candidate =>
            candidate.Value > 0 && candidate.Value <= granted.ActionValue + 1e-9 &&
            candidate.Damage <= damageLimit && candidate.Weight > 0 &&
            profile.IntentWeights.GetValueOrDefault(candidate.Style) > 0 &&
            (profile.AttackFrequency > 0 || !candidate.IsAttack) &&
            (profile.AttackFrequency < 1 || candidate.IsAttack)).ToArray();

        ActionCandidate? action = Pick(eligible, preferAttack, profile, granted.ActionValue, random)
            ?? Pick(eligible, !preferAttack, profile, granted.ActionValue, random);
        if (action is null)
        {
            return new ActionPlan(null, granted, granted, true);
        }

        return new ActionPlan(action, granted, Settle(granted, action.Value, action.IsAttack), true);
    }

    // The caller uses this when a random opportunity fell back to a vanilla move.
    // A native move may spend more than the available balance, but this never creates negative credit.
    public Pools Settle(Pools granted, double value, bool isAttack) => new(
        Math.Max(0, granted.ActionValue - value),
        Math.Clamp(granted.AttackDesire - (isAttack ? 1 : 0),
            -settings.AttackDesireCapacity, settings.AttackDesireCapacity));

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
            double weight = candidate.Weight * profile.IntentWeights[candidate.Style] /
                (1 + distance / scale);
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
