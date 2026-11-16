using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace MonsterIntentRandomizer;

public abstract class RandomizedCardPower : CustomPower
{
    protected abstract EffectKind Kind { get; }
    internal EffectKind? ConfiguredCard { get; set; }
    internal EffectKind Card => ConfiguredCard ?? TriggeredCardBuffs.Types[Kind].Card;
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;

    public override LocString Description
    {
        get
        {
            var text = base.Description;
            var names = IntentDetails.Names(Card);
            text.Add("Card", LocManager.Instance.Language == "zhs" ? names.Chinese : names.English);
            return text;
        }
    }

    protected Task InsertCard(Creature target, int count) =>
        NativeActions.InsertStatus([target], new ActionEffect(Card, count, Pile: TriggeredCardBuffs.Types[Kind].Pile));

    internal static RandomizedCardPower? Find(Creature owner, EffectKind kind) => kind switch
    {
        EffectKind.PersonalHive => owner.GetPower<RandomizedPersonalHivePower>(),
        EffectKind.WitheringPresence => owner.GetPowerInstances<RandomizedWitheringPresencePower>().FirstOrDefault(),
        EffectKind.PainfulStabs => owner.GetPower<RandomizedPainfulStabsPower>(),
        _ => null
    };
}

public sealed class RandomizedPersonalHivePower : RandomizedCardPower
{
    protected override EffectKind Kind => EffectKind.PersonalHive;
    internal override PowerModel NativePower => ModelDb.Power<PersonalHivePower>();
    protected override (string Chinese, string English) DescriptionText =>
        ("受到攻击命中时，将 {Amount} 张[gold]{Card}[/gold]洗入攻击者的抽牌堆。",
         "When hit by an attack, adds {Amount} [gold]{Card}[/gold] to random positions in the attacker's draw pile.");

    public override async Task AfterDamageReceived(PlayerChoiceContext choice, Creature target, DamageResult result,
        ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (target != Owner || dealer == null || !props.IsPoweredAttack())
            return;
        var player = dealer.IsPet ? dealer.PetOwner?.Creature : dealer;
        if (player?.IsPlayer != true)
            return;
        Flash();
        await InsertCard(player, Amount);
    }
}

public sealed class RandomizedWitheringPresencePower : RandomizedCardPower
{
    private int? remaining;
    public override PowerInstanceType InstanceType => PowerInstanceType.Instanced;
    protected override EffectKind Kind => EffectKind.WitheringPresence;
    internal override PowerModel NativePower => ModelDb.Power<WitheringPresencePower>();
    protected override (string Chinese, string English) DescriptionText =>
        ("玩家每打出 {Amount} 张牌，向其手牌加入 1 张[gold]{Card}[/gold]。",
         "Every {Amount} cards played, adds a [gold]{Card}[/gold] to the player's hand.");
    public override int DisplayAmount => remaining ?? Amount;

    internal async Task ReduceThreshold(PlayerChoiceContext choice, int threshold)
    {
        int played = Amount - DisplayAmount;
        await PowerCmd.ModifyAmount(choice, this, threshold - Amount, Owner, null);
        if (Amount > 0)
        {
            remaining = Math.Clamp(Amount - played, 1, Amount);
            InvokeDisplayAmountChanged();
        }
    }

    public override async Task AfterCardPlayed(PlayerChoiceContext choice, CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner.Creature != Target)
            return;
        remaining = DisplayAmount - 1;
        if (remaining == 0)
        {
            Flash();
            await InsertCard(Target, 1);
            remaining = Amount;
        }
        InvokeDisplayAmountChanged();
    }
}
