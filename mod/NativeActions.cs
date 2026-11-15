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
            candidate.Effects!.Count == 0 ? [CreateIntent(new ActionEffect(EffectKind.Wait, 0))] :
                candidate.Effects.Select(CreateIntent).ToArray());

    private static AbstractIntent CreateIntent(ActionEffect effect) => IntentDetails.Register(effect, effect.Kind switch
    {
        EffectKind.Attack => effect.Hits == 1 ? new SingleAttackIntent(effect.Amount) :
            new MultiAttackIntent(effect.Amount, effect.Hits),
        EffectKind.Wait => new UnknownIntent(),
        EffectKind.Block or EffectKind.Intangible => new DefendIntent(),
        EffectKind.Heal => new HealIntent(),
        EffectKind.Strength or EffectKind.Ritual or EffectKind.Regen or EffectKind.Thorns or EffectKind.TemporaryThorns => new BuffIntent(),
        EffectKind.Dazed or EffectKind.Wound or EffectKind.Burn or EffectKind.Beckon => new StatusIntent(effect.Amount),
        _ => ActionEffect.IsRareBuff(effect.Kind) ? new BuffIntent() : new DebuffIntent()
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
            if (effect.IsSpecialDebuff && !CombatIntegration.SpendSpecialDebuff(combat))
                continue;

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
                case EffectKind.Regen:
                    await PowerCmd.Apply<RegenPower>(choice, self, amount, self, null);
                    break;
                case EffectKind.Thorns:
                    await PowerCmd.Apply<ThornsPower>(choice, self, amount, self, null);
                    break;
                case EffectKind.TemporaryThorns:
                    await ApplyTemporaryThorns(self, amount);
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
                case EffectKind.Smoggy:
                    await PowerCmd.Apply<SmoggyPower>(choice, living.Where(target => !target.HasPower<SmoggyPower>()), 1, self, null);
                    break;
                case EffectKind.Tender:
                    await PowerCmd.Apply<TenderPower>(choice, living.Where(target => !target.HasPower<TenderPower>()), amount, self, null);
                    break;
                case EffectKind.Frail:
                    await PowerCmd.Apply<FrailPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Poison:
                    await PowerCmd.Apply<PoisonPower>(choice, living, amount, self, null);
                    break;
                case EffectKind.Shrink:
                    await PowerCmd.Apply<ShrinkPower>(choice, living.Where(target => !target.HasPower<ShrinkPower>()), -1, self, null);
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
                case EffectKind.PersonalHive or EffectKind.VitalSpark or EffectKind.WitheringPresence
                    or EffectKind.Envenom or EffectKind.PaperCuts:
                    await ApplyRareBuff(self, new OpeningState(OpeningId(effect.Kind), amount));
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
        "THORNS" or "TEMPORARY_THORNS" => Get<ThornsPower>,
        "ARTIFACT" => Get<ArtifactPower>,
        "INTANGIBLE" => Get<IntangiblePower>,
        "WEAK" => Get<WeakPower>,
        "VULNERABLE" => Get<VulnerablePower>,
        "DEMISE" => Get<DemisePower>,
        "DOOM" => Get<DoomPower>,
        "IMBALANCED" => Get<ImbalancedPower>,
        "PERSONAL_HIVE" => Get<PersonalHivePower>,
        "VITAL_SPARK" => Get<VitalSparkPower>,
        "WITHERING_PRESENCE" => Get<WitheringPresencePower>,
        "ENVENOM" => Get<EnvenomPower>,
        "PAPER_CUTS" => Get<PaperCutsPower>,
        _ => null
    };

    private static PowerModel? Get<T>(Creature target) where T : PowerModel => target.GetPower<T>();

    internal static bool SupportsOpening(string id) => id == "STUN" || OpeningPower(id) != null;

    private static readonly (EffectKind Kind, string Id)[] RareBuffs =
    [
        (EffectKind.PersonalHive, "PERSONAL_HIVE"), (EffectKind.VitalSpark, "VITAL_SPARK"),
        (EffectKind.WitheringPresence, "WITHERING_PRESENCE"), (EffectKind.Envenom, "ENVENOM"),
        (EffectKind.PaperCuts, "PAPER_CUTS")
    ];

    private static string OpeningId(EffectKind kind) => RareBuffs.First(effect => effect.Kind == kind).Id;

    internal static HashSet<EffectKind> ActiveEffects(Creature self, Creature player, IEnumerable<Creature> enemies)
    {
        var active = RareBuffs.Where(effect => HasRareBuff(self, effect.Id, enemies)).Select(effect => effect.Kind).ToHashSet();
        if (player.HasPower<TenderPower>())
            active.Add(EffectKind.Tender);
        if (player.HasPower<ShrinkPower>())
            active.Add(EffectKind.Shrink);
        if (player.HasPower<ChainsOfBindingPower>())
            active.Add(EffectKind.Soulbind);
        if (player.HasPower<MindRotPower>())
            active.Add(EffectKind.DrawDown);
        if (player.HasPower<WasteAwayPower>())
            active.Add(EffectKind.EnergyDown);
        return active;
    }

    private static bool HasRareBuff(Creature self, string id, IEnumerable<Creature> enemies) =>
        OpeningValuation.IsEncounterUnique(id)
            ? enemies.Any(enemy => enemy.IsAlive && OpeningPower(id)!(enemy) != null)
            : OpeningPower(id)!(self) != null;

    internal static bool CanApplyOpening(Creature target, OpeningOption option, IEnumerable<Creature> enemies) =>
        target.CanReceivePowers && (option.Value > 0 || !target.HasPower<ArtifactPower>()) &&
        (option.State.Id == "STUN" ? !target.IsStunned : !HasRareBuff(target, option.State.Id, enemies));

    internal static async Task<int> ApplyOpening(Creature target, OpeningState state)
    {
        if (state.Id == "STUN")
        {
            if (target.Monster?.NextMove is not { } original || target.IsStunned)
                return 0;
            await CreatureCmd.Stun(target, original.Id);
            return target.IsStunned ? 1 : 0;
        }

        if (RareBuffs.Any(effect => effect.Id == state.Id))
            return await ApplyRareBuff(target, state);

        var choice = new ThrowingPlayerChoiceContext();
        var applied = state.Id switch
        {
            "STRENGTH" or "STRENGTH_DOWN" => await Apply<StrengthPower>(),
            "RITUAL" => await Apply<RitualPower>(),
            "REGEN" => await Apply<RegenPower>(),
            "PLATING" => await Apply<PlatingPower>(),
            "THORNS" => await Apply<ThornsPower>(),
            "TEMPORARY_THORNS" => await ApplyTemporaryThorns(target, state.Amount),
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
        if (owner.CombatState is not { } combat || HasRareBuff(owner, state.Id, combat.Enemies))
            return 0;

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
        if (power is VitalSparkPower spark)
            await spark.BeforeCombatStart();
        return power?.Amount ?? 0;

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
