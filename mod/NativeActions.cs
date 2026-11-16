using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
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
    private const float AnimationLeadTime = 0.2f;

    internal static MoveState Build(MonsterModel monster, ActionCandidate candidate, MonsterRules rules, int[]? animatedAttackHits)
    {
        var effects = candidate.Effects!;
        string? animation = effects.Count == 0 ? null : rules.AnimationFor(candidate.IsAttack
            ? IntentStyle.Attack : ActionEffect.StyleFor(effects[0].Kind));
        return new MoveState(MoveId, targets => Execute(monster, effects, targets,
                candidate.IsAttack ? animation : null, candidate.IsAttack ? null : animation, animatedAttackHits),
            effects.Count == 0 ? [CreateIntent(new ActionEffect(EffectKind.Wait, 0))] : effects.Select(CreateIntent).ToArray());
    }

    private static AbstractIntent CreateIntent(ActionEffect effect) => IntentDetails.Register(effect, effect.Kind switch
    {
        EffectKind.Attack => effect.Hits == 1 ? new SingleAttackIntent(effect.Amount) :
            new MultiAttackIntent(effect.Amount, effect.Hits),
        EffectKind.Wait => new UnknownIntent(),
        EffectKind.Block => new DefendIntent(),
        EffectKind.Heal => new HealIntent(),
        _ when ActionEffect.IsDefensiveBuff(effect.Kind) => new DefendIntent(),
        _ when effect.Target == EffectTarget.Self => new BuffIntent(),
        _ when effect.IsInsertedCard => new StatusIntent(effect.Amount),
        _ => new DebuffIntent()
    });

    private static async Task Execute(MonsterModel monster, IReadOnlyList<ActionEffect> effects, IReadOnlyList<Creature> targets,
        string? attackAnimation, string? supportAnimation, int[]? animatedAttackHits)
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
            {
                if (effect.Kind == EffectKind.VitalSpark)
                    Log.Warn($"[MonsterIntentRandomizer] {monster.Id.Entry} could not execute its Vital Spark intent under the active effect policy.");
                continue;
            }
            if (effect.Unique && !CombatIntegration.SpendSpecialStatus(combat, self, effect.Kind))
                continue;
            if (!string.IsNullOrWhiteSpace(supportAnimation))
            {
                await CreatureCmd.TriggerAnim(self, supportAnimation, AnimationLeadTime);
                supportAnimation = null;
            }
            int amount = effect.Amount;
            switch (effect.Kind)
            {
                case EffectKind.Attack:
                    var attack = DamageCmd.Attack(amount).FromMonster(monster).WithHitCount(effect.Hits)
                        .WithHitFx("vfx/vfx_attack_blunt");
                    SetAttackAnimation(attack, self, attackAnimation, animatedAttackHits);
                    await attack.Execute(choice);
                    break;
                case EffectKind.Block:
                    await CreatureCmd.GainBlock(self, amount, ValueProp.Move, null);
                    break;
                case EffectKind.Heal:
                    await CreatureCmd.Heal(self, amount);
                    break;
                case var buff when ActionEffect.IsMonsterBuff(buff):
                    string id = EffectPolicy.Id(buff);
                    int before = effect.Threshold != null ? ThresholdFor(self, id) : 0;
                    int applied = await ApplyOpening(self, new OpeningState(id, amount, effect.GeneratedCard));
                    if (effect.Threshold is { } pricing)
                    {
                        CombatIntegration.RecordThresholdCost(combat, self,
                            OpeningValuation.ThresholdCost(pricing.State, pricing.Profile, before, applied) * pricing.Scale);
                    }
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
                case EffectKind.Dampen:
                    foreach (var target in living)
                    {
                        if (target.GetPower<DampenPower>() is { } activeDampen)
                        {
                            activeDampen.AddCaster(self);
                            continue;
                        }
                        var power = (DampenPower)ModelDb.Power<DampenPower>().ToMutable();
                        power.AddCaster(self);
                        await PowerCmd.Apply(choice, power, target, 1, self, null);
                    }
                    break;
                case EffectKind.Hex:
                    await PowerCmd.Apply<HexPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Soulbind:
                    await PowerCmd.Apply<ChainsOfBindingPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.DrawDown:
                    await PowerCmd.Apply<NextTurnDrawDownPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.EnergyDown:
                    await PowerCmd.Apply<NextTurnEnergyDownPower>(choice, living, amount, self, null);
                    break;
                case var _ when effect.IsInsertedCard:
                    await InsertStatus(living, effect);
                    if (ActionEffect.IsCurseCard(effect.Kind))
                        CombatIntegration.RecordCurseInsertion(combat, effect.Kind);
                    break;
            }
        }
    }

    internal static Task InsertStatus(IReadOnlyList<Creature> targets, ActionEffect effect) => effect.Kind switch
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
        EffectKind.Enthralled => Insert<Enthralled>(targets, effect),
        EffectKind.Normality => Insert<Normality>(targets, effect),
        EffectKind.BadLuck => Insert<BadLuck>(targets, effect),
        EffectKind.Debt => Insert<Debt>(targets, effect),
        _ => throw new ArgumentOutOfRangeException(nameof(effect))
    };

    private static void SetAttackAnimation(AttackCommand attack, Creature attacker, string? trigger, int[]? animatedHits)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            attack.WithNoAttackerAnim();
            return;
        }

        if (animatedHits == null)
        {
            attack.WithAttackerAnim(trigger, AnimationLeadTime);
            return;
        }

        int hit = 0, animation = 0;
        // The native callback runs once per hit before its visuals and damage.
        attack.WithNoAttackerAnim().AfterAttackerAnim(() =>
        {
            if (animation == animatedHits.Length || hit++ != animatedHits[animation])
                return Task.CompletedTask;

            animation++;
            return CreatureCmd.TriggerAnim(attacker, trigger, AnimationLeadTime);
        });
    }

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
        "STEAM_ERUPTION" => Get<SteamEruptionPower>,
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
        "NEUROSURGE" => Get<NeurosurgePower>,
        "IMBALANCED" => Get<ImbalancedPower>,
        "PERSONAL_HIVE" => target => (PowerModel?)target.GetPower<RandomizedPersonalHivePower>() ?? target.GetPower<PersonalHivePower>(),
        "VITAL_SPARK" => Get<VitalSparkPower>,
        "WITHERING_PRESENCE" => target => (PowerModel?)target.GetPower<RandomizedWitheringPresencePower>() ??
            target.GetPowerInstances<WitheringPresencePower>().FirstOrDefault(),
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
            EffectKind.Dampen => target.HasPower<DampenPower>(),
            EffectKind.Hex => target.HasPower<HexPower>(),
            EffectKind.Soulbind => target.HasPower<ChainsOfBindingPower>(),
            EffectKind.DrawDown => target.HasPower<NextTurnDrawDownPower>(),
            EffectKind.EnergyDown => target.HasPower<NextTurnEnergyDownPower>(),
            EffectKind.DexterityDown => target.HasPower<DexterityPower>(),
            _ => OpeningPower(EffectPolicy.Id(kind))?.Invoke(target) != null
        };
    }

    private static bool HasOpening(Creature target, string id) => id == "STUN"
        ? target.IsStunned : OpeningPower(id)?.Invoke(target) != null;

    internal static int ThresholdFor(Creature target, string id) => id switch
    {
        "HARDENED_SHELL" => target.GetPower<HardenedShellPower>()?.Amount ?? 0,
        "HARD_TO_KILL" => target.GetPower<HardToKillPower>()?.Amount ?? 0,
        "WITHERING_PRESENCE" => target.GetPower<RandomizedWitheringPresencePower>()?.Amount ?? 0,
        _ => 0
    };

    internal static bool CanApplyOpening(Creature target, OpeningOption option) =>
        target.CanReceivePowers && (option.Value > 0 || !target.HasPower<ArtifactPower>()) &&
        (option.Stackable || !HasOpening(target, option.State.Id));

    internal static async Task<int> ApplyOpening(Creature target, OpeningState state)
    {
        if (state.Id == "STUN")
        {
            if (target.Monster?.NextMove is not { } original || target.IsStunned)
                return 0;
            await CreatureCmd.Stun(target, original.Id);
            return target.IsStunned ? 1 : 0;
        }

        if (state.Id == "VITAL_SPARK")
            return await VitalSparkIntegration.Apply(target, state.Amount);
        if (state.Id is "ENVENOM" or "PAPER_CUTS")
            return await ApplyRareBuff(target, state);
        var choice = new ThrowingPlayerChoiceContext();
        var applied = state.Id switch
        {
            "STRENGTH" or "STRENGTH_DOWN" => await Apply<StrengthPower>(),
            "RITUAL" => await Apply<RitualPower>(),
            "VIGOR" => await Apply<VigorPower>(),
            "STEAM_ERUPTION" => await Apply<SteamEruptionPower>(),
            "ENRAGE" => await Apply<EnragePower>(),
            "PAINFUL_STABS" => await ApplyGenerator<RandomizedPainfulStabsPower>(),
            "PERSONAL_HIVE" when TriggeredCardBuffs.UsesNative(EffectKind.PersonalHive, target.Monster?.Id.Entry) =>
                await Apply<PersonalHivePower>(),
            "PERSONAL_HIVE" => await ApplyGenerator<RandomizedPersonalHivePower>(),
            "WITHERING_PRESENCE" => await ApplyGenerator<RandomizedWitheringPresencePower>(),
            "SLIPPERY" => await Apply<SlipperyPower>(),
            "REGEN" => await Apply<RegenPower>(),
            "PLATING" => await Apply<PlatingPower>(),
            "HARDENED_SHELL" => await ApplyThreshold<HardenedShellPower>(),
            "HARD_TO_KILL" => await ApplyThreshold<HardToKillPower>(),
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
            "NEUROSURGE" => await Apply<NeurosurgePower>(),
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

        async Task<int> ApplyThreshold<T>() where T : PowerModel
        {
            var power = target.GetPower<T>();
            if (power == null)
                await PowerCmd.Apply<T>(choice, target, state.Amount, target, null);
            else if (state.Amount < power.Amount)
                await PowerCmd.ModifyAmount(choice, power, state.Amount - power.Amount, target, null);
            return target.GetPower<T>()?.Amount ?? 0;
        }

        async Task<int> ApplyGenerator<T>() where T : RandomizedCardPower
        {
            var power = target.GetPower<T>();
            int before = power?.Amount ?? 0;
            bool threshold = EffectPolicy.IsThreshold(state.Id);
            if (power == null)
            {
                power = (T)ModelDb.Power<T>().ToMutable();
                power.ConfiguredCard = state.GeneratedCard;
                if (threshold)
                    power.Target = target.CombatState?.PlayerCreatures.FirstOrDefault(player => player.IsAlive);
                await PowerCmd.Apply(choice, power, target, state.Amount, target, null);
            }
            else if (state.GeneratedCard == null || state.GeneratedCard == power.Card)
            {
                if (power is RandomizedWitheringPresencePower withering)
                {
                    if (state.Amount < before)
                        await withering.ReduceThreshold(choice, state.Amount);
                }
                else
                    await PowerCmd.ModifyAmount(choice, power, state.Amount, target, null);
            }

            int appliedAmount = target.GetPower<T>()?.Amount ?? 0;
            return threshold ? appliedAmount : appliedAmount - before;
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
        if (owner.CombatState == null)
            return 0;

        int before = OpeningPower(state.Id)?.Invoke(owner)?.Amount ?? 0;
        var choice = new ThrowingPlayerChoiceContext();
        var power = state.Id switch
        {
            "ENVENOM" => (PowerModel?)await PowerCmd.Apply<EnvenomPower>(choice, owner, state.Amount, owner, null),
            "PAPER_CUTS" => await PowerCmd.Apply<PaperCutsPower>(choice, owner, state.Amount, owner, null),
            _ => null
        };
        return power == null ? 0 : power.Amount - before;
    }
}
