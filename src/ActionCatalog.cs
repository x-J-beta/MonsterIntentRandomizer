namespace MonsterIntentRandomizer;

public enum EffectKind
{
    Attack, Block, Heal, Strength, Ritual, Intangible, Weak, Vulnerable, Tangled,
    Ringing, StrengthDown, DexterityDown, Dazed, Wound, Burn, Beckon,
    PersonalHive, VitalSpark, WitheringPresence, Envenom, PaperCuts, Smoggy, Tender, Frail, Poison, Shrink,
    Thorns, TemporaryThorns, Soulbind, DrawDown, EnergyDown, Regen, Wait,
    HardenedShell, HardToKill, Plating, Wither, Amnesia, MindRot, Sadness, Infection, Sloth,
    Toxic, Soot, Disintegration, Debris, Void, WasteAway, Slimed, Artifact, Barricade, Slippery, Vigor, Enrage, PainfulStabs,
    SteamEruption, Dampen, Hex, Enthralled, Normality, BadLuck, Debt
}

public enum EffectTarget { Self, Player }
public enum StatusPile { Draw, Discard, Hand, Top }

public sealed record StatusCardDestination(StatusPile Pile, int Amount);

public sealed record ActionEffect(EffectKind Kind, int Amount, int Hits = 1, StatusPile? Pile = null,
    IReadOnlyList<StatusCardDestination>? SplitDestinations = null, ThresholdPrice? Threshold = null,
    EffectKind? GeneratedCard = null, bool Unique = false)
{
    public IReadOnlyList<StatusCardDestination> CardDestinations => SplitDestinations ?? [new(Pile ?? StatusPile.Discard, Amount)];
    public EffectTarget Target => TargetFor(Kind);
    public static EffectTarget TargetFor(EffectKind kind) => kind is EffectKind.Block or EffectKind.Heal or
        EffectKind.Regen or EffectKind.Strength or EffectKind.Ritual or EffectKind.Thorns or EffectKind.TemporaryThorns
        or EffectKind.Artifact or EffectKind.Vigor or EffectKind.Enrage or EffectKind.SteamEruption || IsDefensiveBuff(kind) || IsRareBuff(kind)
        ? EffectTarget.Self : EffectTarget.Player;
    public bool IsInsertedCard => IsCard(Kind) || IsCurseCard(Kind);
    public static bool IsMonsterBuff(EffectKind kind) => TargetFor(kind) == EffectTarget.Self &&
        kind is not (EffectKind.Block or EffectKind.Heal);
    public static bool IsDefensiveBuff(EffectKind kind) => kind is EffectKind.Intangible or EffectKind.Plating or
        EffectKind.Barricade or EffectKind.Slippery or EffectKind.HardenedShell or EffectKind.HardToKill or
        EffectKind.Regen or EffectKind.Artifact or EffectKind.Thorns or EffectKind.TemporaryThorns;
    internal static IntentStyle StyleFor(EffectKind kind) => kind switch
    {
        EffectKind.Attack => IntentStyle.Attack,
        EffectKind.Block or EffectKind.Heal => IntentStyle.Defend,
        _ when IsDefensiveBuff(kind) => IntentStyle.Defend,
        _ when IsMonsterBuff(kind) => IntentStyle.Buff,
        _ => IntentStyle.PlayerDebuff
    };
    public static bool IsCard(EffectKind kind) => kind is EffectKind.Dazed or EffectKind.Wound or EffectKind.Burn or EffectKind.Beckon or
        EffectKind.Wither or EffectKind.Amnesia or EffectKind.MindRot or EffectKind.Sadness or EffectKind.Infection or
        EffectKind.Sloth or EffectKind.Toxic or EffectKind.Soot or EffectKind.Disintegration or EffectKind.Debris or
        EffectKind.Void or EffectKind.WasteAway or EffectKind.Slimed;
    internal static bool IsCurseCard(EffectKind kind) =>
        kind is EffectKind.Enthralled or EffectKind.Normality or EffectKind.BadLuck or EffectKind.Debt;
    public static bool UsesThreshold(EffectKind kind) =>
        kind is EffectKind.HardenedShell or EffectKind.HardToKill or EffectKind.WitheringPresence;
    internal static bool IsPlayerEffect(EffectKind kind) =>
        kind is not (EffectKind.Attack or EffectKind.Wait) && TargetFor(kind) == EffectTarget.Player;
    public static bool IsRareBuff(EffectKind kind) => kind is EffectKind.PersonalHive or EffectKind.VitalSpark or
        EffectKind.WitheringPresence or EffectKind.Envenom or EffectKind.PaperCuts or EffectKind.PainfulStabs;
}

public sealed record StatusPlacement(EffectKind Card, StatusPile Pile, double Weight = 1)
{
    public override string ToString() => $"{Card}:{Pile}";
}

public sealed record StatusPlacementValues(double Draw = 1, double Discard = 0.75, double Top = 1.33)
{
    public double For(StatusPile pile, double combatTurns)
    {
        double longFightShare = Math.Clamp((combatTurns - 1) / 4, 0, 1);
        return pile switch
        {
            StatusPile.Draw => Draw * (0.60 + 0.40 * longFightShare),
            StatusPile.Discard => Discard * (0.20 + 0.80 * longFightShare),
            StatusPile.Top => Top,
            _ => throw new ArgumentOutOfRangeException(nameof(pile))
        };
    }
}

public sealed record ActionContext(
    double ExpectedDamageAgainstMonster,
    int Strength = 0,
    bool Weakened = false,
    double ExistingBlock = 0,
    bool AllowHardCardLimit = true, double MissingHp = 0,
    IReadOnlySet<EffectKind>? ActiveEffects = null,
    int SpecialStatusesRemaining = 1, int Rooms = 0, IReadOnlyDictionary<EffectKind, int>? Thresholds = null,
    IReadOnlyDictionary<EffectKind, EffectKind>? GeneratedCards = null, int EnemyCount = 1);

public sealed record ActionValuationSettings(
    ActionValuation Valuation, double WeakDamageReduction, int MinimumBaseDamagePerHit,
    StatusPlacementValues? PlacementValues = null,
    int MaxHardCardLimits = 1,
    IReadOnlyDictionary<EffectKind, RoomWeight>? Weights = null,
    IReadOnlyDictionary<EffectKind, int>? MinimumAmounts = null,
    IReadOnlyDictionary<EffectKind, EffectPolicy>? Policies = null,
    IReadOnlyDictionary<EffectKind, StatusValue>? Buffs = null, double ChaosProgress = 0,
    IReadOnlyDictionary<EffectKind, double>? CardPlayerActionValueRatios = null, double CardStartingPlayerActionValue = 16,
    IReadOnlyDictionary<EffectKind, double>? LaterMonsterPercent = null,
    IReadOnlyDictionary<EffectKind, double>? MaximumCostMultipliers = null)
{
    internal double MaxCostMultiplier(EffectKind kind) => MaximumCostMultipliers?.GetValueOrDefault(kind, 1) ?? 1;
    internal double CardUnitValue(EffectKind card, MonsterProfile profile, int amount = 1)
    {
        double baseline = CardPlayerActionValueRatios?.GetValueOrDefault(EffectKind.Dazed, 0.05) ?? 0.05;
        double ratio = Math.Max(baseline, CardPlayerActionValueRatios?.GetValueOrDefault(card) ?? 0);
        double baseValue = Valuation.Evaluate(card, profile, amount) / amount;
        return (Math.Max(0, baseValue - ratio * CardStartingPlayerActionValue) + ratio * profile.PlayerActionValue) *
            LaterMonsterMultiplier(card, profile);
    }

    internal double LaterMonsterMultiplier(EffectKind kind, MonsterProfile profile) =>
        profile.LaterMonsterMultiplier(LaterMonsterPercent?.GetValueOrDefault(kind) ?? 0);

    public EffectPolicy PolicyFor(EffectKind kind, string? monsterId = null) => Buffs?.GetValueOrDefault(kind) is { } buff
        ? new EffectPolicy(buff.Stackable || TriggeredCardBuffs.UsesNative(kind, monsterId), buff.Unique, EffectTarget.Self)
        : Policies?.GetValueOrDefault(kind) ?? EffectPolicy.Default(kind);
}

internal sealed record ActionOffer(EffectKind Kind, EffectPolicy Policy, double Weight, double Favorite,
    double MaximumCost, Func<double, double, ActionEffect?, Random, ActionCandidate?> Create)
{
    internal IntentStyle Style => ActionEffect.StyleFor(Kind);
    internal ActionCandidate? Quote(double target, double limit, ActionEffect? previous, Random random) =>
        Create(target, Math.Min(MaximumCost, limit), previous, random);
}

public sealed record ActionHistory(int AddedStrength = 0, int AddedRitual = 0, int RitualStrength = 0)
{
    // Record only actions which the caller actually executed.
    public ActionHistory Record(ActionCandidate action)
    {
        var effects = action.Effects;
        int addedRitual = effects.Where(effect => effect.Kind == EffectKind.Ritual).Sum(effect => effect.Amount);
        return new ActionHistory(
            AddedStrength + effects.Where(effect => effect.Kind == EffectKind.Strength).Sum(effect => effect.Amount),
            AddedRitual + addedRitual, RitualStrength + AddedRitual);
    }
}

public sealed class ActionCatalog(ActionValuationSettings settings)
{
    internal static bool CanFavor(EffectKind kind) => Enum.IsDefined(kind) &&
        kind is not (EffectKind.Attack or EffectKind.Wait);

    internal IEnumerable<ActionOffer> Generate(MonsterProfile profile, ActionContext context, string? monsterId = null,
        double extraBuffHits = 0)
    {
        var buffProfile = extraBuffHits == 0 ? profile : profile with { AttackFrequency = profile.AttackFrequency + extraBuffHits };
        foreach (var (kind, buff) in settings.Buffs?.AsEnumerable() ?? [])
        {
            if (!EffectPolicy.CanTargetMonster(kind, monsterId) ||
                profile.AllowedBuffs is { } allowed && !allowed.Contains(kind))
                continue;
            var (weight, favorite) = Weights(kind, buff.RoomWeight);
            var policy = settings.PolicyFor(kind, monsterId);
            double maximumCost = buff.MaximumCost(profile);
            if (!IsAvailable(kind, policy, weight, favorite) || maximumCost <= 0)
                continue;
            bool threshold = ActionEffect.UsesThreshold(kind);
            int current = threshold ? context.Thresholds?.GetValueOrDefault(kind) ?? 0 : 0;
            if (current > 0 && !policy.Stackable)
                continue;
            EffectKind? existingCard = context.GeneratedCards != null && context.GeneratedCards.TryGetValue(kind, out var card)
                ? card : null;
            yield return new ActionOffer(kind, policy, weight, favorite, maximumCost, (target, limit, previous, random) =>
            {
                int before = threshold && previous?.Kind == kind ? previous.Amount : current;
                int lastImprovement = before > 0 ? before - 1 : int.MaxValue;
                var choices = new List<(ActionCandidate Choice, double Weight)>();
                var appliedCard = previous?.Kind == kind ? previous.GeneratedCard : existingCard;
                foreach (var variant in TriggeredCardBuffs.Variants(kind, buff, settings, context.Rooms, appliedCard, monsterId))
                {
                    double scale = TriggeredCardBuffs.Scale(kind, variant.Card, settings, profile);
                    double Price(int dose) => scale * (threshold
                        ? OpeningValuation.ThresholdCost(buff, buffProfile, before, dose)
                        : OpeningValuation.Evaluate(buff, buffProfile, dose));
                    int amount = SpendAmount.Closest(buff.MinAmount, lastImprovement, target, limit, Price, descending: threshold);
                    if (amount == 0)
                        continue;
                    double value = Price(amount);
                    var effect = new ActionEffect(kind, amount, Unique: policy.Unique, GeneratedCard: variant.Card,
                        Threshold: threshold ? new ThresholdPrice(buff, buffProfile, value, scale) : null);
                    choices.Add((Candidate(effect, value), variant.Weight));
                }
                return ActionPlanner.Draw(choices, 0, random);
            });
        }
        foreach (var kind in settings.Valuation.Effects)
        {
            if (ActionEffect.IsMonsterBuff(kind) || kind is EffectKind.Attack or EffectKind.Wait)
                continue;
            var (weight, favorite) = Weights(kind, settings.Weights?.GetValueOrDefault(kind));
            var policy = settings.PolicyFor(kind, monsterId);
            double maximumCost = profile.ActionValue * settings.MaxCostMultiplier(kind);
            if (!IsAvailable(kind, policy, weight, favorite) || maximumCost <= 0 ||
                (kind is EffectKind.Ringing or EffectKind.Smoggy && (!context.AllowHardCardLimit || settings.MaxHardCardLimits <= 0)))
                continue;

            int minimum = settings.MinimumAmounts?.GetValueOrDefault(kind, 1) ?? 1;
            bool curse = ActionEffect.IsCurseCard(kind);
            yield return new ActionOffer(kind, policy, weight, favorite, maximumCost, Quote);

            ActionCandidate? Quote(double target, double limit, ActionEffect? previous, Random random)
            {
                if (ActionEffect.IsCard(kind))
                    return DrawCards(kind, minimum, target, limit, policy.Unique, random);

                double effectiveLimit = kind switch
                {
                    EffectKind.Block => context.ExpectedDamageAgainstMonster - context.ExistingBlock,
                    EffectKind.Heal => context.MissingHp,
                    _ => 0
                };
                if (previous?.Kind == kind)
                    effectiveLimit -= previous.Amount;
                effectiveLimit = Math.Max(0, effectiveLimit);
                double scale = settings.LaterMonsterMultiplier(kind, profile);
                double Price(int amount) => scale * (curse
                    ? amount * settings.Valuation.Evaluate(kind, profile, 1)
                    : settings.Valuation.Evaluate(kind, profile, amount, effectiveAmount: Math.Min(amount, effectiveLimit)));
                int amount = SpendAmount.Closest(minimum, int.MaxValue, target, limit, Price);
                return amount == 0 ? null : Candidate(new ActionEffect(kind, amount, Unique: policy.Unique,
                    Pile: curse ? StatusPile.Hand : null), Price(amount));
            }
        }

        (double Base, double Favorite) Weights(EffectKind kind, RoomWeight? weight) =>
            (weight?.At(context.Rooms, settings.ChaosProgress) ?? 1, profile.Favorites?.GetValueOrDefault(kind) ?? 0);

        bool IsAvailable(EffectKind kind, EffectPolicy policy, double weight, double favorite) =>
            (weight > 0 || favorite > 0) && (context.SpecialStatusesRemaining > 0 || !policy.Unique) &&
            context.ActiveEffects?.Contains(kind) != true;

        ActionCandidate? DrawCards(EffectKind kind, int minimum, double target, double limit, bool unique, Random random)
        {
            var placements = profile.CardPlacements?.Where(place => place.Card == kind && place.Pile != StatusPile.Hand).ToArray();
            if (placements is not { Length: > 0 })
                placements = [new(kind, StatusPile.Top), new(kind, StatusPile.Draw), new(kind, StatusPile.Discard)];
            var available = placements.Where(place => place.Weight > 0)
                .GroupBy(place => place.Pile)
                .Select(group => new StatusPlacement(kind, group.Key, group.Sum(place => place.Weight)))
                .OrderByDescending(place => place.Weight).ToArray();
            var variants = new List<(ActionCandidate Choice, double Weight)>();
            var values = settings.PlacementValues ?? new StatusPlacementValues();
            for (int mask = 1; mask < (1 << available.Length); mask++)
            {
                var selected = available.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                int count = selected.Length;
                double Price(int amount)
                {
                    double placementValue = 0;
                    for (int i = 0; i < count; i++)
                        placementValue += (amount / count + (i < amount % count ? 1 : 0)) *
                            values.For(selected[i].Pile, profile.ExpectedCombatTurns);
                    return settings.CardUnitValue(kind, profile, amount) * placementValue;
                }
                int amount = SpendAmount.Closest(Math.Max(minimum, count), int.MaxValue, target, limit, Price);
                if (amount == 0)
                    continue;
                var destinations = selected.Select((placement, index) => new StatusCardDestination(placement.Pile,
                    amount / count + (index < amount % count ? 1 : 0))).ToArray();
                var effect = count == 1 ? new ActionEffect(kind, amount, Pile: destinations[0].Pile, Unique: unique)
                    : new ActionEffect(kind, amount, SplitDestinations: destinations, Unique: unique);
                variants.Add((Candidate(effect, Price(amount)), selected.Average(place => place.Weight)));
            }
            return ActionPlanner.Draw(variants, 0, random);
        }
    }

    private static ActionCandidate Candidate(ActionEffect effect, double value) =>
        new(EffectName(effect), value, 0, [effect]);

    public ActionCandidate? BuildAttack(MonsterProfile profile, ActionContext context, int hits, double target)
    {
        int min = Math.Max(1, settings.MinimumBaseDamagePerHit);
        int amount = (int)Math.Clamp(Math.Round(target / hits, MidpointRounding.AwayFromZero), min, int.MaxValue);
        var attack = ActionCandidate.Attack($"{hits}x{amount}", hits, amount, context.Strength, context.Weakened,
            settings.WeakDamageReduction);
        double cost = settings.Valuation.Evaluate(EffectKind.Attack, profile, amount, hits: hits);
        return cost > 0 ? attack with { Value = cost } : null;
    }

    private static string EffectName(ActionEffect effect) =>
        $"{effect.Kind} {effect.Amount}" + (effect.GeneratedCard is { } card ? $" [{card}]" : "") + (effect.SplitDestinations is { } split
            ? " @" + string.Join("+", split.Select(destination => $"{destination.Pile}:{destination.Amount}"))
            : effect.Pile is { } pile ? $" @{pile}" : "");
}
