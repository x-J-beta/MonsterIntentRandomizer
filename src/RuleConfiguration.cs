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
        PlayerTurns = actions.TryGetProperty("playerTurnValue", out var playerTurns)
            ? Read<PlayerTurnValues>(playerTurns) : new PlayerTurnValues();
        if (PlayerTurns.Act1 <= 0 || PlayerTurns.Act2 <= 0 || PlayerTurns.Act3 <= 0)
            throw new ArgumentException("各幕的玩家回合价值须为正数。");
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
        Actions = new ActionValuationSettings(
            new ActionValuation(Read<Dictionary<EffectKind, Dictionary<string, double>>>(actions.GetProperty("valuation")), PlayerTurns),
            actions.GetProperty("weakDamageReduction").GetDouble(),
            actions.GetProperty("minimumBaseDamagePerHit").GetInt32(),
            maximums.ToDictionary(entry => entry.Key, entry => Enumerable.Range(1, entry.Value).ToArray()),
            Read<StatusPlacementValues>(actions.GetProperty("cardPlacementValues")),
            actions.GetProperty("maxHardCardLimits").GetInt32(),
            actions.GetProperty("handBurnDamageChance").GetDouble(),
            actions.TryGetProperty("minActs", out var minActs) ? Read<Dictionary<EffectKind, int>>(minActs) : null,
            actions.TryGetProperty("specialDebuffChance", out var chance) ? chance.GetDouble() : 0.20,
            actions.TryGetProperty("weights", out var effectWeights) ? Read<Dictionary<EffectKind, RoomWeight>>(effectWeights) : null,
            minimums, hpCaps);
        Draws = new OpeningDraws(weights.GetProperty("none").GetDouble(), weights.GetProperty("positive").GetDouble(),
            weights.GetProperty("negative").GetDouble(), opening.GetProperty("attempts").GetInt32(),
            opening.GetProperty("maxPerMonster").GetInt32(), opening.GetProperty("maxEncounterValueMultiplier").GetDouble());
        OpeningValues = Read<Dictionary<string, StatusValue>>(opening.GetProperty("states"));
        HardCardLimitFirstTurn = actions.GetProperty("hardCardLimitFirstTurn").GetInt32();
        TurnPrior = Read<ActionContext>(settings.GetProperty("simulation").GetProperty("turnContext"));
        var defaults = profiles.GetProperty("defaults");
        KeepVanilla = profiles.TryGetProperty("keepVanilla", out var keep) ? Read<string[]>(keep).ToHashSet() : [];
        Monsters = profiles.GetProperty("monsters").EnumerateObject().ToDictionary(field => field.Name,
            field => ReadMonster(field.Value, defaults));
    }

    public PoolSettings PoolSettings { get; }
    public PlayerActionCurve PlayerAction { get; }
    public PlayerTurnValues PlayerTurns { get; }
    public ActionValuationSettings Actions { get; }
    public OpeningDraws Draws { get; }
    public Dictionary<string, StatusValue> OpeningValues { get; }
    public Dictionary<string, MonsterRules> Monsters { get; }
    public HashSet<string> KeepVanilla { get; }
    public ActionContext TurnPrior { get; }
    public int HardCardLimitFirstTurn { get; }
    internal IEnumerable<string> OpeningStatesFor(MonsterRules monster) => monster.OpeningStates.Length == 0 ? [] :
        monster.OpeningStates.Concat(OpeningValues.Where(state => state.Value.Common).Select(state => state.Key)).Distinct();
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
            entry.TryGetProperty("attackSpend", out var spend) ? spend.GetDouble() : null);
        return new MonsterRules(profile,
            entry.TryGetProperty("deadlyEnemies", out var higher) ? higher.GetDouble() : null,
            !entry.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
            entry.TryGetProperty("fixedOpening", out var fixedOpening) && fixedOpening.GetBoolean(),
            Read<string[]>(Setting("openingStates")),
            entry.TryGetProperty("keepMoves", out var moves) ? Read<string[]>(moves).ToHashSet() : [],
            entry.TryGetProperty("randomizeWhenPower", out var required) ? required.GetString() : null,
            entry.TryGetProperty("fullBlockChance", out var chance) ? chance.GetDouble() : 0);
    }

    public static T Read<T>(JsonElement json) => json.Deserialize<T>(JsonOptions)!;
}

internal sealed record MonsterRules(MonsterProfile Profile, double? DeadlyEnemies, bool Enabled,
    bool FixedOpening, string[] OpeningStates, HashSet<string> KeepMoves, string? RandomizeWhenPower, double FullBlockChance)
{
    public MonsterProfile AtAscension(bool deadly) =>
        deadly && DeadlyEnemies is double higher ? Profile with { ActionValue = higher } : Profile;
}
