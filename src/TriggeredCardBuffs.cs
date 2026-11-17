namespace MonsterIntentRandomizer;

internal static class TriggeredCardBuffs
{
    internal static readonly IReadOnlyDictionary<EffectKind, StatusPlacement> Types = new Dictionary<EffectKind, StatusPlacement>
    {
        [EffectKind.WitheringPresence] = new(EffectKind.Wither, StatusPile.Hand),
        [EffectKind.PersonalHive] = new(EffectKind.Dazed, StatusPile.Draw),
        [EffectKind.PainfulStabs] = new(EffectKind.Wound, StatusPile.Discard)
    };

    internal static bool UsesNative(EffectKind kind, string? monsterId) =>
        kind == EffectKind.PersonalHive && monsterId == "ENTOMANCER";

    internal static bool CanGenerate(EffectKind card, StatusPile pile) =>
        ActionEffect.IsCard(card) && (pile != StatusPile.Hand || card is not
            (EffectKind.Dazed or EffectKind.Void or EffectKind.Amnesia or EffectKind.MindRot or
             EffectKind.WasteAway or EffectKind.Sloth or EffectKind.Disintegration));

    internal static IEnumerable<(EffectKind? Card, double Weight)> Variants(EffectKind kind, StatusValue state,
        ActionValuationSettings settings, int rooms, EffectKind? existing = null, string? monsterId = null)
    {
        if (!Types.TryGetValue(kind, out var original))
            return [(null, 1)];
        if (UsesNative(kind, monsterId))
            return [(original.Card, 1)];
        if (existing is { } card)
            return [(card, 1)];

        var choices = state.GeneratedCards ?? new Dictionary<EffectKind, double> { [original.Card] = 1 };
        return choices.Where(pair => pair.Value > 0).Select(pair => ((EffectKind?)pair.Key,
            pair.Value * (settings.Weights?.GetValueOrDefault(pair.Key)?.At(rooms, settings.ChaosProgress) ?? 1)))
            .Where(choice => choice.Item2 > 0);
    }

    internal static double Scale(EffectKind kind, EffectKind? card, ActionValuationSettings settings, MonsterProfile profile)
    {
        if (card == null || !Types.TryGetValue(kind, out var original) || card == original.Card)
            return 1;
        double baseline = settings.CardUnitValue(original.Card, profile);
        return baseline > 0 ? settings.CardUnitValue(card.Value, profile) / baseline : 0;
    }
}
