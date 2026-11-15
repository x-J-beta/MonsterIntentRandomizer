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

    public RuleConfiguration(JsonElement settings, JsonElement profiles)
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
                    !ActionEffect.IsCard(entry.Key) || !ActionCatalog.CanPlace(entry.Key, source.Pile) || entry.Value < 0)))
                throw new ArgumentException($"{kind} 的生成牌须为可加入指定牌堆的状态牌，权重须为非负数。");
        PlayerAction = settings.TryGetProperty("playerAction", out var curve) ? Read<PlayerActionCurve>(curve) : new();
        if (PlayerAction.Base <= 0 || PlayerAction.RoomsPerStep <= 0 || PlayerAction.Exponent <= 0)
            throw new ArgumentException("玩家行动力的基础值、房间除数和指数须为正数。");
        PoolSettings = Read<PoolSettings>(settings.GetProperty("pools"));
        if (PoolSettings.ActionValueMultiplier <= 0 || PoolSettings.ChaosMultiplier < 0 || PoolSettings.HighAscensionActionValueMultiplier < 1)
            throw new ArgumentException("全局行动力倍率须为正数，混沌倍率须为非负数，高进阶行动力倍率须至少为 1。");
        if (PoolSettings.FavoriteDirectBaseShare is < 0 or > 1 || PoolSettings.FavoriteDirectStrengthMultiplier < 0)
            throw new ArgumentException("偏好直占基础值须在 0 到 1 之间，强度系数须为非负数。");
        if (PoolSettings.DrawBudgetMultiplier <= 0 || PoolSettings.LocalOverdraftShare < 0 ||
            PoolSettings.NonAttackCandidates < 1 || PoolSettings.UnusedAttackBudgetShare is < 0 or > 1 ||
            PoolSettings.FullChaosBudgetAt <= 1 || PoolSettings.OpeningStrengthAtMax < 1 ||
            PoolSettings.ChaosBudgetWeights is { } chaosWeights &&
            (chaosWeights.Any(entry => !Enum.IsDefined(entry.Key) || entry.Value < 0) || chaosWeights.Values.Sum() <= 0))
            throw new ArgumentException("抽取预算、候选数量或混沌分配参数无效。");
        var maximums = Read<Dictionary<EffectKind, int>>(actions.GetProperty("maxAmounts"));
        var minimums = actions.TryGetProperty("minAmounts", out var minAmounts)
            ? Read<Dictionary<EffectKind, int>>(minAmounts) : null;
        if (maximums.Values.Any(max => max < 0) || (minimums?.Values.Any(min => min < 1) ?? false) ||
            (minimums?.Any(entry => maximums.TryGetValue(entry.Key, out int max) && max > 0 && max < entry.Value) ?? false))
            throw new ArgumentException("出招状态的层数下限或上限无效。");
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
        Actions = new ActionValuationSettings(
            new ActionValuation(valuation),
            actions.GetProperty("weakDamageReduction").GetDouble(),
            actions.GetProperty("minimumBaseDamagePerHit").GetInt32(),
            maximums.ToDictionary(entry => entry.Key, entry => Enumerable.Range(1, entry.Value).ToArray()),
            Read<StatusPlacementValues>(actions.GetProperty("cardPlacementValues")),
            actions.GetProperty("maxHardCardLimits").GetInt32(),
            actions.TryGetProperty("weights", out var effectWeights) ? Read<Dictionary<EffectKind, RoomWeight>>(effectWeights) : null,
            minimums, policies, buffs, PoolSettings.ChaosProgress, cardRatios, PlayerAction.At(0));
        Draws = new OpeningDraws(weights.GetProperty("none").GetDouble(), weights.GetProperty("positive").GetDouble(),
            weights.GetProperty("negative").GetDouble(), opening.GetProperty("attempts").GetInt32(),
            opening.GetProperty("maxPerMonster").GetInt32(), opening.GetProperty("maxEncounterValueMultiplier").GetDouble(),
            PoolSettings.OpeningStrength);
        OpeningValues = Read<Dictionary<string, StatusValue>>(opening.GetProperty("states"));
        foreach (var (kind, buff) in buffs)
            OpeningValues[EffectPolicy.Id(kind)] = buff;
        HardCardLimitFirstTurn = actions.GetProperty("hardCardLimitFirstTurn").GetInt32();
        TurnPrior = Read<ActionContext>(settings.GetProperty("simulation").GetProperty("turnContext"));
        var defaults = profiles.GetProperty("defaults");
        KeepVanilla = profiles.TryGetProperty("keepVanilla", out var keep) ? Read<string[]>(keep).ToHashSet() : [];
        Monsters = profiles.GetProperty("monsters").EnumerateObject().ToDictionary(field => field.Name,
            field => ReadMonster(field.Value, defaults));
    }

    public PoolSettings PoolSettings { get; }
    public PlayerActionCurve PlayerAction { get; }
    public ActionValuationSettings Actions { get; }
    public OpeningDraws Draws { get; }
    public Dictionary<string, StatusValue> OpeningValues { get; }
    public Dictionary<string, MonsterRules> Monsters { get; }
    public HashSet<string> KeepVanilla { get; }
    public ActionContext TurnPrior { get; }
    public int HardCardLimitFirstTurn { get; }
    internal OpeningOption OpeningOption(string id, MonsterProfile profile, int rooms, int? threshold = null,
        EffectKind? existingCard = null) =>
        OpeningValuation.Option(id, OpeningValues[id], profile, rooms, Draws.Strength, PoolSettings.ChaosProgress,
            EffectPolicy.IsThreshold(id) ? threshold ?? 0 : null, Actions, existingCard);
    internal IEnumerable<string> OpeningStatesFor(MonsterRules monster) => monster.OpeningStates.Length == 0 ? [] :
        monster.OpeningStates.Concat(OpeningValues.Where(state => state.Value.Common).Select(state => state.Key))
            .Concat((Actions.Buffs?.Keys ?? []).Where(kind => monster.Profile.EffectWeights?.GetValueOrDefault(kind) > 0)
                .Select(EffectPolicy.Id))
            .Distinct().Where(id => id != "STUN" || !monster.FixedOpening);

    internal bool UniqueAction(EffectKind kind) => Actions.PolicyFor(kind).Unique;
    internal static MonsterRules ReadMonster(JsonElement entry, JsonElement defaults)
    {
        JsonElement Setting(string name) => entry.TryGetProperty(name, out var value) ? value : defaults.GetProperty(name);
        var profile = new MonsterProfile(
            entry.GetProperty("actionValue").GetDouble(), Setting("volatility").GetDouble(),
            entry.GetProperty("attackFrequency").GetDouble(), Setting("nonAttackMultiplier").GetDouble(),
            0,
            Read<Dictionary<IntentStyle, double>>(entry.GetProperty("preferences")),
            entry.TryGetProperty("effects", out var effects) ? Read<Dictionary<EffectKind, double>>(effects) : null,
            entry.TryGetProperty("cards", out var cards) ? Read<StatusPlacement[]>(cards) : null,
            SingleHitPreference: entry.TryGetProperty("singleHitPreference", out var single) ? single.GetDouble() : 1,
            DesireHitPreference: entry.TryGetProperty("desireHitPreference", out var multi) ? multi.GetDouble() : 1,
            Favorites: entry.TryGetProperty("favorites", out var favorites) ? Read<Dictionary<EffectKind, double>>(favorites) : null);
        return new MonsterRules(profile,
            !entry.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
            entry.TryGetProperty("fixedOpening", out var fixedOpening) && fixedOpening.GetBoolean(),
            Read<string[]>(Setting("openingStates")),
            entry.TryGetProperty("keepMoves", out var moves) ? Read<string[]>(moves).ToHashSet() : [],
            entry.TryGetProperty("randomizeWhenPower", out var required) ? required.GetString() : null,
            entry.TryGetProperty("fullBlockChance", out var chance) ? chance.GetDouble() : 0,
            entry.TryGetProperty("randomMoves", out var randomMoves) ? Read<string[]>(randomMoves).ToHashSet(StringComparer.Ordinal) : null,
            entry.TryGetProperty("keepVanillaWhileAllyAlive", out var ally) ? ally.GetString() : null,
            entry.TryGetProperty("randomizeAllMoves", out var all) && all.GetBoolean());
    }

    public static T Read<T>(JsonElement json) => json.Deserialize<T>(JsonOptions)!;
}

internal sealed record MonsterRules(MonsterProfile Profile, bool Enabled,
    bool FixedOpening, string[] OpeningStates, HashSet<string> KeepMoves, string? RandomizeWhenPower, double FullBlockChance,
    HashSet<string>? RandomMoves = null, string? KeepVanillaWhileAllyAlive = null, bool RandomizeAllMoves = false)
{
    internal bool AllowsMove(string id) => !KeepMoves.Contains(id) && (RandomMoves?.Contains(id) ?? true);
}
