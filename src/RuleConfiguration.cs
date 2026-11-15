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
        var buffs = settings.TryGetProperty("buffs", out var buffConfig)
            ? Read<Dictionary<EffectKind, StatusValue>>(buffConfig) : [];
        if (buffs.Any(entry => !ActionEffect.IsMonsterBuff(entry.Key) || entry.Value.Formula == null || entry.Value.Amount != 1))
            throw new ArgumentException("共用强化须为怪物自身的正面状态，并配置估值公式。");
        PlayerAction = settings.TryGetProperty("playerAction", out var curve) ? Read<PlayerActionCurve>(curve) : new();
        if (PlayerAction.Base <= 0 || PlayerAction.RoomsPerStep <= 0 || PlayerAction.Exponent <= 0)
            throw new ArgumentException("玩家行动力的基础值、房间除数和指数须为正数。");
        PoolSettings = Read<PoolSettings>(settings.GetProperty("pools"));
        if (PoolSettings.ActionValueMultiplier <= 0 || PoolSettings.ChaosMultiplier < 0)
            throw new ArgumentException("全局行动力倍率须为正数，混沌程度倍率须为非负数。");
        if (PoolSettings.DrawBudgetMultiplier <= 0 || PoolSettings.LocalOverdraftShare < 0 ||
            PoolSettings.NonAttackCandidates < 1 || PoolSettings.UnusedAttackBudgetShare is < 0 or > 1 ||
            PoolSettings.FullChaosBudgetAt <= 1 ||
            PoolSettings.ChaosBudgetWeights is { } chaosWeights &&
            (chaosWeights.Any(entry => !Enum.IsDefined(entry.Key) || entry.Value < 0) || chaosWeights.Values.Sum() <= 0))
            throw new ArgumentException("抽取预算、候选数量或混沌分配参数无效。");
        var maximums = actions.TryGetProperty("maxAmounts", out var maxAmounts)
            ? Read<Dictionary<EffectKind, int>>(maxAmounts)
            : Read<Dictionary<EffectKind, int[]>>(actions.GetProperty("amounts"))
                .ToDictionary(entry => entry.Key, entry => entry.Value.DefaultIfEmpty(0).Max());
        var minimums = actions.TryGetProperty("minAmounts", out var minAmounts)
            ? Read<Dictionary<EffectKind, int>>(minAmounts) : null;
        var hpCaps = actions.TryGetProperty("hpPerSurvivalTurnCaps", out var caps)
            ? Read<Dictionary<EffectKind, double>>(caps) : null;
        if (maximums.Values.Any(max => max < 0) || (minimums?.Values.Any(min => min < 1) ?? false) ||
            (hpCaps?.Values.Any(cap => cap < 0) ?? false) ||
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
        Actions = new ActionValuationSettings(
            new ActionValuation(valuation),
            actions.GetProperty("weakDamageReduction").GetDouble(),
            actions.GetProperty("minimumBaseDamagePerHit").GetInt32(),
            maximums.ToDictionary(entry => entry.Key, entry => Enumerable.Range(1, entry.Value).ToArray()),
            Read<StatusPlacementValues>(actions.GetProperty("cardPlacementValues")),
            actions.GetProperty("maxHardCardLimits").GetInt32(),
            actions.TryGetProperty("minActs", out var minActs) ? Read<Dictionary<EffectKind, int>>(minActs) : null,
            actions.TryGetProperty("specialDebuffChance", out var chance) ? chance.GetDouble() : 0.20,
            actions.TryGetProperty("weights", out var effectWeights) ? Read<Dictionary<EffectKind, RoomWeight>>(effectWeights) : null,
            minimums, hpCaps, policies, buffs);
        Draws = new OpeningDraws(weights.GetProperty("none").GetDouble(), weights.GetProperty("positive").GetDouble(),
            weights.GetProperty("negative").GetDouble(), opening.GetProperty("attempts").GetInt32(),
            opening.GetProperty("maxPerMonster").GetInt32(), opening.GetProperty("maxEncounterValueMultiplier").GetDouble());
        OpeningValues = Read<Dictionary<string, StatusValue>>(opening.GetProperty("states"));
        foreach (var (kind, buff) in buffs)
            OpeningValues[EffectPolicy.Id(kind)] = buff;
        foreach (var kind in Enum.GetValues<EffectKind>().Where(kind => ActionEffect.TargetFor(kind) == EffectTarget.Self &&
                     Actions.PolicyFor(kind).Unique))
        {
            string id = EffectPolicy.Id(kind);
            if (OpeningValues.TryGetValue(id, out var state))
                OpeningValues[id] = state with { Unique = true };
        }
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
    internal IEnumerable<string> OpeningStatesFor(MonsterRules monster) => monster.OpeningStates.Length == 0 ? [] :
        monster.OpeningStates.Concat(OpeningValues.Where(state => state.Value.Common).Select(state => state.Key))
            .Concat((Actions.Buffs?.Keys ?? []).Where(kind => monster.Profile.EffectWeights?.GetValueOrDefault(kind) > 0)
                .Select(EffectPolicy.Id))
            .Distinct().Where(id => id != "STUN" || !monster.FixedOpening);

    internal bool UniqueAction(EffectKind kind) => Actions.PolicyFor(kind).Unique ||
        (ActionEffect.TargetFor(kind) == EffectTarget.Self &&
            OpeningValues.TryGetValue(EffectPolicy.Id(kind), out var opening) && opening.Unique);
    internal static MonsterRules ReadMonster(JsonElement entry, JsonElement defaults)
    {
        JsonElement Setting(string name) => entry.TryGetProperty(name, out var value) ? value : defaults.GetProperty(name);
        var profile = new MonsterProfile(
            entry.GetProperty("actionValue").GetDouble(), Setting("volatility").GetDouble(),
            entry.GetProperty("attackFrequency").GetDouble(), Setting("nonAttackMultiplier").GetDouble(),
            0,
            Read<Dictionary<IntentStyle, double>>(entry.GetProperty("preferences")),
            Read<Dictionary<int, double>>(Setting("hitWeights")),
            entry.TryGetProperty("effects", out var effects) ? Read<Dictionary<EffectKind, double>>(effects) : null,
            entry.TryGetProperty("cards", out var cards) ? Read<StatusPlacement[]>(cards) : null,
            entry.TryGetProperty("attackSpend", out var spend) ? spend.GetDouble() : null,
            SingleHitPreference: entry.TryGetProperty("singleHitPreference", out var single) ? single.GetDouble() : 1,
            DesireHitPreference: entry.TryGetProperty("desireHitPreference", out var multi) ? multi.GetDouble() : 1);
        return new MonsterRules(profile,
            entry.TryGetProperty("deadlyEnemies", out var higher) ? higher.GetDouble() : null,
            !entry.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
            entry.TryGetProperty("fixedOpening", out var fixedOpening) && fixedOpening.GetBoolean(),
            Read<string[]>(Setting("openingStates")),
            entry.TryGetProperty("keepMoves", out var moves) ? Read<string[]>(moves).ToHashSet() : [],
            entry.TryGetProperty("randomizeWhenPower", out var required) ? required.GetString() : null,
            entry.TryGetProperty("fullBlockChance", out var chance) ? chance.GetDouble() : 0,
            entry.TryGetProperty("randomMoves", out var randomMoves) ? Read<string[]>(randomMoves).ToHashSet(StringComparer.Ordinal) : null);
    }

    public static T Read<T>(JsonElement json) => json.Deserialize<T>(JsonOptions)!;
}

internal sealed record MonsterRules(MonsterProfile Profile, double? DeadlyEnemies, bool Enabled,
    bool FixedOpening, string[] OpeningStates, HashSet<string> KeepMoves, string? RandomizeWhenPower, double FullBlockChance,
    HashSet<string>? RandomMoves = null)
{
    public MonsterProfile AtAscension(bool deadly) =>
        deadly && DeadlyEnemies is double higher ? Profile with { ActionValue = higher } : Profile;
}
