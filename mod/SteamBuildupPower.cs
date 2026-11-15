using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace MonsterIntentRandomizer;

public sealed class SteamBuildupPower : PowerModel
{
    private static readonly Func<WaterfallGiant, int> NativeSteamPerTurn =
        AccessTools.MethodDelegate<Func<WaterfallGiant, int>>(AccessTools.PropertyGetter(typeof(WaterfallGiant), "SteamBoostAmount"));
    private static readonly Func<LocManager, string, LocTable> GetTable =
        AccessTools.MethodDelegate<Func<LocManager, string, LocTable>>(AccessTools.Method(typeof(LocManager), "GetTable"));

    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override LocString Title => Localize("title");
    public override LocString Description => Localize("description");

    internal static async Task ApplyAfterOpening(Task original, WaterfallGiant giant)
    {
        await original;
        var owner = giant.Creature;
        if (owner.IsAlive)
            await PowerCmd.Apply<SteamBuildupPower>(new ThrowingPlayerChoiceContext(), owner, NativeSteamPerTurn(giant), owner, null);
    }

    public override async Task AfterSideTurnEnd(PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
    {
        if (side != Owner.Side || CombatState.RoundNumber <= 1 || !Owner.IsAlive)
            return;

        // A native move can already have supplied this turn's Steam Eruption.
        var steam = Owner.GetPower<SteamEruptionPower>();
        int gained = steam == null ? 0 : Math.Max(0, steam.Amount - steam.AmountOnTurnStart);
        if (gained < Amount)
        {
            Flash();
            await PowerCmd.Apply<SteamEruptionPower>(choice, Owner, Amount - gained, Owner, null);
        }
    }

    private LocString Localize(string suffix)
    {
        var manager = LocManager.Instance;
        var table = GetTable(manager, "powers");
        string id = Id.Entry;
        if (!table.IsLocalKey(id + ".title"))
        {
            bool chinese = manager.Language == "zhs";
            table.MergeWith(new()
            {
                [id + ".title"] = chinese ? "蒸汽积蓄" : "Steam Buildup",
                [id + ".description"] = chinese ? "从第 2 回合起，在自身回合结束时获得 {Amount} 层蒸汽喷发。"
                    : "From turn 2, gains {Amount} Steam Eruption at the end of its turn."
            });
        }
        return table.GetLocString(id + "." + suffix);
    }
}
