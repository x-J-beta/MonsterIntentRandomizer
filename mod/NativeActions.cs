using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
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
            effects.Count == 0 ? [CreateIntent(new ActionEffect(EffectKind.Wait, 0))] :
                effects.Select(effect => CreateIntent(Preview(monster.Creature, effect))).ToArray());
    }

    private static ActionEffect Preview(Creature self, ActionEffect effect)
    {
        if (self.CombatState is not { Players.Count: > 1 } combat)
            return effect;
        if (effect.Kind == EffectKind.Block)
        {
            int block = (int)Hook.ModifyBlock(combat, self, effect.Amount, ValueProp.Move, null, null, out _);
            return effect with { Amount = Math.Max(0, block) };
        }
        PowerModel? power = effect.Kind switch
        {
            EffectKind.Artifact => ModelDb.Power<ArtifactPower>(),
            EffectKind.HardenedShell => ModelDb.Power<HardenedShellPower>(),
            EffectKind.Plating => ModelDb.Power<PlatingPower>(),
            EffectKind.Regen => ModelDb.Power<RegenPower>(),
            EffectKind.Slippery => ModelDb.Power<SlipperyPower>(),
            _ => null
        };
        return power == null ? effect : effect with { Amount = (int)ScaledAmount(self, power, effect.Amount) };
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

            if (!CombatIntegration.CanUseEffect(combat, self, effect.Kind))
            {
                if (effect.Kind == EffectKind.VitalSpark)
                    Log.Warn($"[MonsterIntentRandomizer] {monster.Id.Entry} could not execute its Vital Spark intent under the active effect policy.");
                continue;
            }
            if (effect.Unique && !CombatIntegration.SpendSpecialStatus(combat, self, effect.Kind))
                continue;
            if (ActionEffect.IsPlayerEffect(effect.Kind))
                CombatIntegration.RecordPlayerEffect(combat, self, effect.Kind);
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
                    await PowerCmd.Apply<WeakPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Vulnerable:
                    await PowerCmd.Apply<VulnerablePower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Tangled:
                    await PowerCmd.Apply<TangledPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Ringing:
                    await PowerCmd.Apply<RingingPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.StrengthDown:
                    await PowerCmd.Apply<StrengthPower>(choice, targets, -amount, self, null);
                    break;
                case EffectKind.DexterityDown:
                    await PowerCmd.Apply<DexterityPower>(choice, targets, -amount, self, null);
                    break;
                case EffectKind.Smoggy:
                    await PowerCmd.Apply<SmoggyPower>(choice, targets, 1, self, null);
                    break;
                case EffectKind.Tender:
                    await PowerCmd.Apply<TenderPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Frail:
                    await PowerCmd.Apply<FrailPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Poison:
                    await PowerCmd.Apply<PoisonPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Shrink:
                    await PowerCmd.Apply<ShrinkPower>(choice, targets, -1, self, null);
                    break;
                case EffectKind.Dampen:
                    foreach (var target in targets)
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
                    await PowerCmd.Apply<HexPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.Soulbind:
                    await PowerCmd.Apply<ChainsOfBindingPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.DrawDown:
                    await PowerCmd.Apply<NextTurnDrawDownPower>(choice, targets, amount, self, null);
                    break;
                case EffectKind.EnergyDown:
                    await PowerCmd.Apply<NextTurnEnergyDownPower>(choice, targets, amount, self, null);
                    break;
                case var _ when effect.IsInsertedCard:
                    await InsertStatus(targets, effect);
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
        "WITHERING_PRESENCE" => target => (PowerModel?)RandomizedCardPower.Find(target, EffectKind.WitheringPresence) ??
            target.GetPowerInstances<WitheringPresencePower>().FirstOrDefault(),
        "ENVENOM" => Get<EnvenomPower>,
        "PAPER_CUTS" => Get<PaperCutsPower>,
        "PAINFUL_STABS" => Get<RandomizedPainfulStabsPower>,
        _ => null
    };

    private static PowerModel? Get<T>(Creature target) where T : PowerModel => target.GetPower<T>();

    internal static bool SupportsOpening(string id) => id == "STUN" || OpeningPower(id) != null;

    internal static bool HasSelfEffect(EffectKind kind, Creature target) => kind == EffectKind.Block
        ? target.Block > 0 : OpeningPower(EffectPolicy.Id(kind))?.Invoke(target) != null;

    private static bool HasOpening(Creature target, string id) => id == "STUN"
        ? target.IsStunned : OpeningPower(id)?.Invoke(target) != null;

    internal static int ThresholdFor(Creature target, string id) => id switch
    {
        "HARDENED_SHELL" => target.GetPower<HardenedShellPower>() is { } shell ?
            BaseAmount(target, shell, shell.Amount) : 0,
        "HARD_TO_KILL" => target.GetPower<HardToKillPower>()?.Amount ?? 0,
        "WITHERING_PRESENCE" => RandomizedCardPower.Find(target, EffectKind.WitheringPresence)?.Amount ?? 0,
        _ => 0
    };

    internal static bool CanApplyOpening(Creature target, OpeningOption option) =>
        option.Stackable || !HasOpening(target, option.State.Id);

    private static decimal ScaledAmount(Creature target, PowerModel power, decimal amount) =>
        amount != 0 && target.CombatState is { Players.Count: > 1 } combat &&
            (target.IsPrimaryEnemy || target.IsSecondaryEnemy) && power.ShouldScaleInMultiplayer
            ? power.GetScaledAmountForMultiplayer(combat, target, amount, target, null)
            : amount;

    private static int BaseAmount(Creature target, PowerModel power, int actualAmount)
    {
        if (actualAmount <= 0)
            return actualAmount;
        // Native enemy power scaling uses a multiplier and, for Artifact, a flat offset.
        decimal first = ScaledAmount(target, power, 1);
        decimal step = ScaledAmount(target, power, 2) - first;
        return (int)Math.Ceiling((actualAmount - first + step) / step);
    }

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
        var choice = new ThrowingPlayerChoiceContext();
        return state.Id switch
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
            "WITHERING_PRESENCE" => await ApplyWitheringPresence(),
            "ENVENOM" => await Apply<EnvenomPower>(),
            "PAPER_CUTS" => await Apply<PaperCutsPower>(),
            "SLIPPERY" => await Apply<SlipperyPower>(),
            "REGEN" => await Apply<RegenPower>(),
            "PLATING" => await Apply<PlatingPower>(),
            "HARDENED_SHELL" => await ApplyThreshold<HardenedShellPower>(),
            "HARD_TO_KILL" => await ApplyThreshold<HardToKillPower>(),
            "THORNS" => await Apply<ThornsPower>(),
            "TEMPORARY_THORNS" => await ApplyTemporaryThorns(target, state.Amount),
            "ARTIFACT" => await Apply<ArtifactPower>(),
            "BARRICADE" => await Apply<BarricadePower>(),
            "INTANGIBLE" => await ApplyIntangible(),
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

        async Task<int> Apply<T>() where T : PowerModel
        {
            var existing = target.GetPower<T>();
            int before = existing?.Amount ?? 0;
            // The native command scales a new instance. For an instance currently on the creature, scale the added amount here.
            decimal amount = existing == null ? state.Amount : ScaledAmount(target, existing, state.Amount);
            var power = await PowerCmd.Apply<T>(choice, target, amount, target, null);
            int applied = (target.GetPower<T>()?.Amount ?? 0) - before;
            return power == null ? applied : BaseAmount(target, power, applied);
        }

        async Task<int> ApplyIntangible()
        {
            bool skipFirstTick = target.CombatState?.CurrentSide == CombatSide.Enemy &&
                !target.HasPower<IntangiblePower>();
            int applied = await Apply<IntangiblePower>();
            if (skipFirstTick && applied > 0 && target.GetPower<IntangiblePower>() is { } power)
                power.SkipNextDurationTick = true;
            return applied;
        }

        async Task<int> ApplyThreshold<T>() where T : PowerModel
        {
            var power = target.GetPower<T>();
            if (power == null)
                await PowerCmd.Apply<T>(choice, target, state.Amount, target, null);
            else
            {
                int threshold = (int)ScaledAmount(target, power, state.Amount);
                if (threshold < power.Amount)
                    await PowerCmd.ModifyAmount(choice, power, threshold - power.Amount, target, null);
            }
            return ThresholdFor(target, state.Id);
        }

        async Task<int> ApplyGenerator<T>() where T : RandomizedCardPower
        {
            var power = target.GetPower<T>();
            int before = power?.Amount ?? 0;
            if (power == null)
            {
                power = (T)ModelDb.Power<T>().ToMutable();
                power.ConfiguredCard = state.GeneratedCard;
                await PowerCmd.Apply(choice, power, target, state.Amount, target, null);
            }
            else if (state.GeneratedCard == null || state.GeneratedCard == power.Card)
                await PowerCmd.ModifyAmount(choice, power, state.Amount, target, null);
            return (target.GetPower<T>()?.Amount ?? 0) - before;
        }

        async Task<int> ApplyWitheringPresence()
        {
            if (target.CombatState is not { } combat)
                return 0;
            foreach (var player in combat.PlayerCreatures)
            {
                var existing = target.GetPowerInstances<RandomizedWitheringPresencePower>()
                    .FirstOrDefault(power => power.Target == player);
                if (existing == null)
                {
                    var power = (RandomizedWitheringPresencePower)ModelDb.Power<RandomizedWitheringPresencePower>().ToMutable();
                    power.ConfiguredCard = state.GeneratedCard;
                    power.Target = player;
                    await PowerCmd.Apply(choice, power, target, state.Amount, target, null);
                }
                else if ((state.GeneratedCard == null || state.GeneratedCard == existing.Card) && state.Amount < existing.Amount)
                    await existing.ReduceThreshold(choice, state.Amount);
            }
            return ThresholdFor(target, state.Id);
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
}
