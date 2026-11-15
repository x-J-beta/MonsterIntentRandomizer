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
    double ActionValueMultiplier = 1, double ChaosMultiplier = 1,
    double DrawBudgetMultiplier = 1.3, double LocalOverdraftShare = 0.2, int NonAttackCandidates = 5,
    double UnusedAttackBudgetShare = 0.5, double FullChaosBudgetAt = 5,
    IReadOnlyDictionary<IntentStyle, double>? ChaosBudgetWeights = null)
{
    internal double WeightPower => 1 - Math.Clamp((ChaosMultiplier - 1) / (FullChaosBudgetAt - 1), 0, 1);

    private static readonly IReadOnlyDictionary<IntentStyle, double> DefaultChaosWeights =
        new Dictionary<IntentStyle, double>
        {
            [IntentStyle.Attack] = 40, [IntentStyle.Buff] = 20, [IntentStyle.Defend] = 20, [IntentStyle.PlayerDebuff] = 20
        };

    public MonsterProfile Adjust(MonsterProfile profile)
    {
        var adjusted = profile with
        {
            ActionValue = profile.ActionValue * ActionValueMultiplier,
            Volatility = profile.Volatility * ChaosMultiplier
        };
        double mix = 1 - WeightPower;
        if (mix == 0)
            return adjusted;

        var target = ChaosBudgetWeights ?? DefaultChaosWeights;
        var styles = Enum.GetValues<IntentStyle>();
        double configuredTotal = styles.Sum(style => Math.Max(0, profile.IntentWeights.GetValueOrDefault(style)));
        double targetTotal = styles.Sum(style => target.GetValueOrDefault(style));
        double targetAttack = target.GetValueOrDefault(IntentStyle.Attack) / targetTotal;
        return adjusted with
        {
            AttackSpend = profile.AttackFrequency > 0
                ? (1 - mix) * profile.BudgetedAttackShare + mix * targetAttack : 0,
            IntentWeights = styles.ToDictionary(style => style, style =>
                (1 - mix) * Math.Max(0, profile.IntentWeights.GetValueOrDefault(style)) / Math.Max(configuredTotal, double.Epsilon)
                + mix * target.GetValueOrDefault(style) / targetTotal)
        };
    }
}

public sealed record MonsterProfile(
    double ActionValue, double Volatility, double AttackFrequency, double NonAttackMultiplier,
    double BaseExpectedSurvivalTurns, IReadOnlyDictionary<IntentStyle, double> IntentWeights,
    IReadOnlyDictionary<EffectKind, double>? EffectWeights = null,
    IReadOnlyList<StatusPlacement>? CardPlacements = null, double? AttackSpend = null, double PlayerActionValue = 0,
    double CurrentHp = 0, int RoomsEntered = 0, double SingleHitPreference = 1, double DesireHitPreference = 1)
{
    public double HpPerSurvivalTurn => BaseExpectedSurvivalTurns > 0 ? CurrentHp / BaseExpectedSurvivalTurns : 0;

    internal int CapAmount(int maximum, double? hpPerTurnFactor) => hpPerTurnFactor is double factor
        ? (int)Math.Clamp(Math.Floor(HpPerSurvivalTurn * factor + 1e-9), 0, maximum)
        : maximum;

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

        double turnShare = Math.Clamp(profile.AttackFrequency, 0, 1);
        double income = profile.ActionValue * (turnShare + (1 - turnShare) * profile.NonAttackMultiplier);
        var granted = new Pools(retainedAction + income, retainedDesire + profile.AttackFrequency);
        double target = Math.Max(0, profile.ActionValue + retainedAction * settings.BalanceTargetWeight) * settings.DrawBudgetMultiplier;
        double attackFraction = profile.BudgetedAttackShare;
        double attackTarget = Jitter(target * attackFraction, profile.Volatility, random);
        var eligible = candidates.Where(candidate => candidate.Value > 0 && candidate.Weight > 0).ToArray();
        var fixedHits = buildAttack == null
            ? eligible.Where(candidate => candidate.IsAttack).Select(candidate => candidate.AttackHits).ToHashSet()
            : null;
        int hits = DrawHits(profile, granted.AttackDesire, attackFraction, fixedHits, random);
        var attack = hits > 0 ? (buildAttack != null ? buildAttack(hits, attackTarget) :
            Pick(eligible.Where(candidate => candidate.AttackHits == hits), profile, attackTarget, random)) : null;
        double allocatedOthers = target * (1 - attackFraction +
            (attack == null ? attackFraction * settings.UnusedAttackBudgetShare : 0));
        double otherTarget = Jitter(allocatedOthers, profile.Volatility, random);
        double overdraft = profile.ActionValue * settings.LocalOverdraftShare;
        var parts = new List<ActionCandidate>(2);
        if (attack != null)
            parts.Add(attack);

        var extras = Shortlist(eligible.Where(candidate => !candidate.IsAttack && candidate.Effects is { Count: 1 } &&
            (attack == null || CanAttach(candidate.Effects[0]))), profile, otherTarget, overdraft, attack == null, random);
        while (parts.Count < 2 && otherTarget > 0 && extras.Count > 0)
        {
            var extra = Pick(extras.Where(candidate => candidate.Value <= otherTarget + overdraft), profile, otherTarget,
                random, maySkip: true);
            if (extra == null)
                break;
            parts.Add(extra);
            otherTarget -= extra.Value;
            var chosen = extra.Effects![0];
            extras.RemoveAll(candidate => candidate.Effects![0].Kind == chosen.Kind ||
                (chosen.IsSpecialDebuff && candidate.Effects[0].IsSpecialDebuff));
        }

        var action = Compose(parts);
        return new ActionPlan(action, granted, Settle(granted, action.Value, action.AttackHits), true);
    }

    public Pools Settle(Pools granted, double value, int attackHits) =>
        new(granted.ActionValue - value, granted.AttackDesire - attackHits);

    private int DrawHits(MonsterProfile profile, double desire, double attackFraction, HashSet<int>? fixedHits, Random random)
    {
        if (desire < 0 || attackFraction <= 0 || profile.AttackFrequency <= 0 ||
            profile.IntentWeights.GetValueOrDefault(IntentStyle.Attack) <= 0)
            return 0;
        int desireHits = (int)Math.Clamp(Math.Floor(desire), 1, int.MaxValue);
        int large = fixedHits?.Where(hits => hits <= desireHits).DefaultIfEmpty(1).Max() ?? desireHits;
        var choices = new List<(int Hits, double Weight)>(2);
        if (profile.SingleHitPreference > 0 && (fixedHits == null || fixedHits.Contains(1)))
            choices.Add((1, profile.SingleHitPreference));
        if (large > 1 && profile.DesireHitPreference > 0)
            choices.Add((large, profile.DesireHitPreference));
        double waitChance = 5 / (desire + 10);
        if (choices.Count == 0 || random.NextDouble() < waitChance)
            return 0;
        return Draw(choices.Select(choice => (choice.Hits, Math.Pow(choice.Weight, settings.WeightPower))), 0, random);
    }

    private static bool CanAttach(ActionEffect effect) => !effect.IsHardCardLimit && effect.Kind != EffectKind.Intangible &&
        !ActionEffect.IsRareBuff(effect.Kind);

    private List<ActionCandidate> Shortlist(IEnumerable<ActionCandidate> options, MonsterProfile profile, double budget,
        double overdraft, bool twoSlots, Random random)
    {
        var shortlist = new List<ActionCandidate>();
        if (budget <= 0)
            return shortlist;

        var available = options.Where(candidate => candidate.Value <= budget + overdraft).ToList();
        while (shortlist.Count < settings.NonAttackCandidates && available.Count > 0)
        {
            double target = twoSlots && shortlist.Count % 2 == 1 ? budget * 0.5 : budget;
            var chosen = Pick(available, profile, target, random);
            if (chosen == null)
                break;
            var kind = chosen.Effects![0].Kind;
            double familyWeight = available.Where(candidate => candidate.Effects![0].Kind == kind).Sum(candidate => candidate.Weight);
            shortlist.Add(chosen with { Weight = familyWeight });
            available.RemoveAll(candidate => candidate.Effects![0].Kind == kind);
        }
        return shortlist;
    }

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
        var candidates = eligible.GroupBy(candidate => candidate.Style)
            .Where(group => profile.IntentWeights.GetValueOrDefault(group.Key) > 0)
            .SelectMany(group =>
            {
                var families = group.GroupBy(candidate => candidate.Effects?[0].Kind ?? EffectKind.Attack)
                    .Select(family => (Members: family, Raw: family.Sum(candidate => candidate.Weight))).ToArray();
                double exponent = settings.WeightPower;
                double categoryWeight = profile.IntentWeights[group.Key] / families.Sum(family => Math.Pow(family.Raw, exponent));
                return families.SelectMany(family => family.Members.Select(candidate => (Candidate: candidate,
                    Weight: candidate.Weight / family.Raw * Math.Pow(family.Raw, exponent) * categoryWeight *
                        MatchingWeight(candidate.Value, target, scale))));
            }).ToArray();
        if (candidates.Length == 0)
            return null;
        double skip = maySkip ? candidates.Sum(x => x.Weight) * candidates.Min(x => x.Candidate.Value) / target : 0;
        return Draw(candidates, skip, random);
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
