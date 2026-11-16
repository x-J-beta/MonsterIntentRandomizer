namespace MonsterIntentRandomizer;

public enum EffectKind
{
    Attack, Block, Heal, Strength, Ritual, Intangible, Weak, Vulnerable, Tangled,
    Ringing, StrengthDown, DexterityDown, Dazed, Wound, Burn, Beckon,
    PersonalHive, VitalSpark, WitheringPresence, Envenom, PaperCuts, Smoggy, Tender, Frail, Poison, Shrink,
    Thorns, TemporaryThorns, Soulbind, DrawDown, EnergyDown, Regen, Wait,
    HardenedShell, HardToKill, Plating, Wither, Amnesia, MindRot, Sadness, Infection, Sloth,
    Toxic, Soot, Disintegration, Debris, Void, WasteAway, Slimed, Artifact, Barricade, Slippery, Vigor, Enrage, PainfulStabs,
    SteamEruption
}

public enum EffectTarget { Self, Player }
public enum StatusPile { Draw, Discard, Hand, Top, Bottom }

public sealed record StatusCardDestination(StatusPile Pile, int Amount);

public sealed record ActionEffect(EffectKind Kind, int Amount, int Hits = 1, StatusPile? Pile = null,
    IReadOnlyList<StatusCardDestination>? SplitDestinations = null, ThresholdPrice? Threshold = null,
    EffectKind? GeneratedCard = null)
{
    public IReadOnlyList<StatusCardDestination> CardDestinations => SplitDestinations ?? [new(Pile ?? StatusPile.Discard, Amount)];
    public EffectTarget Target => TargetFor(Kind);
    public static EffectTarget TargetFor(EffectKind kind) => kind is EffectKind.Block or EffectKind.Heal or
        EffectKind.Regen or EffectKind.Strength or EffectKind.Ritual or EffectKind.Thorns or EffectKind.TemporaryThorns
        or EffectKind.Artifact or EffectKind.Vigor or EffectKind.Enrage or EffectKind.SteamEruption || IsDefensiveBuff(kind) || IsRareBuff(kind)
        ? EffectTarget.Self : EffectTarget.Player;
    public bool IsStatusCard => IsCard(Kind);
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
    public static bool UsesThreshold(EffectKind kind) =>
        kind is EffectKind.HardenedShell or EffectKind.HardToKill or EffectKind.WitheringPresence;
    public bool IsPlayerDebuff => Kind is EffectKind.Weak or EffectKind.Vulnerable or EffectKind.Tangled or
        EffectKind.Ringing or EffectKind.StrengthDown or EffectKind.DexterityDown or EffectKind.Smoggy or EffectKind.Tender or
        EffectKind.Frail or EffectKind.Poison or EffectKind.Shrink or EffectKind.Soulbind or EffectKind.DrawDown or EffectKind.EnergyDown;
    public bool IsSpecialDebuff => IsRareDebuff(Kind);
    public static bool IsRareDebuff(EffectKind kind) => kind is EffectKind.Ringing or EffectKind.Smoggy or
        EffectKind.Tender or EffectKind.Shrink or EffectKind.StrengthDown or EffectKind.DexterityDown or
        EffectKind.Soulbind or EffectKind.DrawDown or EffectKind.EnergyDown or EffectKind.MindRot or EffectKind.WasteAway or
        EffectKind.Sloth or EffectKind.Disintegration;

    public static bool IsRareBuff(EffectKind kind) => kind is EffectKind.PersonalHive or EffectKind.VitalSpark or
        EffectKind.WitheringPresence or EffectKind.Envenom or EffectKind.PaperCuts or EffectKind.PainfulStabs;
}

public sealed record StatusPlacement(EffectKind Card, StatusPile Pile, double Weight = 1)
{
    public override string ToString() => $"{Card}:{Pile}";
}

public sealed record StatusPlacementValues(double Draw = 1, double Discard = 0.75,
    double Hand = 0.35, double Top = 1.33, double Bottom = 0.45)
{
    public double For(StatusPile pile) => pile switch
    {
        StatusPile.Draw => Draw, StatusPile.Discard => Discard, StatusPile.Hand => Hand,
        StatusPile.Top => Top, StatusPile.Bottom => Bottom, _ => 0
    };
}

public sealed record ActionContext(
    double ExpectedDamageAgainstMonster,
    int Strength = 0,
    bool Weakened = false,
    bool PlayerHasArtifact = false,
    double ExistingBlock = 0,
    bool PlayerAlreadyRestricted = false,
    bool AllowHardCardLimit = true, double MissingHp = 0,
    IReadOnlySet<EffectKind>? ActiveEffects = null,
    bool SpecialDebuffAvailable = true, int Rooms = 0, IReadOnlyDictionary<EffectKind, int>? Thresholds = null,
    IReadOnlyDictionary<EffectKind, EffectKind>? GeneratedCards = null);

public sealed record ActionValuationSettings(
    ActionValuation Valuation, double WeakDamageReduction, int MinimumBaseDamagePerHit,
    IReadOnlyDictionary<EffectKind, int[]> Amounts,
    StatusPlacementValues? PlacementValues = null,
    int MaxHardCardLimits = 1,
    IReadOnlyDictionary<EffectKind, RoomWeight>? Weights = null,
    IReadOnlyDictionary<EffectKind, int>? MinimumAmounts = null,
    IReadOnlyDictionary<EffectKind, EffectPolicy>? Policies = null,
    IReadOnlyDictionary<EffectKind, StatusValue>? Buffs = null, double ChaosProgress = 0,
    IReadOnlyDictionary<EffectKind, double>? CardPlayerActionValueRatios = null, double CardStartingPlayerActionValue = 16)
{
    internal double CardUnitValue(EffectKind card, MonsterProfile profile, int amount = 1)
    {
        double baseline = CardPlayerActionValueRatios?.GetValueOrDefault(EffectKind.Dazed, 0.05) ?? 0.05;
        double ratio = Math.Max(baseline, CardPlayerActionValueRatios?.GetValueOrDefault(card) ?? 0);
        double baseValue = Valuation.Evaluate(card, profile, amount) / amount;
        return Math.Max(0, baseValue - ratio * CardStartingPlayerActionValue) + ratio * profile.PlayerActionValue;
    }

    public EffectPolicy PolicyFor(EffectKind kind) => Buffs?.GetValueOrDefault(kind) is { } buff
        ? new EffectPolicy(buff.Stackable, buff.Unique, EffectTarget.Self)
        : Policies?.GetValueOrDefault(kind) ?? EffectPolicy.Default(kind);
}

public sealed record ActionHistory(int AddedStrength = 0, int AddedRitual = 0, int RitualStrength = 0)
{
    // Record only actions which the caller actually executed.
    public ActionHistory Record(ActionCandidate action)
    {
        var effects = action.Effects ?? [];
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

    public IEnumerable<ActionCandidate> Generate(MonsterProfile profile, ActionContext context, string? monsterId = null,
        double extraBuffHits = 0)
    {
        bool canDebuff = !context.PlayerHasArtifact;
        var buffProfile = extraBuffHits == 0 ? profile : profile with { AttackFrequency = profile.AttackFrequency + extraBuffHits };
        var extras = new List<(ActionEffect Effect, IntentStyle Style, double Value, double Weight)>();
        foreach (var (kind, buff) in settings.Buffs?.AsEnumerable() ?? [])
        {
            if (!EffectPolicy.CanTargetMonster(kind, monsterId) ||
                profile.AllowedBuffs is { } allowed && !allowed.Contains(kind))
                continue;
            var (weight, bonus) = Weights(kind, buff.RoomWeight);
            if (weight + bonus <= 0 || context.ActiveEffects?.Contains(kind) == true)
                continue;
            var style = ActionEffect.StyleFor(kind);
            bool threshold = ActionEffect.UsesThreshold(kind);
            int current = threshold ? context.Thresholds?.GetValueOrDefault(kind) ?? 0 : 0;
            if (current > 0 && !buff.Stackable)
                continue;
            EffectKind? existingCard = context.GeneratedCards != null && context.GeneratedCards.TryGetValue(kind, out var card)
                ? card : null;
            var doses = (from variant in TriggeredCardBuffs.Variants(kind, buff, settings, context.Rooms, existingCard)
                         let scale = TriggeredCardBuffs.Scale(kind, variant.Card, settings, profile)
                         from amount in buff.Doses(profile)
                         where current == 0 || amount < current
                         let price = threshold ? OpeningValuation.ThresholdCost(buff, buffProfile, current, amount)
                             : OpeningValuation.Evaluate(buff, buffProfile, amount)
                         where price * scale > 0
                         select (Amount: amount, Value: price * scale, variant.Card, variant.Weight, Scale: scale)).ToArray();
            double doseWeight = doses.Sum(dose => dose.Weight);
            foreach (var dose in doses)
            {
                var effect = new ActionEffect(kind, dose.Amount,
                    Threshold: threshold ? new ThresholdPrice(buff, buffProfile, dose.Value, dose.Scale) : null,
                    GeneratedCard: dose.Card);
                yield return new ActionCandidate(EffectName(effect), style, false, dose.Value, 0, weight * dose.Weight / doseWeight,
                    Effects: [effect], SharedWeightBonus: bonus * dose.Weight / doseWeight);
            }
        }
        foreach (var (kind, levels) in settings.Amounts)
        {
            if (ActionEffect.IsMonsterBuff(kind))
                continue;
            var (weight, bonus) = Weights(kind, settings.Weights?.GetValueOrDefault(kind));
            if (weight + bonus <= 0 || context.ActiveEffects?.Contains(kind) == true)
                continue;
            if (ActionEffect.IsRareDebuff(kind) && !context.SpecialDebuffAvailable)
                continue;

            int minimum = settings.MinimumAmounts?.GetValueOrDefault(kind, 1) ?? 1;
            extras.Clear();
            foreach (int amount in levels.Where(amount => amount >= minimum).Distinct())
            {
                switch (kind)
                {
                    case EffectKind.Block:
                        Add(kind, amount, IntentStyle.Defend,
                            effectiveAmount: Math.Min(amount, Math.Max(0, context.ExpectedDamageAgainstMonster - context.ExistingBlock)));
                        break;
                    case EffectKind.Heal:
                        Add(kind, amount, IntentStyle.Defend, effectiveAmount: Math.Min(amount, Math.Max(0, context.MissingHp)));
                        break;
                    case EffectKind.Weak when canDebuff:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Vulnerable when canDebuff:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Tangled when canDebuff && !context.PlayerAlreadyRestricted:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Ringing or EffectKind.Smoggy when amount == 1 && canDebuff && !context.PlayerAlreadyRestricted &&
                        context.AllowHardCardLimit && settings.MaxHardCardLimits > 0:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Tender when amount == 1 && canDebuff:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.StrengthDown or EffectKind.DexterityDown when canDebuff:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Frail or EffectKind.Poison when canDebuff:
                    case EffectKind.Shrink when canDebuff && amount == 1:
                    case EffectKind.Soulbind or EffectKind.DrawDown or EffectKind.EnergyDown when canDebuff:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case var card when ActionEffect.IsCard(card):
                        var placements = profile.CardPlacements?.Where(place => place.Card == kind).ToArray();
                        if (placements is not { Length: > 0 })
                            placements = [new(kind, StatusPile.Top), new(kind, StatusPile.Draw), new(kind, StatusPile.Discard)];
                        AddCards(kind, amount, placements);
                        break;
                }
            }
            // Doses and card placements share their effect's weight.
            double total = extras.Sum(extra => extra.Weight);
            foreach (var extra in extras)
                yield return new ActionCandidate(EffectName(extra.Effect), extra.Style, false, extra.Value, 0,
                    extra.Weight / total * weight, Effects: [extra.Effect], SharedWeightBonus: extra.Weight / total * bonus);
        }

        (double Base, double Bonus) Weights(EffectKind kind, RoomWeight? roomWeight)
        {
            double roomFactor = roomWeight?.At(context.Rooms, settings.ChaosProgress) ?? 1;
            return (roomFactor, (profile.Favorites?.GetValueOrDefault(kind) ?? 0) * roomFactor);
        }

        void Add(EffectKind kind, int amount, IntentStyle style, double effectiveAmount = 0)
        {
            double value = settings.Valuation.Evaluate(kind, profile, amount, effectiveAmount: effectiveAmount);
            if (value > 0)
                extras.Add((new ActionEffect(kind, amount), style, value, 1));
        }

        void AddCards(EffectKind kind, int amount, StatusPlacement[] placements)
        {
            var available = placements.Where(place => place.Weight > 0 && CanPlace(kind, place.Pile))
                .GroupBy(place => place.Pile)
                .Select(group => new StatusPlacement(kind, group.Key, group.Sum(place => place.Weight)))
                .OrderByDescending(place => place.Weight).ToArray();
            double perCard = settings.CardUnitValue(kind, profile, amount);
            if (perCard <= 0)
                return;

            // Every dose has one set of placement weights, including the split variants.
            var variants = new List<(ActionEffect Effect, double Value, double Weight)>();
            var values = settings.PlacementValues ?? new StatusPlacementValues();
            for (int mask = 1; mask < (1 << available.Length); mask++)
            {
                var selected = available.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                if (selected.Length > amount)
                    continue;
                var destinations = selected.Select((placement, index) => new StatusCardDestination(placement.Pile,
                    amount / selected.Length + (index < amount % selected.Length ? 1 : 0))).ToArray();
                var effect = destinations.Length == 1 ? new ActionEffect(kind, amount, Pile: destinations[0].Pile)
                    : new ActionEffect(kind, amount, SplitDestinations: destinations);
                double cost = perCard * destinations.Sum(destination => destination.Amount * values.For(destination.Pile));
                if (cost > 0)
                    variants.Add((effect, cost, selected.Average(place => place.Weight)));
            }
            double total = variants.Sum(variant => variant.Weight);
            foreach (var variant in variants)
                extras.Add((variant.Effect, IntentStyle.PlayerDebuff, variant.Value, variant.Weight / total));
        }
    }

    internal static bool CanPlace(EffectKind kind, StatusPile pile) =>
        !(kind == EffectKind.Dazed && pile is StatusPile.Hand or StatusPile.Bottom) &&
        !(pile == StatusPile.Hand && kind is EffectKind.Void or EffectKind.Amnesia or EffectKind.MindRot or
            EffectKind.WasteAway or EffectKind.Sloth or EffectKind.Disintegration);

    public ActionCandidate? BuildAttack(MonsterProfile profile, ActionContext context, int hits, double target)
    {
        int min = Math.Max(1, settings.MinimumBaseDamagePerHit);
        int amount = (int)Math.Clamp(Math.Round(target / hits, MidpointRounding.AwayFromZero), min, int.MaxValue);
        var attack = ActionCandidate.Attack($"{hits}x{amount}", hits, amount, context.Strength, context.Weakened, 1,
            settings.WeakDamageReduction);
        double cost = settings.Valuation.Evaluate(EffectKind.Attack, profile, amount, hits: hits);
        return cost > 0 ? attack with { Value = cost } : null;
    }

    private static string EffectName(ActionEffect effect) =>
        $"{effect.Kind} {effect.Amount}" + (effect.GeneratedCard is { } card ? $" [{card}]" : "") + (effect.SplitDestinations is { } split
            ? " @" + string.Join("+", split.Select(destination => $"{destination.Pile}:{destination.Amount}"))
            : effect.Pile is { } pile ? $" @{pile}" : "");
}
