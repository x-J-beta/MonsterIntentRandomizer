using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Powers;

namespace MonsterIntentRandomizer;

[HarmonyPatch]
internal static class VitalSparkIntegration
{
    internal static Task Refresh(ICombatState combat) => Refresh(combat, TotalSpark(combat));

    private static int TotalSpark(ICombatState combat) =>
        combat.Enemies.Where(enemy => enemy.IsAlive).Sum(enemy => enemy.GetPower<VitalSparkPower>()?.Amount ?? 0);

    private static ICombatState? ManagedCombat(Creature owner) =>
        owner.Side == CombatSide.Enemy && owner.CombatState is { } combat && CombatIntegration.ManagesCombat(combat)
            ? combat : null;

    private static async Task Refresh(ICombatState combat, int amount)
    {
        if (amount <= 0)
            return;

        var cards = combat.Players.SelectMany(player => player.PlayerCombatState?.AllCards ?? []).ToArray();
        foreach (var card in cards)
            await RefreshCard(card, amount);
    }

    private static async Task RefreshCard(CardModel card, int amount)
    {
        if (card.Type != CardType.Skill || amount <= 0)
            return;

        if (card.Affliction is Tainted tainted)
            tainted.Amount = amount;
        else if (card.Affliction == null)
            await CardCmd.Afflict<Tainted>(card, amount);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(VitalSparkPower), nameof(VitalSparkPower.AfterPowerAmountChanged))]
    private static bool SyncOnAmountChanged(VitalSparkPower __instance, PowerModel __1, ref Task __result)
    {
        if (!ReferenceEquals(__instance, __1) || ManagedCombat(__instance.Owner) is not { } combat)
            return true;

        __result = Refresh(combat);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(VitalSparkPower), nameof(VitalSparkPower.AfterCardEnteredCombat))]
    private static bool SyncNewCard(VitalSparkPower __instance, CardModel __0, ref Task __result)
    {
        if (ManagedCombat(__instance.Owner) is not { } combat)
            return true;

        __result = RefreshCard(__0, TotalSpark(combat));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(VitalSparkPower), nameof(VitalSparkPower.AfterRemoved))]
    private static bool KeepOtherSources(Creature __0, ref Task __result)
    {
        if (ManagedCombat(__0) is not { } combat)
            return true;

        int remaining = TotalSpark(combat);
        if (remaining <= 0)
            return true;

        __result = Refresh(combat, remaining);
        return false;
    }
}
