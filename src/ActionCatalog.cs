namespace MonsterIntentRandomizer;

public enum EffectKind
{
    Attack, Block, Heal, Strength, Ritual, Intangible, Weak, Vulnerable, Tangled,
    Ringing, StrengthDown, DexterityDown, Dazed, Wound, Burn, Beckon,
    PersonalHive, VitalSpark, WitheringPresence, Envenom, PaperCuts, Smoggy, Tender, Frail, Poison, Shrink,
    Thorns, TemporaryThorns, Soulbind, DrawDown, EnergyDown, Regen, Wait,
    HardenedShell, HardToKill, Plating, Wither, Amnesia, MindRot, Sadness, Infection, Sloth,
    Toxic, Soot, Disintegration, Debris, Void, WasteAway, Slimed, Artifact, Barricade, CurlUp, Slippery, Vigor, Enrage
}

public enum EffectTarget { Self, Player }
public enum StatusPile { Draw, Discard, Hand, Top, Bottom }

public sealed record ActionEffect(EffectKind Kind, int Amount, int Hits = 1, StatusPile? Pile = null)
{
    public EffectTarget Target => TargetFor(Kind);
    public static EffectTarget TargetFor(EffectKind kind) => kind is EffectKind.Block or EffectKind.Heal or EffectKind.Regen or EffectKind.Strength or EffectKind.Ritual or
        EffectKind.Intangible or EffectKind.Thorns or EffectKind.TemporaryThorns or EffectKind.Plating
        or EffectKind.Artifact or EffectKind.Barricade or EffectKind.CurlUp or EffectKind.Vigor or EffectKind.Enrage
        || IsNonStackingDefense(kind)
        || IsRareBuff(kind) ? EffectTarget.Self : EffectTarget.Player;
    public bool IsStatusCard => IsCard(Kind);
    public static bool IsMonsterBuff(EffectKind kind) => TargetFor(kind) == EffectTarget.Self &&
        kind is not (EffectKind.Block or EffectKind.Heal);
    public static bool IsCard(EffectKind kind) => kind is EffectKind.Dazed or EffectKind.Wound or EffectKind.Burn or EffectKind.Beckon or
        EffectKind.Wither or EffectKind.Amnesia or EffectKind.MindRot or EffectKind.Sadness or EffectKind.Infection or
        EffectKind.Sloth or EffectKind.Toxic or EffectKind.Soot or EffectKind.Disintegration or EffectKind.Debris or
        EffectKind.Void or EffectKind.WasteAway or EffectKind.Slimed;
    public static bool IsNonStackingDefense(EffectKind kind) => kind is EffectKind.HardenedShell or EffectKind.HardToKill or EffectKind.Slippery;
    public bool IsPlayerDebuff => Kind is EffectKind.Weak or EffectKind.Vulnerable or EffectKind.Tangled or
        EffectKind.Ringing or EffectKind.StrengthDown or EffectKind.DexterityDown or EffectKind.Smoggy or EffectKind.Tender or
        EffectKind.Frail or EffectKind.Poison or EffectKind.Shrink or EffectKind.Soulbind or EffectKind.DrawDown or EffectKind.EnergyDown;
    public bool IsHardCardLimit => Kind is EffectKind.Ringing or EffectKind.Smoggy;
    public bool IsSpecialDebuff => IsRareDebuff(Kind);
    public static bool IsRareDebuff(EffectKind kind) => kind is EffectKind.Ringing or EffectKind.Smoggy or
        EffectKind.Tender or EffectKind.Shrink or EffectKind.StrengthDown or EffectKind.DexterityDown or
        EffectKind.Soulbind or EffectKind.DrawDown or EffectKind.EnergyDown or EffectKind.MindRot or EffectKind.WasteAway or
        EffectKind.Sloth or EffectKind.Disintegration;

    public static bool IsRareBuff(EffectKind kind) => kind is EffectKind.PersonalHive or EffectKind.VitalSpark or
        EffectKind.WitheringPresence or EffectKind.Envenom or EffectKind.PaperCuts;
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
    int Act = 1, IReadOnlySet<EffectKind>? ActiveEffects = null, double RareBuffRoll = 0,
    bool SpecialDebuffAvailable = true, double SpecialDebuffRoll = 0, int Rooms = 0);

public sealed record ActionValuationSettings(
    ActionValuation Valuation, double WeakDamageReduction, int MinimumBaseDamagePerHit,
    IReadOnlyDictionary<EffectKind, int[]> Amounts,
    StatusPlacementValues? PlacementValues = null,
    int MaxHardCardLimits = 1, IReadOnlyDictionary<EffectKind, int>? MinActs = null,
    double SpecialDebuffChance = 0.20, IReadOnlyDictionary<EffectKind, RoomWeight>? Weights = null,
    IReadOnlyDictionary<EffectKind, int>? MinimumAmounts = null,
    IReadOnlyDictionary<EffectKind, double>? HpPerSurvivalTurnCaps = null,
    IReadOnlyDictionary<EffectKind, EffectPolicy>? Policies = null,
    IReadOnlyDictionary<EffectKind, StatusValue>? Buffs = null)
{
    public EffectPolicy PolicyFor(EffectKind kind) => Buffs?.GetValueOrDefault(kind) is { } buff
        ? new EffectPolicy(buff.Stackable, buff.Unique, EffectTarget.Self)
        : Policies?.GetValueOrDefault(kind) ?? EffectPolicy.Default(kind);
}

public sealed record ActionHistory(
    int AddedStrength = 0, int AddedRitual = 0, int RitualStrength = 0,
    int HardCardLimits = 0)
{
    // Record only actions which the caller actually executed.
    public ActionHistory Record(ActionCandidate action)
    {
        var effects = action.Effects ?? [];
        int addedRitual = effects.Where(effect => effect.Kind == EffectKind.Ritual).Sum(effect => effect.Amount);
        return new ActionHistory(
            AddedStrength + effects.Where(effect => effect.Kind == EffectKind.Strength).Sum(effect => effect.Amount),
            AddedRitual + addedRitual, RitualStrength + AddedRitual,
            HardCardLimits + effects.Count(effect => effect.IsHardCardLimit));
    }
}

public sealed class ActionCatalog(ActionValuationSettings settings)
{
    public IEnumerable<ActionCandidate> Generate(
        MonsterProfile profile, ActionContext context, ActionHistory history, int turn)
    {
        var actions = profile.EffectWeights;
        bool canDebuff = !context.PlayerHasArtifact;
        var extras = new List<(ActionEffect Effect, IntentStyle Style, double Value, double Weight)>();
        foreach (var (kind, buff) in settings.Buffs?.AsEnumerable() ?? [])
        {
            double preference = actions?.GetValueOrDefault(kind, buff.Common ? 1 : 0) ?? (buff.Common ? 1 : 0);
            double weight = preference * buff.RoomWeight.At(context.Rooms);
            if (weight <= 0 || context.ActiveEffects?.Contains(kind) == true)
                continue;
            var style = kind is EffectKind.Intangible or EffectKind.Plating or EffectKind.Barricade or EffectKind.CurlUp ||
                ActionEffect.IsNonStackingDefense(kind) ? IntentStyle.Defend : IntentStyle.Buff;
            if (profile.IntentWeights.GetValueOrDefault(style) <= 0)
                continue;
            var doses = buff.Doses(profile).Select(amount => (Amount: amount, Value: OpeningValuation.Evaluate(buff, profile, amount)))
                .Where(dose => dose.Value > 0).ToArray();
            foreach (var dose in doses)
            {
                var effect = new ActionEffect(kind, dose.Amount);
                yield return new ActionCandidate(EffectName(effect), style, false, dose.Value, 0, weight / doses.Length, Effects: [effect]);
            }
        }
        foreach (var kind in settings.Amounts.Keys.Union(settings.HpPerSurvivalTurnCaps?.Keys ?? []))
        {
            if (settings.Buffs?.ContainsKey(kind) == true)
                continue;
            double preference = actions?.GetValueOrDefault(kind, IsCommon(kind) ? 1 : 0) ?? (IsCommon(kind) ? 1 : 0);
            if (preference <= 0 || (settings.Weights?.GetValueOrDefault(kind)?.At(context.Rooms) ?? 1) <= 0)
                continue;
            if (context.Act < (settings.MinActs?.GetValueOrDefault(kind) ?? 1) || context.ActiveEffects?.Contains(kind) == true)
                continue;
            if (ActionEffect.IsRareDebuff(kind) && (!context.SpecialDebuffAvailable || turn <= 1 ||
                context.SpecialDebuffRoll >= settings.SpecialDebuffChance))
                continue;

            foreach (int amount in Amounts(kind, profile))
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
                    case EffectKind.Intangible:
                    case EffectKind.Plating or EffectKind.HardenedShell or EffectKind.HardToKill or EffectKind.Barricade or EffectKind.Slippery:
                        Add(kind, amount, IntentStyle.Defend);
                        break;
                    case EffectKind.CurlUp:
                        Add(kind, amount, IntentStyle.Defend, effectiveAmount: Math.Min(amount, context.ExpectedDamageAgainstMonster));
                        break;
                    case EffectKind.Strength or EffectKind.Ritual or EffectKind.Regen or EffectKind.Artifact:
                        Add(kind, amount, IntentStyle.Buff);
                        break;
                    case EffectKind.Thorns or EffectKind.TemporaryThorns:
                        Add(kind, amount, IntentStyle.Buff);
                        break;
                    case EffectKind.PersonalHive or EffectKind.VitalSpark or EffectKind.WitheringPresence
                        or EffectKind.Envenom or EffectKind.PaperCuts when turn > 1 && context.RareBuffRoll < preference:
                        Add(kind, amount, IntentStyle.Buff);
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
                        context.AllowHardCardLimit && history.HardCardLimits < settings.MaxHardCardLimits:
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
                        foreach (var placement in placements)
                        {
                            if (kind == EffectKind.Dazed && placement.Pile is StatusPile.Hand or StatusPile.Bottom)
                                continue;
                            if (placement.Pile == StatusPile.Hand && kind is EffectKind.Void or EffectKind.Amnesia or
                                EffectKind.MindRot or EffectKind.WasteAway or EffectKind.Sloth or EffectKind.Disintegration)
                                continue;

                            Add(kind, amount, IntentStyle.PlayerDebuff, placement.Pile, placement.Weight);
                        }
                        break;
                }
            }
        }

        // Normalize alternatives within a style so adding another dose does not inflate its weight.
        foreach (var group in extras.GroupBy(extra => extra.Effect.Kind))
        {
            double total = group.Sum(extra => extra.Weight);
            double preference = ActionEffect.IsRareBuff(group.Key) ? 1 : actions?.GetValueOrDefault(group.Key, 1) ?? 1;
            double roomWeight = settings.Weights?.GetValueOrDefault(group.Key)?.At(context.Rooms) ?? 1;
            foreach (var extra in group)
                yield return new ActionCandidate(EffectName(extra.Effect), extra.Style, false, extra.Value, 0,
                    extra.Weight / total * preference * roomWeight, Effects: [extra.Effect]);
        }

        void Add(EffectKind kind, int amount, IntentStyle style, StatusPile? pile = null, double weight = 1,
            double effectiveAmount = 0)
        {
            double value = settings.Valuation.Evaluate(kind, profile, amount, effectiveAmount: effectiveAmount);
            if (pile is StatusPile position)
                value *= (settings.PlacementValues ?? new StatusPlacementValues()).For(position);
            if (value > 0 && weight > 0 && profile.IntentWeights.GetValueOrDefault(style) > 0)
            {
                extras.Add((new ActionEffect(kind, amount, Pile: pile), style, value, weight));
            }
        }
    }

    public ActionCandidate? BuildAttack(MonsterProfile profile, ActionContext context, int hits, double target)
    {
        int min = Math.Max(1, settings.MinimumBaseDamagePerHit);
        int amount = (int)Math.Clamp(Math.Round(target / hits, MidpointRounding.AwayFromZero), min, int.MaxValue);
        var attack = ActionCandidate.Attack($"{hits}x{amount}", hits, amount, context.Strength, context.Weakened, 1,
            settings.WeakDamageReduction);
        double cost = settings.Valuation.Evaluate(EffectKind.Attack, profile, amount, hits: hits);
        return cost > 0 ? attack with { Value = cost } : null;
    }

    private IEnumerable<int> Amounts(EffectKind kind, MonsterProfile profile)
    {
        int minimum = settings.MinimumAmounts?.GetValueOrDefault(kind, 1) ?? 1;
        var declared = settings.Amounts.GetValueOrDefault(kind);
        if (settings.HpPerSurvivalTurnCaps?.TryGetValue(kind, out double hpFactor) == true)
        {
            int maximum = profile.CapAmount(declared?.DefaultIfEmpty(0).Max() ?? int.MaxValue, hpFactor);
            return Enumerable.Range(minimum, Math.Max(0, maximum - minimum + 1));
        }
        return (declared ?? []).Where(amount => amount >= minimum).Distinct();
    }

    private static bool IsCommon(EffectKind kind) => kind is EffectKind.Strength or EffectKind.Ritual or EffectKind.Regen or
        EffectKind.Thorns or EffectKind.TemporaryThorns or EffectKind.Block or EffectKind.Heal or EffectKind.Weak or
        EffectKind.Vulnerable or EffectKind.Frail or EffectKind.Plating or EffectKind.Artifact or EffectKind.Barricade or EffectKind.CurlUp ||
        ActionEffect.IsCard(kind) || ActionEffect.IsNonStackingDefense(kind);

    private static string EffectName(ActionEffect effect) =>
        $"{effect.Kind} {effect.Amount}" + (effect.Pile is { } pile ? $" @{pile}" : "");
}
