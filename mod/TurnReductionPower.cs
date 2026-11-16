using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace MonsterIntentRandomizer;

public abstract class TurnReductionPower : CustomPower
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Counter;

    protected Task Consume(int used)
    {
        if (used == 0)
            return Task.CompletedTask;
        Flash();
        return PowerCmd.ModifyAmount(new ThrowingPlayerChoiceContext(), this, -used, Owner, null);
    }
}

public sealed class NextTurnDrawDownPower : TurnReductionPower
{
    private int used;
    internal override PowerModel NativePower => ModelDb.Power<MindRotPower>();
    protected override (string Chinese, string English) DescriptionText =>
        ("下回合开始时少抽 {Amount} 张牌。扣不完的张数延至下一回合。",
         "Draw {Amount} fewer cards at the start of next turn. Any unused reduction carries over.");

    public override decimal ModifyHandDrawLate(Player player, decimal count)
    {
        if (player != Owner.Player)
            return count;
        used = Math.Clamp((int)count, 0, Amount);
        return count - used;
    }

    public override Task AfterModifyingHandDraw()
    {
        int applied = used;
        used = 0;
        return Consume(applied);
    }
}

public sealed class NextTurnEnergyDownPower : TurnReductionPower
{
    internal override PowerModel NativePower => ModelDb.Power<WasteAwayPower>();
    protected override (string Chinese, string English) DescriptionText =>
        ("下回合开始时扣除 {Amount} 点能量。扣不完的点数延至下一回合。",
         "Lose {Amount} Energy at the start of next turn. Any unused reduction carries over.");

    public override async Task AfterEnergyResetLate(Player player)
    {
        if (player != Owner.Player || player.PlayerCombatState is not { } combatState)
            return;
        int used = Math.Clamp(combatState.Energy, 0, Amount);
        await PlayerCmd.LoseEnergy(used, player);
        await Consume(used);
    }
}
