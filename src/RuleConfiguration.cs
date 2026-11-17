using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonsterIntentRandomizer;

internal sealed class RuleConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RuleConfiguration(JsonElement settings, JsonElement profiles, MonsterMovePresets movePresets)
    {
        var actions = settings.GetProperty("actions");
        var opening = settings.GetProperty("opening");
        var weights = opening.GetProperty("weights");
        var buffs = Read<Dictionary<EffectKind, StatusValue>>(settings.GetProperty("buffs"));
        if (buffs.Any(entry => !ActionEffect.IsMonsterBuff(entry.Key) || entry.Value.Amount != 1))
            throw new ArgumentException("共用强化须为怪物自身的正面状态，并配置估值公式。");
        foreach (var (kind, buff) in buffs)
            if (buff.GeneratedCards is { } generated &&
                (!TriggeredCardBuffs.Types.TryGetValue(kind, out var source) || generated.Any(entry =>
                    !TriggeredCardBuffs.CanGenerate(entry.Key, source.Pile) || entry.Value < 0)))
                throw new ArgumentException($"{kind} 的生成牌须为可加入指定牌堆的状态牌，权重须为非负数。");
        PlayerAction = settings.TryGetProperty("playerAction", out var curve) ? Read<PlayerActionCurve>(curve) : new();
        if (PlayerAction.Base <= 0 || PlayerAction.RoomsPerStep <= 0 || PlayerAction.Exponent <= 0)
            throw new ArgumentException("玩家行动力的基础值、房间除数和指数须为正数。");
        PoolSettings = Read<PoolSettings>(settings.GetProperty("pools"));
        if (PoolSettings.ActionValueMultiplier <= 0 || PoolSettings.Chaos is < 0 or > 1 || PoolSettings.HighAscensionActionValueMultiplier < 1)
            throw new ArgumentException("全局行动力倍率须为正数，混沌值须在 0 到 1 之间，高进阶行动力倍率须至少为 1。");
        if (PoolSettings.FavoriteDirectBaseShare is < 0 or > 1 || PoolSettings.FavoriteDirectStrengthMultiplier < 0)
            throw new ArgumentException("偏好直占基础值须在 0 到 1 之间，强度系数须为非负数。");
        if (PoolSettings.NonAttackCandidates < 1 ||
            PoolSettings.FullChaosBudgetAt <= 0 || PoolSettings.OpeningStrengthAtMax < 1 ||
            PoolSettings.ChaosBudgetWeights is { } chaosWeights &&
            (chaosWeights.Any(entry => !Enum.IsDefined(entry.Key) || entry.Value < 0) || chaosWeights.Values.Sum() <= 0))
            throw new ArgumentException("抽取预算、候选数量或混沌分配参数无效。");
        var maximumCosts = actions.TryGetProperty("maxCostMultipliers", out var limits)
            ? Read<Dictionary<EffectKind, double>>(limits) : [];
        var minimums = actions.TryGetProperty("minAmounts", out var minAmounts)
            ? Read<Dictionary<EffectKind, int>>(minAmounts) : null;
        if (maximumCosts.Values.Any(cost => cost < 0) || minimums?.Values.Any(min => min < 1) == true)
            throw new ArgumentException("层数下限须为正数，最大花费系数须为非负数。");
        var policies = actions.TryGetProperty("policies", out var policyConfig)
            ? Read<Dictionary<EffectKind, EffectPolicy>>(policyConfig) : null;
        if (policies?.Any(entry => entry.Value.Target is EffectTarget target && ActionEffect.TargetFor(entry.Key) != target) == true)
            throw new ArgumentException("出招目标由原版效果决定。");
        var valuation = Read<Dictionary<EffectKind, Dictionary<string, double>>>(actions.GetProperty("valuation"));
        if (actions.TryGetProperty("statusCardValues", out var cardValues))
        {
            foreach (var (card, value) in Read<Dictionary<EffectKind, double>>(cardValues))
            {
                if (!ActionEffect.IsCard(card) || value < 0)
                    throw new ArgumentException($"{card} 的单张状态牌估值无效。");
                valuation[card] = new Dictionary<string, double> { ["amount"] = value };
            }
        }
        var cardRatios = actions.TryGetProperty("statusCardPlayerActionValueRatios", out var ratioConfig)
            ? Read<Dictionary<EffectKind, double>>(ratioConfig) : null;
        if (cardRatios?.Any(entry => !ActionEffect.IsCard(entry.Key) || entry.Value < 0) == true)
            throw new ArgumentException("状态牌的玩家行动力系数须为非负数。");
        var laterMonsterPercent = actions.TryGetProperty("laterMonsterPercent", out var laterMonsterConfig)
            ? Read<Dictionary<EffectKind, double>>(laterMonsterConfig) : null;
        if (laterMonsterPercent?.Values.Any(percent => percent < -100) == true)
            throw new ArgumentException("每个后续怪物的估值变化须至少为 -100%。");
        Actions = new ActionValuationSettings(
            new ActionValuation(valuation),
            actions.GetProperty("weakDamageReduction").GetDouble(),
            actions.GetProperty("minimumBaseDamagePerHit").GetInt32(),
            Read<StatusPlacementValues>(actions.GetProperty("cardPlacementValues")),
            actions.GetProperty("maxHardCardLimits").GetInt32(),
            actions.TryGetProperty("weights", out var effectWeights) ? Read<Dictionary<EffectKind, RoomWeight>>(effectWeights) : null,
            minimums, policies, buffs, PoolSettings.Chaos, cardRatios, PlayerAction.At(0), laterMonsterPercent, maximumCosts);
        Draws = new OpeningDraws(weights.GetProperty("none").GetDouble(), weights.GetProperty("positive").GetDouble(),
            weights.GetProperty("negative").GetDouble(), opening.GetProperty("attempts").GetInt32(),
            opening.GetProperty("maxPerMonster").GetInt32(), opening.GetProperty("maxEncounterValueMultiplier").GetDouble(),
            PoolSettings.OpeningStrength, PoolSettings.SpecialStatusLimit);
        OpeningValues = Read<Dictionary<string, StatusValue>>(opening.GetProperty("states"));
        foreach (var (kind, buff) in buffs)
            OpeningValues[EffectPolicy.Id(kind)] = buff;
        if (OpeningValues.Values.Any(state => state.LaterMonsterPercent < -100 || state.MaxCostMultiplier < 0))
            throw new ArgumentException("状态最大花费系数须为非负数，后续怪物的估值变化须至少为 -100%。");
        HardCardLimitFirstTurn = actions.GetProperty("hardCardLimitFirstTurn").GetInt32();
        TurnPrior = Read<ActionContext>(settings.GetProperty("simulation").GetProperty("turnContext"));
        var defaults = profiles.GetProperty("defaults");
        MovePresets = movePresets;
        Monsters = profiles.GetProperty("monsters").EnumerateObject().ToDictionary(field => field.Name,
            field => ReadMonster(field.Value, defaults));
        Planner = new ActionPlanner(PoolSettings);
        Catalog = new ActionCatalog(Actions);
    }

    public PoolSettings PoolSettings { get; }
    public PlayerActionCurve PlayerAction { get; }
    public ActionValuationSettings Actions { get; }
    public OpeningDraws Draws { get; }
    public Dictionary<string, StatusValue> OpeningValues { get; }
    public Dictionary<string, MonsterRules> Monsters { get; }
    internal MonsterMovePresets MovePresets { get; }
    internal ActionPlanner Planner { get; }
    private ActionCatalog Catalog { get; }
    public ActionContext TurnPrior { get; }
    public int HardCardLimitFirstTurn { get; }
    internal OpeningOption OpeningOption(string id, MonsterProfile profile, int rooms, int? threshold = null,
        EffectKind? existingCard = null, string? monsterId = null) =>
        OpeningValuation.Option(id, OpeningValues[id], profile, rooms, Draws.Strength, PoolSettings.Chaos,
            EffectPolicy.IsThreshold(id) ? threshold ?? 0 : null, Actions, existingCard, monsterId);
    internal IEnumerable<string> OpeningStatesFor(string id, MonsterRules monster) => monster.OpeningStates.Length == 0 ? [] :
        monster.OpeningStates.Concat(OpeningValues.Where(state => state.Value.Common).Select(state => state.Key))
            .Distinct().Where(state => !EffectPolicy.TryGetKind(state, out var kind) || EffectPolicy.CanTargetMonster(kind, id));

    internal ActionPlan PlanMove(string id, string? moveId, Pools previous, MonsterProfile profile, ActionContext context,
        Random random, bool canRandomize = true, double sharedAttackBudget = 0)
    {
        if (!canRandomize)
            return new ActionPlan(null, previous, previous);

        bool clawPhase = UsesClawGrowth(id, moveId);
        var candidates = Catalog.Generate(profile, context, id,
            extraBuffHits: clawPhase ? profile.BaseExpectedSurvivalTurns / 2 : 0);
        var plan = Planner.Plan(previous, profile, candidates, random,
            buildAttack: (hits, target) => Catalog.BuildAttack(profile, context, hits, target),
            extraClawDesire: clawPhase ? previous.CompletedClawTurns : 0,
            specialStatusesRemaining: context.SpecialStatusesRemaining, sharedAttackBudget: sharedAttackBudget,
            enemyCount: context.EnemyCount);
        if (plan.Action is { AttackHits: > 1 } attack &&
            !string.IsNullOrWhiteSpace(Monsters.GetValueOrDefault(MovePresets.ProfileId(id))?.AnimationFor(IntentStyle.Attack, attack.AttackHits)))
            plan = plan with { AnimatedAttackHits = CombatMath.DrawAttackAnimationHits(attack.AttackHits, random) };
        return clawPhase ? plan with { After = plan.After with { CompletedClawTurns = previous.CompletedClawTurns + 1 } } : plan;
    }

    internal static bool UsesClawGrowth(string id, string? moveId) =>
        id == "TEST_SUBJECT" && moveId == "MULTI_CLAW_MOVE";

    internal ActionContext TurnContext(int rooms, int enemyCount, int round, double? averageDamage = null) => TurnPrior with
    {
        Rooms = rooms,
        EnemyCount = Math.Max(1, enemyCount),
        ExpectedDamageAgainstMonster = PlayerAction.At(rooms, averageDamage) / Math.Max(1, enemyCount),
        AllowHardCardLimit = round >= HardCardLimitFirstTurn
    };

    internal HashSet<EffectKind> FixedKnightStatuses(IEnumerable<string> enemies)
    {
        var fixedStatuses = new HashSet<EffectKind>();
        foreach (string id in enemies)
        {
            if (id == "MAGI_KNIGHT" && Monsters.GetValueOrDefault(id)?.AllowsMove("DAMPEN_MOVE") != true)
                fixedStatuses.Add(EffectKind.Dampen);
            if (id == "SPECTRAL_KNIGHT" && Monsters.GetValueOrDefault(id)?.AllowsMove("HEX") != true)
                fixedStatuses.Add(EffectKind.Hex);
        }
        return fixedStatuses;
    }

    internal static MonsterRules ReadMonster(JsonElement entry, JsonElement defaults)
    {
        JsonElement Setting(string name) => entry.TryGetProperty(name, out var value) ? value : defaults.GetProperty(name);
        var phases = entry.TryGetProperty("phases", out var phaseDefinitions)
            ? Read<Dictionary<string, MonsterPhase>>(phaseDefinitions) : null;
        if (phases != null)
        {
            var movesInPhases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var phase in phases.Values)
                if (phase.Moves is not { Length: > 0 } || phase.Moves.Any(move => !movesInPhases.Add(move)) ||
                    phase.ActionValue is <= 0 || phase.AttackFrequency is < 0 || phase.NonAttackMultiplier is < 0 ||
                    phase.Hp is <= 0 ||
                    phase.AllowedBuffs?.Any(kind => !ActionEffect.IsMonsterBuff(kind)) == true)
                    throw new ArgumentException("阶段招式须存在且不可跨阶段重复，行动参数须在有效范围内。");
        }
        var profile = new MonsterProfile(
            entry.GetProperty("actionValue").GetDouble(),
            entry.GetProperty("attackFrequency").GetDouble(), Setting("nonAttackMultiplier").GetDouble(),
            0,
            Read<Dictionary<IntentStyle, double>>(entry.GetProperty("preferences")),
            entry.TryGetProperty("cards", out var cards) ? Read<StatusPlacement[]>(cards) : null,
            Favorites: entry.TryGetProperty("favorites", out var favorites) ? Read<Dictionary<EffectKind, double>>(favorites) : null);
        return new MonsterRules(profile,
            !entry.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
            Read<string[]>(Setting("openingStates")),
            entry.TryGetProperty("keepMoves", out var moves) ? Read<string[]>(moves).ToHashSet() : [],
            entry.TryGetProperty("fullBlockChance", out var chance) ? chance.GetDouble() : 0,
            phases, entry.TryGetProperty("animations", out var animations) ? Read<MonsterAnimations>(animations) : null);
    }

    public static T Read<T>(JsonElement json) => json.Deserialize<T>(JsonOptions)!;
}

internal sealed record MonsterRules(MonsterProfile Profile, bool Enabled,
    string[] OpeningStates, HashSet<string> KeepMoves, double FullBlockChance,
    IReadOnlyDictionary<string, MonsterPhase>? Phases = null, MonsterAnimations? Animations = null)
{
    internal bool HasEnabledStage => Phases is not { Count: > 0 } || Phases.Values.Any(phase => phase.Enabled);

    internal bool AllowsMove(string? id) => Enabled && (id == null || !KeepMoves.Contains(id) && PhaseForMove(id)?.Enabled != false);

    internal MonsterPhase? PhaseForMove(string moveId) =>
        Phases?.Values.FirstOrDefault(phase => phase.Moves.Contains(moveId, StringComparer.Ordinal));

    internal string? DefaultPhase => Phases?.Keys.FirstOrDefault();

    internal string? AnimationFor(IntentStyle style, int hits = 1) => Animations?.For(style, hits);
}

internal sealed record MonsterAnimations(string? Attack = null, string? SingleAttack = null, string? MultiAttack = null,
    string? Buff = null, string? Defend = null, string? PlayerDebuff = null)
{
    internal string? For(IntentStyle style, int hits) => style switch
    {
        IntentStyle.Attack => (hits > 1 ? MultiAttack : SingleAttack) ?? Attack,
        IntentStyle.Buff => Buff ?? Defend,
        IntentStyle.Defend => Defend ?? Buff,
        _ => PlayerDebuff
    };
}

internal sealed record MonsterPhase(string Name, string[] Moves, double? ActionValue = null, double? AttackFrequency = null,
    double? NonAttackMultiplier = null, IReadOnlyDictionary<IntentStyle, double>? Preferences = null,
    IReadOnlyDictionary<EffectKind, double>? Favorites = null, IReadOnlyList<EffectKind>? AllowedBuffs = null,
    bool Enabled = true, double? Hp = null)
{
    internal MonsterProfile Apply(MonsterProfile profile) => profile with
    {
        ActionValue = ActionValue ?? profile.ActionValue,
        AttackFrequency = AttackFrequency ?? profile.AttackFrequency,
        NonAttackMultiplier = NonAttackMultiplier ?? profile.NonAttackMultiplier,
        IntentWeights = Preferences ?? profile.IntentWeights,
        Favorites = Favorites ?? profile.Favorites,
        AllowedBuffs = AllowedBuffs ?? profile.AllowedBuffs
    };
}
