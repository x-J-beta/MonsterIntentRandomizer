namespace MonsterIntentRandomizer;

internal sealed record SimulatedTurn(int Round, ActionPlan Plan, bool Imbalanced, SimulationMove? OriginalMove)
{
    public double Cost => Plan.Action?.Value ?? 0;
    public int Hits => Plan.Action?.AttackHits ?? 0;
}

internal sealed record SimulationMove(string Id, string Name);

internal sealed record SimulationMoveSequence(SimulationMove[] Opening, SimulationMove[] Loop)
{
    private static readonly SimulationMove[] KnowledgeLoop =
    [
        new("SLAP_MOVE", "抽打"), new("KNOWLEDGE_OVERWHELMING_MOVE", "知识过载"), new("PONDER_MOVE", "思考")
    ];
    private static readonly SimulationMove[] KnowledgeCurseCycle =
        [new("CURSE_OF_KNOWLEDGE_MOVE", "知识的诅咒"), .. KnowledgeLoop];

    internal static SimulationMoveSequence? For(string id, MonsterPhase? phase = null) => id switch
    {
        "CEREMONIAL_BEAST" when phase?.Moves.Contains("STOMP_MOVE") == true =>
            new([new("STUN_MOVE", "击晕")], [new("BEAST_CRY_MOVE", "野兽咆哮"),
                new("STOMP_MOVE", "踩踏"), new("CRUSH_MOVE", "碾碎")]),
        "CEREMONIAL_BEAST" => new([new("STAMP_MOVE", "跺地")], [new("PLOW_MOVE", "横冲直撞")]),
        "PHROG_PARASITE" => new([], [new("INFECT_MOVE", "感染"), new("LASH_MOVE", "甩动")]),
        "TOUGH_EGG" => new([new("HATCH_MOVE", "孵化")], [new("NIBBLE_MOVE", "啃咬")]),
        "VANTOM" => new([], [new("INK_BLOT_MOVE", "墨迹"), new("INKY_LANCE_MOVE", "墨水长枪"),
            new("DISMEMBER_MOVE", "肢解"), new("PREPARE_MOVE", "准备")]),
        "AEONGLASS" => new([], [new("EBB_MOVE", "消退"), new("EYE_LASERS_MOVE", "眼部激光"),
            new("INCREASING_INTENSITY_MOVE", "加大力度")]),
        "KNOWLEDGE_DEMON" => new([.. KnowledgeCurseCycle, .. KnowledgeCurseCycle, .. KnowledgeCurseCycle], KnowledgeLoop),
        _ => null
    };

    internal SimulationMove At(int index) => index < Opening.Length
        ? Opening[index] : Loop[(index - Opening.Length) % Loop.Length];
}

internal static class ActionSimulation
{
    internal static IReadOnlyList<SimulatedTurn> Trace(RuleConfiguration rules, string monsterId, MonsterProfile profile,
        IReadOnlyList<OpeningPick> opening, int seed, int rounds,
        bool phaseAllowsRandomization = true, int firstEnemyAction = 1,
        int rooms = 0, int enemyCount = 1, MonsterPhase? phase = null)
    {
        var monster = rules.Monsters[monsterId];
        var sequence = SimulationMoveSequence.For(monsterId, phase);
        var planner = new ActionPlanner(rules.PoolSettings);
        var catalog = new ActionCatalog(rules.Actions);
        var actionRandom = new Random(seed ^ 0x649AAB81);
        var imbalanceRandom = new Random(seed ^ 0x4B82C097);
        var balances = planner.Start(profile, opening.Sum(pick => pick.Value));
        var activeEffects = new Dictionary<EffectKind, int>();
        var thresholds = new Dictionary<EffectKind, int>();
        var generatedCards = new Dictionary<EffectKind, EffectKind>();
        var usedEffects = new HashSet<EffectKind>();
        int vigor = 0;
        foreach (var pick in opening)
            if (Enum.TryParse<EffectKind>(pick.State.Id.Replace("_", ""), ignoreCase: true, out var kind) &&
                ActionEffect.TargetFor(kind) == EffectTarget.Self)
                RecordEffect(kind, pick.State.Amount, 0, pick.State.GeneratedCard);
        var prior = rules.TurnPrior with { Rooms = rooms,
            ExpectedDamageAgainstMonster = rules.PlayerAction.At(rooms) / Math.Max(1, enemyCount) };
        var history = new ActionHistory();
        bool previousAttack = false;
        bool specialDebuffSpent = false;
        bool imbalanced = opening.Any(pick => pick.State.Id == "IMBALANCED");
        int completedMoves = 0;
        var turns = new List<SimulatedTurn>(rounds);
        for (int turn = 1; turn <= rounds; turn++)
        {
            int strength = prior.Strength + history.AddedStrength + history.RitualStrength + opening.Sum(pick =>
                pick.State.Id switch
                {
                    "STRENGTH" or "STRENGTH_DOWN" => pick.State.Amount,
                    "RITUAL" => (turn - 1) * pick.State.Amount,
                    _ => 0
                });
            bool skipFromImbalance = imbalanced && previousAttack && imbalanceRandom.NextDouble() < monster.FullBlockChance;
            var context = prior with
            {
                Strength = strength + vigor,
                Weakened = prior.Weakened || opening.Any(pick => pick.State.Id == "WEAK" && turn <= pick.State.Amount),
                SpecialDebuffAvailable = !specialDebuffSpent,
                Thresholds = thresholds,
                GeneratedCards = generatedCards,
                ActiveEffects = activeEffects.Where(entry => entry.Value >= turn).Select(entry => entry.Key)
                    .Concat(usedEffects.Where(kind => rules.UniqueAction(kind) && !thresholds.ContainsKey(kind))).ToHashSet(),
                AllowHardCardLimit = prior.AllowHardCardLimit && turn >= rules.HardCardLimitFirstTurn
            };
            bool hasMove = turn >= firstEnemyAction && !skipFromImbalance && !opening.Any(pick => pick.State.Id == "STUN" &&
                    turn < firstEnemyAction + pick.State.Amount);
            var originalMove = hasMove ? sequence?.At(completedMoves++) : null;
            bool canRandomize = phaseAllowsRandomization && hasMove && !(monster.FixedOpening && turn == 1) &&
                (originalMove == null || monster.AllowsMove(originalMove.Id));
            var plan = planner.Plan(balances, profile, catalog.Generate(profile, context),
                actionRandom, canRandomize, (hits, target) => catalog.BuildAttack(profile, context, hits, target));
            balances = plan.After;
            turns.Add(new SimulatedTurn(turn, plan, skipFromImbalance, originalMove));

            previousAttack = plan.Action?.IsAttack == true;
            if (previousAttack)
            {
                vigor = 0;
                activeEffects.Remove(EffectKind.Vigor);
            }
            if (plan.Action is { } action)
            {
                history = history.Record(action);
                foreach (var effect in action.Effects!)
                    RecordEffect(effect.Kind, effect.Amount, turn, effect.GeneratedCard);
                specialDebuffSpent |= action.Effects!.Any(effect => effect.IsSpecialDebuff);
            }
            else
                history = history with { RitualStrength = history.RitualStrength + history.AddedRitual };
        }
        return turns;

        void RecordEffect(EffectKind kind, int amount, int round, EffectKind? card)
        {
            usedEffects.Add(kind);
            if (ActionEffect.UsesThreshold(kind))
                thresholds[kind] = amount;
            if (card.HasValue)
                generatedCards[kind] = card.Value;
            if (kind == EffectKind.Vigor)
                vigor += amount;
            if (rules.Actions.PolicyFor(kind).Stackable)
                return;
            if (kind is EffectKind.Block or EffectKind.Heal or EffectKind.Attack)
                return;
            activeEffects[kind] = kind switch
            {
                EffectKind.Regen or EffectKind.Plating or EffectKind.Intangible or EffectKind.Weak or
                    EffectKind.Vulnerable or EffectKind.Frail => round + amount,
                EffectKind.Tangled or EffectKind.Ringing or EffectKind.TemporaryThorns => round + 1,
                _ => int.MaxValue
            };
        }
    }
}
