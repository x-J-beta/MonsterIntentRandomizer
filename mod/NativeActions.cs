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
using VoidCard = MegaCrit.Sts2.Core.Models.Cards.Void;

namespace MonsterIntentRandomizer;

internal static class NativeActions
{
    internal const string MoveId = "MIR_RANDOM_MOVE";

    internal static MoveState Build(MonsterModel monster, ActionCandidate candidate) =>
        new(MoveId, targets => Execute(monster, candidate.Effects!, targets),
            candidate.Effects!.Count == 0 ? [CreateIntent(new ActionEffect(EffectKind.Wait, 0))] :
                candidate.Effects.Select(CreateIntent).ToArray());

    private static AbstractIntent CreateIntent(ActionEffect effect) => IntentDetails.Register(effect, effect.Kind switch
    {
        EffectKind.Attack => effect.Hits == 1 ? new SingleAttackIntent(effect.Amount) :
            new MultiAttackIntent(effect.Amount, effect.Hits),
        EffectKind.Wait => new UnknownIntent(),
        EffectKind.Block => new DefendIntent(),
        EffectKind.Heal => new HealIntent(),
        _ when ActionEffect.IsDefensiveBuff(effect.Kind) => new DefendIntent(),
        _ when effect.Target == EffectTarget.Self => new BuffIntent(),
        _ when effect.IsStatusCard => new StatusIntent(effect.Amount),
        _ => new DebuffIntent()
    });

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
            if (!CombatIntegration.CanUseEffect(combat, self, living[0], effect.Kind))
                continue;
            if (effect.IsSpecialDebuff && !CombatIntegration.SpendSpecialDebuff(combat))
                continue;
            CombatIntegration.RecordEffect(combat, effect.Kind);

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
                case var buff when ActionEffect.IsMonsterBuff(buff):
                    await ApplyOpening(self, new OpeningState(EffectPolicy.Id(buff), amount));
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
                case EffectKind.Smoggy:
                    await PowerCmd.Apply<SmoggyPower>(choice, living, 1, self, null);
                    break;
                case EffectKind.Tender:
                    await PowerCmd.Apply<TenderPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Frail:
                    await PowerCmd.Apply<FrailPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Poison:
                    await PowerCmd.Apply<PoisonPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Shrink:
                    await PowerCmd.Apply<ShrinkPower>(choice, living, -1, self, null);
                    break;
                case EffectKind.Soulbind:
                    await PowerCmd.Apply<ChainsOfBindingPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.DrawDown:
                    await PowerCmd.Apply<MindRotPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.EnergyDown:
                    await PowerCmd.Apply<WasteAwayPower>(choice, living, amount, self, null);
                    break;
                case var card when ActionEffect.IsCard(card):
                    await InsertStatus(living, effect);
                    break;
            }
        }
    }

    private static Task InsertStatus(IReadOnlyList<Creature> targets, ActionEffect effect) => effect.Kind switch
    {
        EffectKind.Dazed => Insert<Dazed>(targets, effect),
        EffectKind.Wound => Insert<Wound>(targets, effect),
        EffectKind.Burn => Insert<Burn>(targets, effect),
        EffectKind.Beckon => Insert<Beckon>(targets, effect),
        EffectKind.Wither => Insert<Wither>(targets, effect),
        EffectKind.Amnesia => Insert<Amnesia>(targets, effect),
        EffectKind.MindRot => Insert<MindRot>(targets, effect),
        EffectKind.Sadness => Insert<Sadness>(targets, effect),
        EffectKind.Infection => Insert<Infection>(targets, effect),
        EffectKind.Sloth => Insert<Sloth>(targets, effect),
        EffectKind.Toxic => Insert<Toxic>(targets, effect),
        EffectKind.Soot => Insert<Soot>(targets, effect),
        EffectKind.Disintegration => Insert<Disintegration>(targets, effect),
        EffectKind.Debris => Insert<Debris>(targets, effect),
        EffectKind.Void => Insert<VoidCard>(targets, effect),
        EffectKind.WasteAway => Insert<WasteAway>(targets, effect),
        EffectKind.Slimed => Insert<Slimed>(targets, effect),
        _ => throw new ArgumentOutOfRangeException(nameof(effect))
    };

    private static async Task Insert<T>(IReadOnlyList<Creature> targets, ActionEffect effect) where T : CardModel
    {
        foreach (var destination in effect.CardDestinations.OrderBy(destination => destination.Pile is StatusPile.Top or StatusPile.Bottom))
        {
            var (pile, position) = destination.Pile switch
            {
                StatusPile.Hand => (PileType.Hand, CardPilePosition.Bottom),
                StatusPile.Top => (PileType.Draw, CardPilePosition.Top),
                StatusPile.Draw => (PileType.Draw, CardPilePosition.Random),
                StatusPile.Bottom => (PileType.Draw, CardPilePosition.Bottom),
                _ => (PileType.Discard, CardPilePosition.Bottom)
            };
            await CardPileCmd.AddToCombatAndPreview<T>(targets, pile, destination.Amount, null, position);
        }
    }

    // Both strength variants map to the same native power.
    private static Func<Creature, PowerModel?>? OpeningPower(string id) => id switch
    {
        "STRENGTH" or "STRENGTH_DOWN" => Get<StrengthPower>,
        "RITUAL" => Get<RitualPower>,
        "VIGOR" => Get<VigorPower>,
        "ENRAGE" => Get<EnragePower>,
        "SLIPPERY" => Get<SlipperyPower>,
        "REGEN" => Get<RegenPower>,
        "PLATING" => Get<PlatingPower>,
        "HARDENED_SHELL" => Get<HardenedShellPower>,
        "HARD_TO_KILL" => Get<HardToKillPower>,
        "THORNS" or "TEMPORARY_THORNS" => Get<ThornsPower>,
        "ARTIFACT" => Get<ArtifactPower>,
        "BARRICADE" => Get<BarricadePower>,
        "INTANGIBLE" => Get<IntangiblePower>,
        "WEAK" => Get<WeakPower>,
        "POISON" => Get<PoisonPower>,
        "SLOW" => Get<SlowPower>,
        "VULNERABLE" => Get<VulnerablePower>,
        "DEMISE" => Get<DemisePower>,
        "DOOM" => Get<DoomPower>,
        "IMBALANCED" => Get<ImbalancedPower>,
        "PERSONAL_HIVE" => Get<PersonalHivePower>,
        "VITAL_SPARK" => Get<VitalSparkPower>,
        "WITHERING_PRESENCE" => Get<WitheringPresencePower>,
        "ENVENOM" => Get<EnvenomPower>,
        "PAPER_CUTS" => Get<PaperCutsPower>,
        "PAINFUL_STABS" => Get<RandomizedPainfulStabsPower>,
        _ => null
    };

    private static PowerModel? Get<T>(Creature target) where T : PowerModel => target.GetPower<T>();

    internal static bool SupportsOpening(string id) => id == "STUN" || OpeningPower(id) != null;

    internal static bool HasActionEffect(EffectKind kind, Creature target)
    {
        if (ActionEffect.IsCard(kind))
        {
            string id = EffectPolicy.Id(kind);
            return target.Player?.PlayerCombatState is { } combat && combat.Hand.Cards.Concat(combat.DrawPile.Cards)
                .Concat(combat.DiscardPile.Cards).Any(card => card.Id.Entry == id);
        }
        return kind switch
        {
            EffectKind.Block => target.Block > 0,
            EffectKind.Tangled => target.HasPower<TangledPower>(),
            EffectKind.Ringing => target.HasPower<RingingPower>(),
            EffectKind.Smoggy => target.HasPower<SmoggyPower>(),
            EffectKind.Tender => target.HasPower<TenderPower>(),
            EffectKind.Frail => target.HasPower<FrailPower>(),
            EffectKind.Poison => target.HasPower<PoisonPower>(),
            EffectKind.Shrink => target.HasPower<ShrinkPower>(),
            EffectKind.Soulbind => target.HasPower<ChainsOfBindingPower>(),
            EffectKind.DrawDown => target.HasPower<MindRotPower>(),
            EffectKind.EnergyDown => target.HasPower<WasteAwayPower>(),
            EffectKind.DexterityDown => target.HasPower<DexterityPower>(),
            _ => OpeningPower(EffectPolicy.Id(kind))?.Invoke(target) != null
        };
    }

    private static bool HasOpening(Creature target, string id) => id == "STUN"
        ? target.IsStunned : OpeningPower(id)?.Invoke(target) != null;

    internal static bool CanApplyOpening(Creature target, OpeningOption option, IEnumerable<Creature> enemies) =>
        target.CanReceivePowers && (option.Value > 0 || !target.HasPower<ArtifactPower>()) &&
        (option.Stackable || !HasOpening(target, option.State.Id)) &&
        (!option.Unique || !enemies.Any(enemy => enemy.IsAlive && HasOpening(enemy, option.State.Id)));

    internal static async Task<int> ApplyOpening(Creature target, OpeningState state)
    {
        if (state.Id == "STUN")
        {
            if (target.Monster?.NextMove is not { } original || target.IsStunned)
                return 0;
            await CreatureCmd.Stun(target, original.Id);
            return target.IsStunned ? 1 : 0;
        }

        if (state.Id is "PERSONAL_HIVE" or "VITAL_SPARK" or "WITHERING_PRESENCE" or "ENVENOM" or "PAPER_CUTS")
            return await ApplyRareBuff(target, state);
        var choice = new ThrowingPlayerChoiceContext();
        var applied = state.Id switch
        {
            "STRENGTH" or "STRENGTH_DOWN" => await Apply<StrengthPower>(),
            "RITUAL" => await Apply<RitualPower>(),
            "VIGOR" => await Apply<VigorPower>(),
            "ENRAGE" => await Apply<EnragePower>(),
            "PAINFUL_STABS" => await Apply<RandomizedPainfulStabsPower>(),
            "SLIPPERY" => await Apply<SlipperyPower>(),
            "REGEN" => await Apply<RegenPower>(),
            "PLATING" => await Apply<PlatingPower>(),
            "HARDENED_SHELL" => await Apply<HardenedShellPower>(),
            "HARD_TO_KILL" => await Apply<HardToKillPower>(),
            "THORNS" => await Apply<ThornsPower>(),
            "TEMPORARY_THORNS" => await ApplyTemporaryThorns(target, state.Amount),
            "ARTIFACT" => await Apply<ArtifactPower>(),
            "BARRICADE" => await Apply<BarricadePower>(),
            "INTANGIBLE" => await Apply<IntangiblePower>(),
            "WEAK" => await Apply<WeakPower>(),
            "POISON" => await Apply<PoisonPower>(),
            "SLOW" => await Apply<SlowPower>(),
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

    private static async Task<int> ApplyTemporaryThorns(Creature target, int amount)
    {
        if (target.CombatState is not { } combat)
            return 0;

        int previous = target.GetPower<ThornsPower>()?.Amount ?? 0;
        var power = await PowerCmd.Apply<ThornsPower>(new ThrowingPlayerChoiceContext(), target, amount, target, null);
        int granted = Math.Max(0, (power?.Amount ?? previous) - previous);
        if (power != null && granted > 0)
            CombatIntegration.TrackTemporaryThorns(combat, power, granted);
        return granted;
    }

    private static async Task<int> ApplyRareBuff(Creature owner, OpeningState state)
    {
        if (owner.CombatState is not { } combat)
            return 0;

        int before = OpeningPower(state.Id)?.Invoke(owner)?.Amount ?? 0;
        var choice = new ThrowingPlayerChoiceContext();
        var power = state.Id switch
        {
            "PERSONAL_HIVE" => (PowerModel?)await PowerCmd.Apply<PersonalHivePower>(choice, owner, state.Amount, owner, null),
            "VITAL_SPARK" => await PowerCmd.Apply<VitalSparkPower>(choice, owner, state.Amount, owner, null),
            "WITHERING_PRESENCE" => await ApplyWithering(),
            "ENVENOM" => await PowerCmd.Apply<EnvenomPower>(choice, owner, state.Amount, owner, null),
            "PAPER_CUTS" => await PowerCmd.Apply<PaperCutsPower>(choice, owner, state.Amount, owner, null),
            _ => null
        };
        return power == null ? 0 : power.Amount - before;

        async Task<WitheringPresencePower?> ApplyWithering()
        {
            if (combat.PlayerCreatures.FirstOrDefault(player => player.IsAlive) is not { } player)
                return null;
            var power = (WitheringPresencePower)ModelDb.Power<WitheringPresencePower>().ToMutable();
            power.Target = player;
            await PowerCmd.Apply(choice, power, owner, state.Amount, owner, null);
            return owner.Powers.Contains(power) ? power : null;
        }
    }
}
