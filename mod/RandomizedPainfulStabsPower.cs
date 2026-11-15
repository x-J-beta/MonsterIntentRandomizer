using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace MonsterIntentRandomizer;

public sealed class RandomizedPainfulStabsPower : RandomizedCardPower
{
    protected override EffectKind Kind => EffectKind.PainfulStabs;
    internal override PowerModel NativePower => ModelDb.Power<PainfulStabsPower>();
    protected override (string Chinese, string English) DescriptionText =>
        ("每段打穿格挡的攻击向玩家弃牌堆加入 {Amount} 张[gold]{Card}[/gold]。",
         "Adds {Amount} [gold]{Card}[/gold] to the player's discard pile per unblocked attack hit.");

    public override async Task AfterAttack(PlayerChoiceContext choice, AttackCommand command)
    {
        if (command.Attacker != Owner || command.TargetSide == Owner.Side || !command.DamageProps.IsPoweredAttack())
            return;

        var hits = command.Results.SelectMany(batch => batch)
            .Where(hit => hit.Receiver.IsPlayer && hit.UnblockedDamage > 0)
            .GroupBy(hit => hit.Receiver).ToArray();
        foreach (var group in hits)
            await InsertCard(group.Key, Amount * group.Count());
        if (hits.Length > 0)
            Flash();
    }
}

[HarmonyPatch]
internal static class CustomPowerIcons
{
    private static PowerModel? NativeIcon(PowerModel power) => power switch
    {
        RandomizedCardPower cards => cards.NativePower,
        SteamBuildupPower => ModelDb.Power<SteamEruptionPower>(),
        _ => null
    };

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.PackedIconPath), MethodType.Getter)]
    private static bool UsePackedIcon(PowerModel __instance, ref string __result)
    {
        if (NativeIcon(__instance) is not { } source)
            return true;
        __result = source.PackedIconPath;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.BigIcon), MethodType.Getter)]
    private static bool UseBigIcon(PowerModel __instance, ref Texture2D __result)
    {
        if (NativeIcon(__instance) is not { } source)
            return true;
        __result = source.BigIcon;
        return false;
    }
}
