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
        var turnValues = actions.TryGetProperty("playerTurnValue", out var playerTurns)
            ? Read<PlayerTurnValues>(playerTurns) : new PlayerTurnValues();
        if (turnValues.Act1 <= 0 || turnValues.Act2 <= 0 || turnValues.Act3 <= 0)
            throw new ArgumentException("各幕的玩家回合价值须为正数。");
        PoolSettings = Read<PoolSettings>(settings.GetProperty("pools"));
        if (PoolSettings.ActionValueMultiplier <= 0 || PoolSettings.ChaosMultiplier < 0)
            throw new ArgumentException("全局行动力倍率须为正数，混沌程度倍率须为非负数。");
        Actions = new ActionValuationSettings(
            new ActionValuation(Read<Dictionary<EffectKind, Dictionary<string, double>>>(actions.GetProperty("valuation")), turnValues),
            actions.GetProperty("weakDamageReduction").GetDouble(),
            actions.GetProperty("maxAddedStrength").GetInt32(),
            actions.GetProperty("maxInsertedCards").GetInt32(),
            actions.GetProperty("playerDebuffCooldownTurns").GetInt32(),
            actions.GetProperty("pureBlockCooldownTurns").GetInt32(),
            actions.GetProperty("minimumBaseDamagePerHit").GetInt32(),
            Read<Dictionary<EffectKind, int[]>>(actions.GetProperty("amounts")),
            actions.GetProperty("maxAddedRitual").GetInt32(),
            Read<StatusPlacementValues>(actions.GetProperty("cardPlacementValues")),
            actions.GetProperty("maxHardCardLimits").GetInt32(),
            actions.GetProperty("handBurnDamageChance").GetDouble(),
            actions.TryGetProperty("minActs", out var minActs) ? Read<Dictionary<EffectKind, int>>(minActs) : null,
            actions.TryGetProperty("specialDebuffChance", out var chance) ? chance.GetDouble() : 0.20);
        Draws = new OpeningDraws(weights.GetProperty("none").GetDouble(), weights.GetProperty("positive").GetDouble(),
            weights.GetProperty("negative").GetDouble(), opening.GetProperty("attempts").GetInt32(),
            opening.GetProperty("maxPerMonster").GetInt32(), opening.GetProperty("maxEncounterValueMultiplier").GetDouble());
        OpeningValues = Read<Dictionary<string, StatusValue>>(opening.GetProperty("states"));
        HardCardLimitFirstTurn = actions.GetProperty("hardCardLimitFirstTurn").GetInt32();
        Forecast = settings.GetProperty("forecast").Clone();
        TurnPrior = Read<ActionContext>(settings.GetProperty("simulation").GetProperty("turnContext"));
        Defaults = profiles.GetProperty("defaults").Clone();
        KeepVanilla = profiles.TryGetProperty("keepVanilla", out var keep) ? Read<string[]>(keep).ToHashSet() : [];
        Monsters = profiles.GetProperty("monsters").EnumerateObject().ToDictionary(field => field.Name,
            field => ReadMonster(field.Value));
    }

    public PoolSettings PoolSettings { get; }
    public ActionValuationSettings Actions { get; }
    public OpeningDraws Draws { get; }
    public Dictionary<string, StatusValue> OpeningValues { get; }
    public Dictionary<string, MonsterRules> Monsters { get; }
    public HashSet<string> KeepVanilla { get; }
    public JsonElement Forecast { get; }
    public ActionContext TurnPrior { get; }
    public int HardCardLimitFirstTurn { get; }
    private JsonElement Defaults { get; }

    private MonsterRules ReadMonster(JsonElement entry)
    {
        JsonElement Setting(string name) => entry.TryGetProperty(name, out var value) ? value : Defaults.GetProperty(name);
        var profile = new MonsterProfile(
            entry.GetProperty("actionValue").GetDouble(), Setting("volatility").GetDouble(),
            entry.GetProperty("attackFrequency").GetDouble(), Setting("nonAttackMultiplier").GetDouble(),
            Setting("survivalTurns").GetDouble(),
            Read<Dictionary<IntentStyle, double>>(entry.GetProperty("preferences")),
            Read<Dictionary<int, double>>(Setting("hitWeights")),
            entry.TryGetProperty("effects", out var effects) ? Read<Dictionary<EffectKind, double>>(effects) : null,
            entry.TryGetProperty("cards", out var cards) ? Read<StatusPlacement[]>(cards) : null);
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
