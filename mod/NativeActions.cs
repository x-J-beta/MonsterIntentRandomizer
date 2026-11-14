using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;

namespace MonsterIntentRandomizer;

internal static class NativeActions
{
    internal const string MoveId = "MIR_RANDOM_MOVE";

    internal static MoveState Build(MonsterModel monster, ActionCandidate candidate) =>
        new(MoveId, targets => Execute(monster, candidate.Effects!, targets),
            candidate.Effects!.Select(CreateIntent).ToArray());

    private static AbstractIntent CreateIntent(ActionEffect effect) => effect.Kind switch
    {
        EffectKind.Attack => effect.Hits == 1 ? new SingleAttackIntent(effect.Amount) :
            new MultiAttackIntent(effect.Amount, effect.Hits),
        EffectKind.Block or EffectKind.Intangible => new DefendIntent(),
        EffectKind.Heal => new HealIntent(),
        EffectKind.Strength or EffectKind.Ritual => new BuffIntent(),
        EffectKind.Dazed or EffectKind.Wound or EffectKind.Burn or EffectKind.Beckon => new StatusIntent(effect.Amount),
        _ => new DebuffIntent()
    };

    private static async Task Execute(MonsterModel monster, IReadOnlyList<ActionEffect> effects, IReadOnlyList<Creature> targets)
    {
        Creature self = monster.Creature;
        var choice = new ThrowingPlayerChoiceContext();
        foreach (var effect in effects)
        {
            if (self.IsDead || self.CombatState is not { } combat)
                return;

            var living = targets.Where(target => target.IsAlive && target.CombatState == combat).ToArray();
            if (living.Length == 0)
                return;

            int amount = effect.Amount;
            switch (effect.Kind)
            {
                case EffectKind.Attack:
                    await DamageCmd.Attack(amount).FromMonster(monster).WithHitCount(effect.Hits)
                        .WithNoAttackerAnim().WithHitFx("vfx/vfx_attack_blunt").Execute(choice);
                    break;
                case EffectKind.Block:
                    await CreatureCmd.GainBlock(self, amount, ValueProp.Move, null);
                    break;
                case EffectKind.Heal:
                    await CreatureCmd.Heal(self, amount);
                    break;
                case EffectKind.Strength:
                    await PowerCmd.Apply<StrengthPower>(choice, self, amount, self, null);
                    break;
                case EffectKind.Ritual:
                    await PowerCmd.Apply<RitualPower>(choice, self, amount, self, null);
                    break;
                case EffectKind.Intangible:
                    await PowerCmd.Apply<IntangiblePower>(choice, self, amount, self, null);
                    break;
                case EffectKind.Weak:
                    await PowerCmd.Apply<WeakPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Vulnerable:
                    await PowerCmd.Apply<VulnerablePower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Tangled:
                    await PowerCmd.Apply<TangledPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Ringing:
                    await PowerCmd.Apply<RingingPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.StrengthDown:
                    await PowerCmd.Apply<StrengthPower>(choice, living, -amount, self, null);
                    break;
                case EffectKind.DexterityDown:
                    await PowerCmd.Apply<DexterityPower>(choice, living, -amount, self, null);
                    break;
                case EffectKind.Dazed:
                    await Insert<Dazed>(living, effect);
                    break;
                case EffectKind.Wound:
                    await Insert<Wound>(living, effect);
                    break;
                case EffectKind.Burn:
                    await Insert<Burn>(living, effect);
                    break;
                case EffectKind.Beckon:
                    await Insert<Beckon>(living, effect);
                    break;
            }
        }
    }

    private static Task Insert<T>(IReadOnlyList<Creature> targets, ActionEffect effect) where T : CardModel
    {
        var (pile, position) = effect.Pile switch
        {
            StatusPile.Hand => (PileType.Hand, CardPilePosition.Bottom),
            StatusPile.Top => (PileType.Draw, CardPilePosition.Top),
            StatusPile.Draw => (PileType.Draw, CardPilePosition.Random),
            StatusPile.Bottom => (PileType.Draw, CardPilePosition.Bottom),
            _ => (PileType.Discard, CardPilePosition.Bottom)
        };
        return CardPileCmd.AddToCombatAndPreview<T>(targets, pile, effect.Amount, null, position);
    }

    // Both strength variants map to the same native power.
    private static Func<Creature, PowerModel?>? OpeningPower(string id) => id switch
    {
        "STRENGTH" or "STRENGTH_DOWN" => Get<StrengthPower>,
        "RITUAL" => Get<RitualPower>,
        "REGEN" => Get<RegenPower>,
        "PLATING" => Get<PlatingPower>,
        "THORNS" => Get<ThornsPower>,
        "ARTIFACT" => Get<ArtifactPower>,
        "INTANGIBLE" => Get<IntangiblePower>,
        "WEAK" => Get<WeakPower>,
        "VULNERABLE" => Get<VulnerablePower>,
        "DEMISE" => Get<DemisePower>,
        "DOOM" => Get<DoomPower>,
        "IMBALANCED" => Get<ImbalancedPower>,
        _ => null
    };

    private static PowerModel? Get<T>(Creature target) where T : PowerModel => target.GetPower<T>();

    internal static bool SupportsOpening(string id) => id == "STUN" || OpeningPower(id) != null;

    internal static bool CanApplyOpening(Creature target, OpeningOption option) =>
        target.CanReceivePowers && (option.Value > 0 || !target.HasPower<ArtifactPower>()) &&
        (option.State.Id == "STUN" ? !target.IsStunned : OpeningPower(option.State.Id)?.Invoke(target) == null);

    internal static async Task<int> ApplyOpening(Creature target, OpeningState state)
    {
        if (state.Id == "STUN")
        {
            if (target.Monster?.NextMove is not { } original || target.IsStunned)
                return 0;
            await CreatureCmd.Stun(target, original.Id);
            return target.IsStunned ? 1 : 0;
        }

        var choice = new ThrowingPlayerChoiceContext();
        var applied = state.Id switch
        {
            "STRENGTH" or "STRENGTH_DOWN" => await Apply<StrengthPower>(),
            "RITUAL" => await Apply<RitualPower>(),
            "REGEN" => await Apply<RegenPower>(),
            "PLATING" => await Apply<PlatingPower>(),
            "THORNS" => await Apply<ThornsPower>(),
            "ARTIFACT" => await Apply<ArtifactPower>(),
            "INTANGIBLE" => await Apply<IntangiblePower>(),
            "WEAK" => await Apply<WeakPower>(),
            "VULNERABLE" => await Apply<VulnerablePower>(),
            "DEMISE" => await Apply<DemisePower>(),
            "DOOM" => await Apply<DoomPower>(),
            "IMBALANCED" => await Apply<ImbalancedPower>(),
            _ => 0
        };
        return applied;

        async Task<int> Apply<T>() where T : PowerModel
        {
            int before = target.GetPower<T>()?.Amount ?? 0;
            await PowerCmd.Apply<T>(choice, target, state.Amount, target, null);
            return (target.GetPower<T>()?.Amount ?? 0) - before;
        }
    }
}
