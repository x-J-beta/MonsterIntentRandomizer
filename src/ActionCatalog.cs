namespace MonsterIntentRandomizer;

public enum EffectKind
{
    Attack, Block, Heal, Strength, Ritual, Intangible, Weak, Vulnerable, Tangled,
    Ringing, StrengthDown, DexterityDown, Dazed, Wound, Burn, Beckon,
    PersonalHive, VitalSpark, WitheringPresence, Envenom, PaperCuts, Smoggy, Tender, Frail, Poison, Shrink,
    Thorns, TemporaryThorns
}

public enum EffectTarget { Self, Player }
public enum StatusPile { Draw, Discard, Hand, Top, Bottom }

public sealed record ActionEffect(EffectKind Kind, int Amount, int Hits = 1, StatusPile? Pile = null)
{
    public EffectTarget Target => Kind is EffectKind.Block or EffectKind.Heal or EffectKind.Strength or EffectKind.Ritual or
        EffectKind.Intangible or EffectKind.Thorns or EffectKind.TemporaryThorns
        || IsRareBuff(Kind) ? EffectTarget.Self : EffectTarget.Player;
    public bool IsStatusCard => Kind is EffectKind.Dazed or EffectKind.Wound or EffectKind.Burn or EffectKind.Beckon;
    public bool IsPlayerDebuff => Kind is EffectKind.Weak or EffectKind.Vulnerable or EffectKind.Tangled or
        EffectKind.Ringing or EffectKind.StrengthDown or EffectKind.DexterityDown or EffectKind.Smoggy or EffectKind.Tender or
        EffectKind.Frail or EffectKind.Poison or EffectKind.Shrink;
    public bool IsHardCardLimit => Kind is EffectKind.Ringing or EffectKind.Smoggy;
    public bool IsSpecialDebuff => IsRareDebuff(Kind);
    public static bool IsRareDebuff(EffectKind kind) => kind is EffectKind.Ringing or EffectKind.Smoggy or
        EffectKind.Tender or EffectKind.Shrink or EffectKind.StrengthDown or EffectKind.DexterityDown;

    public static bool IsRareBuff(EffectKind kind) => kind is EffectKind.PersonalHive or EffectKind.VitalSpark or
        EffectKind.WitheringPresence or EffectKind.Envenom or EffectKind.PaperCuts;
}

public sealed record StatusPlacement(EffectKind Card, StatusPile Pile, double Weight = 1)
{
    public override string ToString() => $"{Card}:{Pile}";
}

public sealed record StatusPlacementValues(double Draw = 1, double Discard = 0.6,
    double Hand = 0.35, double Top = 1.4, double Bottom = 0.45)
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
    bool PlayerAlreadyWeak = false,
    bool PlayerAlreadyVulnerable = false,
    bool PlayerHasArtifact = false,
    double DazedDraws = 0,
    double WoundDraws = 0,
    double ExistingBlock = 0,
    double BurnDraws = 0.6, bool PlayerAlreadyRestricted = false,
    bool AllowHardCardLimit = true, double MissingHp = 0, bool AlreadyIntangible = false, double BeckonDraws = 0.6,
    int Act = 1, IReadOnlySet<EffectKind>? ActiveEffects = null, double RareBuffRoll = 0,
    bool SpecialDebuffAvailable = true, double SpecialDebuffRoll = 0);

public sealed record ActionValuationSettings(
    ActionValuation Valuation, double WeakDamageReduction,
    int MaxAddedStrength, int MaxInsertedCards, int PlayerDebuffCooldownTurns,
    int PureBlockCooldownTurns, int MinimumBaseDamagePerHit,
    IReadOnlyDictionary<EffectKind, int[]> Amounts, int MaxAddedRitual = 1,
    StatusPlacementValues? PlacementValues = null,
    int MaxHardCardLimits = 1, double HandBurnDamageChance = 1, IReadOnlyDictionary<EffectKind, int>? MinActs = null,
    double SpecialDebuffChance = 0.20);

public sealed record ActionHistory(
    int AddedStrength = 0, int InsertedCards = 0, int LastPlayerDebuffTurn = int.MinValue,
    int LastPureBlockTurn = int.MinValue, int AddedRitual = 0, int RitualStrength = 0,
    int HardCardLimits = 0)
{
    // Record only actions which the caller actually executed.
    public ActionHistory Record(ActionCandidate action, int turn)
    {
        var effects = action.Effects ?? [];
        int addedRitual = effects.Where(effect => effect.Kind == EffectKind.Ritual).Sum(effect => effect.Amount);
        return new ActionHistory(
            AddedStrength + effects.Where(effect => effect.Kind == EffectKind.Strength).Sum(effect => effect.Amount),
            InsertedCards + effects.Where(effect => effect.IsStatusCard)
                .Sum(effect => effect.Amount),
            effects.Any(effect => effect.IsPlayerDebuff)
                ? turn : LastPlayerDebuffTurn,
            !action.IsAttack && effects.Any(effect => effect.Kind == EffectKind.Block)
                ? turn : LastPureBlockTurn,
            AddedRitual + addedRitual, RitualStrength + AddedRitual,
            HardCardLimits + effects.Count(effect => effect.IsHardCardLimit));
    }
}

public sealed class ActionCatalog(ActionValuationSettings settings)
{
    public IEnumerable<ActionCandidate> Generate(
        MonsterProfile profile, ActionContext context, ActionHistory history, int turn)
    {
        var hits = (profile.HitWeights ?? new Dictionary<int, double> { [1] = 1 })
            .Where(pair => pair.Key > 0 && pair.Value > 0).OrderBy(pair => pair.Key).ToArray();
        var actions = profile.EffectWeights ?? new Dictionary<EffectKind, double>();
        bool canDebuff = !context.PlayerHasArtifact &&
            (history.LastPlayerDebuffTurn == int.MinValue ||
                turn - history.LastPlayerDebuffTurn > settings.PlayerDebuffCooldownTurns);

        var extras = new List<(ActionEffect Effect, IntentStyle Style, double Value, double Weight)>();
        foreach (var (kind, weight) in actions.Where(entry => entry.Value > 0))
        {
            if (context.Act < (settings.MinActs?.GetValueOrDefault(kind) ?? 1) || context.ActiveEffects?.Contains(kind) == true)
                continue;
            if (ActionEffect.IsRareDebuff(kind) && (!context.SpecialDebuffAvailable || turn <= 1 ||
                context.SpecialDebuffRoll >= settings.SpecialDebuffChance))
                continue;

            foreach (int amount in (settings.Amounts.GetValueOrDefault(kind) ?? []).Where(value => value > 0).Distinct())
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
                    case EffectKind.Intangible when !context.AlreadyIntangible:
                        Add(kind, amount, IntentStyle.Defend);
                        break;
                    case EffectKind.Strength when amount <= settings.MaxAddedStrength - history.AddedStrength:
                        Add(kind, amount, IntentStyle.Buff);
                        break;
                    case EffectKind.Ritual when amount <= settings.MaxAddedRitual - history.AddedRitual:
                        Add(kind, amount, IntentStyle.Buff);
                        break;
                    case EffectKind.Thorns or EffectKind.TemporaryThorns:
                        Add(kind, amount, IntentStyle.Buff);
                        break;
                    case EffectKind.PersonalHive or EffectKind.VitalSpark or EffectKind.WitheringPresence
                        or EffectKind.Envenom or EffectKind.PaperCuts when turn > 1 && context.RareBuffRoll < weight:
                        Add(kind, amount, IntentStyle.Buff);
                        break;
                    case EffectKind.Weak when canDebuff && !context.PlayerAlreadyWeak:
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Vulnerable when canDebuff && !context.PlayerAlreadyVulnerable:
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
                        Add(kind, amount, IntentStyle.PlayerDebuff);
                        break;
                    case EffectKind.Dazed or EffectKind.Wound or EffectKind.Burn or EffectKind.Beckon
                        when amount <= settings.MaxInsertedCards - history.InsertedCards:
                        var placements = profile.CardPlacements?.Where(place => place.Card == kind).ToArray();
                        if (placements is not { Length: > 0 })
                            placements = [new StatusPlacement(kind, kind == EffectKind.Wound ? StatusPile.Discard : StatusPile.Draw)];
                        foreach (var placement in placements)
                        {
                            if (kind == EffectKind.Dazed && placement.Pile is StatusPile.Hand or StatusPile.Bottom)
                                continue;

                            double draws = kind switch
                            {
                                EffectKind.Dazed => context.DazedDraws, EffectKind.Wound => context.WoundDraws,
                                EffectKind.Beckon => context.BeckonDraws, _ => context.BurnDraws
                            };
                            draws *= (settings.PlacementValues ?? new StatusPlacementValues()).For(placement.Pile);
                            if (kind == EffectKind.Dazed)
                                draws = Math.Clamp(draws, 0, 1);
                            double triggers = kind == EffectKind.Burn && placement.Pile == StatusPile.Hand
                                ? settings.HandBurnDamageChance : draws;
                            Add(kind, amount, IntentStyle.PlayerDebuff, placement.Pile, placement.Weight,
                                draws: draws, triggers: triggers);
                        }
                        break;
                }
            }
        }

        // Normalize alternatives within a style so adding another dose does not inflate its weight.
        var totalsByStyle = extras.GroupBy(extra => extra.Style)
            .ToDictionary(group => group.Key, group => group.Sum(extra => extra.Weight));
        foreach (var extra in extras)
        {
            if (extra.Effect.Kind == EffectKind.Block && history.LastPureBlockTurn != int.MinValue &&
                turn - history.LastPureBlockTurn <= settings.PureBlockCooldownTurns)
                continue;
            yield return new ActionCandidate(
                EffectName(extra.Effect), extra.Style, false, extra.Value, 0,
                extra.Weight / totalsByStyle[extra.Style], Effects: [extra.Effect]);
        }

        foreach (var hit in hits)
        {
            int maxPerHit = (int)Math.Ceiling(profile.ActionValue * (1 + profile.Volatility) / hit.Key);
            int minPerHit = Math.Max(1, settings.MinimumBaseDamagePerHit);
            double weight = hit.Value / Math.Max(1, maxPerHit - minPerHit + 1);
            for (int amount = minPerHit; amount <= maxPerHit; amount++)
            {
                var attack = ActionCandidate.Attack($"{hit.Key}x{amount}", hit.Key, amount,
                    context.Strength, context.Weakened, weight, settings.WeakDamageReduction);
                if (attack.Damage <= 0)
                    continue;

                attack = attack with { Value = settings.Valuation.Evaluate(EffectKind.Attack, profile, amount, hits: hit.Key) };
                yield return attack;
                foreach (var extra in extras)
                {
                    if (extra.Effect.IsHardCardLimit || extra.Effect.Kind == EffectKind.Intangible ||
                        ActionEffect.IsRareBuff(extra.Effect.Kind))
                        continue;
                    yield return new ActionCandidate(
                        $"{attack.Id} + {EffectName(extra.Effect)}", extra.Style, true,
                        attack.Value + extra.Value, attack.Damage,
                        attack.Weight * extra.Weight / totalsByStyle[extra.Style],
                        [attack.Effects![0], extra.Effect], attack.Damage + extra.Value);
                }
            }
        }

        void Add(EffectKind kind, int amount, IntentStyle style, StatusPile? pile = null, double weight = 1,
            double effectiveAmount = 0, double draws = 0, double triggers = 0)
        {
            double value = settings.Valuation.Evaluate(kind, profile, amount,
                effectiveAmount: effectiveAmount, draws: draws, triggers: triggers);
            if (value > 0 && weight > 0 && profile.IntentWeights.GetValueOrDefault(style) > 0)
            {
                int doses = settings.Amounts[kind].Where(dose => dose > 0).Distinct().Count();
                double preference = ActionEffect.IsRareBuff(kind) ? 1 : actions[kind];
                extras.Add((new ActionEffect(kind, amount, Pile: pile), style, value, preference * weight / doses));
            }
        }
    }

    private static string EffectName(ActionEffect effect) =>
        $"{effect.Kind} {effect.Amount}" + (effect.Pile is { } pile ? $" @{pile}" : "");
}
