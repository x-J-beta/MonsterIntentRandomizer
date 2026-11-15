using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace MonsterIntentRandomizer;

public sealed class RandomizedPainfulStabsPower : PowerModel
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override LocString Title => ModelDb.Power<PainfulStabsPower>().Title;
    public override LocString Description => ModelDb.Power<PainfulStabsPower>().Description;

    public override async Task AfterAttack(PlayerChoiceContext choice, AttackCommand command)
    {
        if (command.Attacker != Owner || command.TargetSide == Owner.Side || !command.DamageProps.IsPoweredAttack())
            return;

        var hits = command.Results.SelectMany(batch => batch)
            .Where(hit => hit.Receiver.IsPlayer && hit.UnblockedDamage > 0)
            .GroupBy(hit => hit.Receiver).ToArray();
        foreach (var group in hits)
            await CardPileCmd.AddToCombatAndPreview<Wound>(group.Key, PileType.Discard, Amount * group.Count(), null,
                CardPilePosition.Bottom);
        if (hits.Length > 0)
            Flash();
    }

    [HarmonyPatch]
    private static class Appearance
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerModel), nameof(PackedIconPath), MethodType.Getter)]
        private static bool UsePackedIcon(PowerModel __instance, ref string __result)
        {
            if (__instance is not RandomizedPainfulStabsPower)
                return true;
            __result = ModelDb.Power<PainfulStabsPower>().PackedIconPath;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerModel), nameof(BigIcon), MethodType.Getter)]
        private static bool UseBigIcon(PowerModel __instance, ref Texture2D __result)
        {
            if (__instance is not RandomizedPainfulStabsPower)
                return true;
            __result = ModelDb.Power<PainfulStabsPower>().BigIcon;
            return false;
        }
    }
}
