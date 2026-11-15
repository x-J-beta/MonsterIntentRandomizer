using System.Text.RegularExpressions;

namespace MonsterIntentRandomizer;

public sealed record EffectPolicy(bool Stackable = true, bool Unique = false, EffectTarget? Target = null)
{
    private static readonly IReadOnlyDictionary<EffectKind, string> Ids = Enum.GetValues<EffectKind>()
        .ToDictionary(kind => kind, kind => Regex.Replace(kind.ToString(), "(?<!^)[A-Z]", "_$0").ToUpperInvariant());

    public static string Id(EffectKind kind) => Ids[kind];

    public static EffectPolicy Default(EffectKind kind) => new(
        Stackable: !(ActionEffect.IsRareBuff(kind) || ActionEffect.IsNonStackingDefense(kind) ||
            kind is EffectKind.Regen or EffectKind.Plating or EffectKind.Intangible or EffectKind.Tender or
                EffectKind.Shrink or EffectKind.Soulbind or EffectKind.DrawDown or EffectKind.EnergyDown or
                EffectKind.Barricade),
        Unique: kind is EffectKind.VitalSpark or EffectKind.WitheringPresence,
        Target: ActionEffect.TargetFor(kind));
}
