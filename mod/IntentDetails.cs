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
            ____valueLabel.Text = effect.Kind is EffectKind.Shrink or EffectKind.Smoggy or EffectKind.Tender or EffectKind.Ringing or
                EffectKind.Dampen or EffectKind.Hex or EffectKind.Wait
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
        bool multiplayer = CombatManager.Instance.DebugOnlyGetState() is { Players.Count: > 1 };
        string player = multiplayer ? "全体玩家" : "玩家";
        string playerEach = multiplayer ? "每名玩家" : "玩家";
        string playerEn = multiplayer ? "each player" : "the player";
        int n = effect.Amount;
        var (zh, en) = Names(effect.Kind);
        if (effect.IsInsertedCard)
        {
            string destinations = DescribeDestinations(effect, chinese);
            return chinese ? (zh, $"向{playerEach}加入 {n} 张{zh}（{destinations}）。") :
                (en, $"Adds {n} {en} to {playerEn} ({destinations}).");
        }
        if (effect.GeneratedCard is { } card)
        {
            var names = Names(card);
            return effect.Kind switch
            {
                EffectKind.WitheringPresence => chinese ? (zh, $"{playerEach}每打出 {n} 张牌，向其手牌加入 1 张{names.Chinese}。") :
                    (en, $"For every {n} cards played by {playerEn}, adds one {names.English} to their hand."),
                EffectKind.PersonalHive => chinese ? (zh, $"受到攻击命中时，向攻击者的抽牌堆洗入 {n} 张{names.Chinese}。") :
                    (en, $"When hit by an attack, adds {n} {names.English} to the attacker's draw pile."),
                _ => chinese ? (zh, $"每段打穿格挡的攻击向玩家的弃牌堆加入 {n} 张{names.Chinese}。") :
                    (en, $"Adds {n} {names.English} to the player's discard pile per unblocked attack hit.")
            };
        }

        return effect.Kind switch
        {
            EffectKind.Wait => chinese ? (zh, "本回合准备，保留剩余行动力。") : (en, "Waits this turn and retains unspent action value."),
            EffectKind.TemporaryThorns => chinese ? (zh, $"获得 {n} 层荆棘，持续到下个玩家回合结束。") :
                (en, $"Gains {n} Thorns through the next player turn."),
            EffectKind.PainfulStabs => chinese ? (zh, $"每段打穿格挡的攻击向玩家弃牌堆加入 {n} 张伤口。") :
                (en, $"Adds {n} Wounds to the player's discard pile per unblocked attack hit."),
            EffectKind.Smoggy or EffectKind.Tender => chinese ? (zh, $"对{player}施加{zh}，持续到战斗结束。") :
                (en, $"Applies {en} to {playerEn} for the rest of combat."),
            EffectKind.Shrink => chinese ? (zh, $"对{player}施加缩小，持续到施加者死亡。") :
                (en, $"Applies Shrink to {playerEn} until the caster dies."),
            EffectKind.Dampen => chinese ? (zh, $"使{player}已升级的牌降级，施加者死亡后恢复。") :
                (en, $"Downgrades upgraded cards for {playerEn} until the caster dies."),
            EffectKind.Hex => chinese ? (zh, $"给{player}可附加恶咒的牌赋予虚无，持续到施加者死亡。") :
                (en, $"Makes eligible cards Ethereal for {playerEn} until the caster dies."),
            EffectKind.DrawDown => chinese ? (zh, $"{playerEach}下回合开始时少抽 {n} 张牌，扣不完则顺延。") :
                (en, $"At the start of next turn, {playerEn} draws {n} fewer cards. Any unused reduction carries over."),
            EffectKind.EnergyDown => chinese ? (zh, $"{playerEach}下回合开始时扣除 {n} 点能量，扣不完则顺延。") :
                (en, $"At the start of next turn, {playerEn} loses {n} Energy. Any unused reduction carries over."),
            EffectKind.Soulbind => chinese ? (zh, $"对{player}各施加 {n} 层魂缚锁链。") :
                (en, $"Applies {n} Chains of Binding to {playerEn}."),
            EffectKind.Block or EffectKind.Heal => chinese ? (zh, $"自身{zh} {n} 点。") : (en, $"Self: {n} {en}."),
            EffectKind.HardenedShell => chinese ? (zh, $"将每回合失去的生命上限设为 {n}。") :
                (en, $"Sets its maximum HP loss per turn to {n}."),
            EffectKind.HardToKill => chinese ? (zh, $"将单次伤害或生命减少的上限设为 {n}。") :
                (en, $"Sets its damage and HP loss cap per instance to {n}."),
            EffectKind.StrengthDown or EffectKind.DexterityDown => chinese ? (zh, $"使{playerEach}{zh} {n} 点。") :
                (en, $"Applies {n} {en} to {playerEn}."),
            _ when effect.Target == EffectTarget.Player => chinese ? (zh, $"对{playerEach}施加 {n} 层{zh}。") :
                (en, $"Applies {n} {en} to {playerEn}."),
            _ => chinese ? (zh, $"获得 {n} 层{zh}。") : (en, $"Gains {n} {en}.")
        };
    }

    internal static (string Chinese, string English) Names(EffectKind kind) => kind switch
        {
            EffectKind.Attack => ("攻击", "Attack"), EffectKind.Block => ("格挡", "Block"), EffectKind.Heal => ("回复", "Heal"),
            EffectKind.Strength => ("力量", "Strength"), EffectKind.Ritual => ("仪式", "Ritual"),
            EffectKind.SteamEruption => ("蒸汽喷发", "Steam Eruption"),
            EffectKind.Regen => ("再生", "Regeneration"),
            EffectKind.Plating => ("甲覆", "Plating"),
            EffectKind.HardenedShell => ("坚硬外壳", "Hardened Shell"), EffectKind.HardToKill => ("难以杀灭", "Hard to Kill"),
            EffectKind.Wait => ("准备", "Wait"),
            EffectKind.Intangible => ("无实体", "Intangible"), EffectKind.Thorns => ("荆棘", "Thorns"),
            EffectKind.TemporaryThorns => ("临时荆棘", "Temporary Thorns"),
            EffectKind.Weak => ("虚弱", "Weak"), EffectKind.Vulnerable => ("易伤", "Vulnerable"),
            EffectKind.Frail => ("脆弱", "Frail"), EffectKind.Poison => ("中毒", "Poison"),
            EffectKind.Tangled => ("缠结", "Tangled"), EffectKind.Ringing => ("昏眩", "Ringing"),
            EffectKind.StrengthDown => ("降力量", "Strength Down"), EffectKind.DexterityDown => ("降敏捷", "Dexterity Down"),
            EffectKind.Smoggy => ("烟雾弥漫", "Smoggy"), EffectKind.Tender => ("柔嫩", "Tender"),
            EffectKind.Shrink => ("缩小", "Shrink"),
            EffectKind.Dampen => ("抑制", "Dampen"), EffectKind.Hex => ("恶咒", "Hex"),
            EffectKind.Soulbind => ("魂缚", "Soulbind"), EffectKind.DrawDown => ("抽牌减少", "Draw Down"),
            EffectKind.EnergyDown => ("能量减少", "Energy Down"),
            EffectKind.PersonalHive => ("人体蜂房", "Personal Hive"), EffectKind.VitalSpark => ("活力火花", "Vital Spark"),
            EffectKind.WitheringPresence => ("凋萎存在", "Withering Presence"), EffectKind.Envenom => ("涂毒", "Envenom"),
            EffectKind.PaperCuts => ("纸伤难愈", "Paper Cuts"), EffectKind.Dazed => ("晕眩", "Dazed"),
            EffectKind.Wound => ("伤口", "Wound"), EffectKind.Burn => ("灼伤", "Burn"),
            EffectKind.Beckon => ("呼唤", "Beckon"), EffectKind.Wither => ("凋萎", "Wither"),
            EffectKind.Amnesia => ("失忆", "Amnesia"), EffectKind.MindRot => ("心灵腐化", "Mind Rot"),
            EffectKind.Sadness => ("悲伤", "Sadness"), EffectKind.Infection => ("感染", "Infection"),
            EffectKind.Sloth => ("懒惰", "Sloth"), EffectKind.Toxic => ("毒素", "Toxic"),
            EffectKind.Soot => ("煤灰", "Soot"), EffectKind.Disintegration => ("瓦解", "Disintegration"),
            EffectKind.Debris => ("碎屑", "Debris"), EffectKind.Void => ("虚空", "Void"),
            EffectKind.WasteAway => ("衰朽", "Waste Away"), EffectKind.Slimed => ("黏液", "Slimed"),
            EffectKind.Enthralled => ("执迷", "Enthralled"), EffectKind.Normality => ("凡庸", "Normality"),
            EffectKind.BadLuck => ("霉运", "Bad Luck"), EffectKind.Debt => ("债务", "Debt"),
            EffectKind.Artifact => ("人工制品", "Artifact"), EffectKind.Barricade => ("壁垒", "Barricade"),
            EffectKind.Slippery => ("滑溜", "Slippery"),
            EffectKind.Vigor => ("活力", "Vigor"), EffectKind.Enrage => ("激怒", "Enrage"),
            EffectKind.PainfulStabs => ("疼痛戳刺", "Painful Stabs"),
            _ => (kind.ToString(), kind.ToString())
        };

    internal static string DescribeDestinations(ActionEffect effect, bool chinese = true) =>
        string.Join(chinese ? "、" : ", ", effect.CardDestinations.Select(destination =>
        {
            var (zh, en) = destination.Pile switch
            {
                StatusPile.Hand => ("手牌", "the hand"),
                StatusPile.Top => ("抽牌堆顶", "the top of the draw pile"),
                StatusPile.Draw => ("洗入抽牌堆", "random positions in the draw pile"),
                _ => ("弃牌堆", "the discard pile")
            };
            return chinese ? $"{zh} {destination.Amount} 张" : $"{destination.Amount} to {en}";
        }));
}
