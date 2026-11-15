using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace MonsterIntentRandomizer;

[HarmonyPatch]
internal static class IntentDetails
{
    private static readonly ConditionalWeakTable<AbstractIntent, ActionEffect> Effects = new();
    private static readonly Func<AbstractIntent, LocString> IntentTitle =
        AccessTools.MethodDelegate<Func<AbstractIntent, LocString>>(AccessTools.PropertyGetter(typeof(AbstractIntent), "IntentTitle"));

    internal static AbstractIntent Register(ActionEffect effect, AbstractIntent intent)
    {
        if (effect.Kind != EffectKind.Attack)
            Effects.Add(intent, effect);
        return intent;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NIntent), "UpdateVisuals")]
    private static void AddLabel(AbstractIntent? ____intent, MegaRichTextLabel ____valueLabel)
    {
        if (ModSettings.Current.ShowIntentDetails && ____intent != null && Effects.TryGetValue(____intent, out var effect))
        {
            var (name, _) = Describe(effect);
            ____valueLabel.Text = effect.Kind is EffectKind.Shrink or EffectKind.Smoggy or EffectKind.Tender or EffectKind.Ringing
                ? name : $"{name} {effect.Amount}";
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AbstractIntent), nameof(AbstractIntent.GetHoverTip))]
    private static void AddTooltip(AbstractIntent __instance, ref HoverTip __result)
    {
        if (ModSettings.Current.ShowIntentDetails && Effects.TryGetValue(__instance, out var effect))
        {
            var (_, details) = Describe(effect);
            __result = new HoverTip(IntentTitle(__instance), details, __result.Icon);
        }
    }

    internal static void Refresh()
    {
        if (CombatManager.Instance.DebugOnlyGetState() is not { CurrentSide: CombatSide.Player } combat)
            return;
        foreach (var enemy in combat.Enemies.Where(enemy => enemy.IsAlive))
        {
            if (enemy.GetCreatureNode() is { } node && GodotObject.IsInstanceValid(node))
                _ = TaskHelper.RunSafely(node.RefreshIntents());
        }
    }

    private static (string Name, string Details) Describe(ActionEffect effect)
    {
        bool chinese = LocManager.Instance.Language == "zhs";
        int n = effect.Amount;
        (string zh, string en) = effect.Kind switch
        {
            EffectKind.Block => ("格挡", "Block"), EffectKind.Heal => ("回复", "Heal"),
            EffectKind.Strength => ("力量", "Strength"), EffectKind.Ritual => ("仪式", "Ritual"),
            EffectKind.Intangible => ("无实体", "Intangible"), EffectKind.Thorns => ("荆棘", "Thorns"),
            EffectKind.TemporaryThorns => ("临时荆棘", "Temporary Thorns"),
            EffectKind.Weak => ("虚弱", "Weak"), EffectKind.Vulnerable => ("易伤", "Vulnerable"),
            EffectKind.Frail => ("脆弱", "Frail"), EffectKind.Poison => ("中毒", "Poison"),
            EffectKind.Tangled => ("缠绕", "Tangled"), EffectKind.Ringing => ("响铃", "Ringing"),
            EffectKind.StrengthDown => ("降力量", "Strength Down"), EffectKind.DexterityDown => ("降敏捷", "Dexterity Down"),
            EffectKind.Smoggy => ("烟雾弥漫", "Smoggy"), EffectKind.Tender => ("柔嫩", "Tender"),
            EffectKind.Shrink => ("缩小", "Shrink"),
            EffectKind.PersonalHive => ("人体蜂房", "Personal Hive"), EffectKind.VitalSpark => ("活力火花", "Vital Spark"),
            EffectKind.WitheringPresence => ("凋萎存在", "Withering Presence"), EffectKind.Envenom => ("涂毒", "Envenom"),
            EffectKind.PaperCuts => ("纸伤难愈", "Paper Cuts"), EffectKind.Dazed => ("晕眩", "Dazed"),
            EffectKind.Wound => ("伤口", "Wound"), EffectKind.Burn => ("灼伤", "Burn"),
            EffectKind.Beckon => ("呼唤", "Beckon"), _ => (effect.Kind.ToString(), effect.Kind.ToString())
        };
        if (effect.IsStatusCard)
        {
            (string pileZh, string pileEn) = effect.Pile switch
            {
                StatusPile.Hand => ("手牌", "the hand"), StatusPile.Top => ("抽牌堆顶", "the top of the draw pile"),
                StatusPile.Draw => ("抽牌堆", "the draw pile"), StatusPile.Bottom => ("抽牌堆底", "the bottom of the draw pile"),
                _ => ("弃牌堆", "the discard pile")
            };
            return chinese ? (zh, $"向玩家的{pileZh}加入 {n} 张{zh}。") : (en, $"Adds {n} {en} to {pileEn}.");
        }

        return effect.Kind switch
        {
            EffectKind.TemporaryThorns => chinese ? (zh, $"获得 {n} 层荆棘，持续到下个玩家回合结束。") :
                (en, $"Gains {n} Thorns through the next player turn."),
            EffectKind.Smoggy or EffectKind.Tender => chinese ? (zh, $"对玩家施加{zh}，持续到战斗结束。") :
                (en, $"Applies {en} to the player for the rest of combat."),
            EffectKind.Shrink => chinese ? (zh, "对玩家施加缩小，持续到施加者死亡。") :
                (en, "Applies Shrink to the player until the caster dies."),
            EffectKind.Block or EffectKind.Heal => chinese ? (zh, $"自身{zh} {n} 点。") : (en, $"Self: {n} {en}."),
            EffectKind.StrengthDown or EffectKind.DexterityDown => chinese ? (zh, $"使玩家{zh} {n} 点。") :
                (en, $"Applies {n} {en} to the player."),
            _ when effect.Target == EffectTarget.Player => chinese ? (zh, $"对玩家施加 {n} 层{zh}。") :
                (en, $"Applies {n} {en} to the player."),
            _ => chinese ? (zh, $"获得 {n} 层{zh}。") : (en, $"Gains {n} {en}.")
        };
    }
}
