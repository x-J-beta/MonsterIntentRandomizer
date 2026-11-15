using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace MonsterIntentRandomizer;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public static void Initialize()
    {
        ModSettings.Load();
        new Harmony("MonsterIntentRandomizer").PatchAll(typeof(ModEntry).Assembly);
        Log.Info("[MonsterIntentRandomizer] Loaded.");
    }
}

internal sealed record StoredSettings(bool Enabled, JsonElement Settings, JsonElement Profiles, int Revision = 0,
    bool ShowIntentDetails = true);

internal static class ModSettings
{
    private static string SavePath => ProjectSettings.GlobalizePath("user://mod_data/MonsterIntentRandomizer/config.json");
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private const int SettingsRevision = 21;
    internal static StoredSettings Defaults { get; } = new(true, ReadResource("settings.json"), ReadResource("monster-profiles.json"), SettingsRevision);
    internal static StoredSettings Current { get; private set; } = Defaults;
    internal static RuleConfiguration Rules { get; private set; } = new(Defaults.Settings, Defaults.Profiles);

    internal static void Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                var stored = Upgrade(JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SavePath))!);
                var rules = Parse(stored);
                Current = stored;
                Rules = rules;
            }
        }
        catch (Exception error)
        {
            Log.Warn($"[MonsterIntentRandomizer] Using default settings: {error.Message}");
        }
    }

    internal static void Save(StoredSettings edited)
    {
        edited = edited with { Revision = SettingsRevision };
        var rules = Parse(edited);
        string path = SavePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(edited, Pretty));
        File.Move(path + ".tmp", path, overwrite: true);
        Current = edited;
        Rules = rules;
    }

    internal static string Format(JsonElement json) => JsonSerializer.Serialize(json, Pretty);

    private static StoredSettings Upgrade(StoredSettings stored)
    {
        if (stored.Revision >= SettingsRevision)
            return stored;

        var settings = JsonNode.Parse(stored.Settings.GetRawText())!;
        var actions = settings["actions"]!.AsObject();
        var defaultSettings = JsonNode.Parse(Defaults.Settings.GetRawText())!;
        actions.Remove("specialDebuffChance");
        UpgradeActionWeightsAndCardRatios(actions, defaultSettings["actions"]!);
        if (stored.Revision >= 20)
            return stored with { Settings = JsonSerializer.SerializeToElement(settings), Revision = SettingsRevision };

        var profiles = JsonNode.Parse(stored.Profiles.GetRawText())!;
        if (profiles["keepVanilla"] is JsonArray keepVanilla)
        {
            foreach (var entry in keepVanilla.Where(entry => entry?.GetValue<string>() is
                "KIN_FOLLOWER" or "KIN_PRIEST" or "TRACKER_RUBY_RAIDER").ToArray())
                keepVanilla.Remove(entry);
        }
        if (stored.Revision >= 19)
            return Updated();

        var pools = settings["pools"]!.AsObject();
        pools["favoriteDirectBaseShare"] ??= defaultSettings["pools"]!["favoriteDirectBaseShare"]!.DeepClone();
        pools["favoriteDirectStrengthMultiplier"] ??= defaultSettings["pools"]!["favoriteDirectStrengthMultiplier"]!.DeepClone();
        pools.Remove("highAscensionActionValueBonus");
        pools["highAscensionActionValueMultiplier"] ??= defaultSettings["pools"]!["highAscensionActionValueMultiplier"]!.DeepClone();
        foreach (string section in new[] { "valuation", "maxAmounts", "minAmounts", "weights", "policies" })
            RemoveSteamAction(actions[section]);
        var defaultProfiles = JsonNode.Parse(Defaults.Profiles.GetRawText())!["monsters"]!.AsObject();
        var monsters = profiles["monsters"]!.AsObject();
        (profiles["defaults"] as JsonObject)?.Remove("attackSpend");
        foreach (var (id, profile) in monsters)
        {
            if (profile is not JsonObject entry)
                continue;
            entry.Remove("attackSpend");
            RemoveSteamAction(entry["effects"]);
            RemoveSteamAction(entry["favorites"]);
            if (defaultProfiles[id]?["favorites"] is { } favorites)
                entry["favorites"] ??= favorites.DeepClone();
        }
        var giant = monsters["WATERFALL_GIANT"] as JsonObject;
        if (giant?["preferences"] is JsonObject preferences &&
            preferences["attack"]?.GetValue<double>() == 4 && preferences["buff"]?.GetValue<double>() == 80 &&
            preferences["defend"]?.GetValue<double>() == 8 && preferences["playerDebuff"]?.GetValue<double>() == 8)
            giant["preferences"] = defaultProfiles["WATERFALL_GIANT"]!["preferences"]!.DeepClone();
        if (stored.Revision >= 16)
            return Updated();

        if (settings["buffs"] is not JsonObject buffs)
            throw new ArgumentException("旧配置缺少共用强化池，请先用上一版保存一次。");

        if (stored.Revision <= 12)
            UpgradeBuffPrices(settings, defaultSettings);

        buffs.Remove("CurlUp");
        (settings["opening"]?["states"] as JsonObject)?.Remove("CURL_UP");
        foreach (var section in new[] { "valuation", "maxAmounts", "minAmounts", "weights", "policies" })
            (actions[section] as JsonObject)?.Remove("CurlUp");
        actions.AsObject().Remove("minActs");
        actions.AsObject().Remove("hpPerSurvivalTurnCaps");
        settings["pools"]!["openingStrengthAtMax"] ??=
            Defaults.Settings.GetProperty("pools").GetProperty("openingStrengthAtMax").GetDouble();
        buffs["PainfulStabs"] ??= JsonNode.Parse(Defaults.Settings.GetProperty("buffs").GetProperty("PainfulStabs").GetRawText());

        CleanProfile(profiles["defaults"]);
        foreach (var (_, profile) in monsters)
            CleanProfile(profile);
        var allMoves = defaultProfiles.Where(pair => pair.Value?["randomizeAllMoves"]?.GetValue<bool>() == true)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string id in allMoves)
        {
            if (monsters[id] is not JsonObject profile)
                continue;
            profile["randomizeAllMoves"] = true;
            if (profile["enabled"]?.GetValue<bool>() == false)
                profile["enabled"] = true;
            foreach (string key in new[] { "fixedOpening", "randomMoves", "keepMoves", "randomizeWhenPower" })
                profile.Remove(key);
        }
        foreach (string id in new[] { "LAGAVULIN_MATRIARCH", "SLUMBERING_BEETLE", "WATERFALL_GIANT", "AEONGLASS", "VANTOM", "KNOWLEDGE_DEMON" })
        {
            if (monsters[id] is not JsonObject profile)
                continue;
            profile.Remove("randomizeAllMoves");
            profile["randomMoves"] = defaultProfiles[id]!["randomMoves"]!.DeepClone();
            if (id is "LAGAVULIN_MATRIARCH" or "SLUMBERING_BEETLE")
            {
                profile.Remove("fixedOpening");
                profile["openingStates"] = new JsonArray();
            }
        }
        if (giant != null)
            giant["fixedOpening"] = true;
        if (monsters["AEONGLASS"] is JsonObject aeonglass)
        {
            aeonglass["keepMoves"] ??= new JsonArray();
            var moves = aeonglass["keepMoves"]!.AsArray();
            if (!moves.Any(move => move?.GetValue<string>() == "INCREASING_INTENSITY_MOVE"))
                moves.Add("INCREASING_INTENSITY_MOVE");
        }
        if (monsters["QUEEN"] is JsonObject queen)
            queen["keepVanillaWhileAllyAlive"] ??= "TORCH_HEAD_AMALGAM";
        if (profiles["keepVanilla"] is JsonArray excluded)
        {
            allMoves.UnionWith(["TORCH_HEAD_AMALGAM", "WATERFALL_GIANT", "LAGAVULIN_MATRIARCH", "SLUMBERING_BEETLE"]);
            foreach (var excludedMonster in excluded.Where(entry => entry != null && allMoves.Contains(entry.GetValue<string>())).ToArray())
                excluded.Remove(excludedMonster);
        }

        return Updated();

        StoredSettings Updated() => stored with
        {
            Settings = JsonSerializer.SerializeToElement(settings),
            Profiles = JsonSerializer.SerializeToElement(profiles),
            Revision = SettingsRevision
        };

        static void CleanProfile(JsonNode? profile)
        {
            (profile as JsonObject)?.Remove("hitWeights");
            (profile as JsonObject)?.Remove("deadlyEnemies");
            (profile?["effects"] as JsonObject)?.Remove("CurlUp");
            if (profile?["openingStates"] is JsonArray pool)
                foreach (var entry in pool.Where(entry => entry?.GetValue<string>() == "CURL_UP").ToArray())
                    pool.Remove(entry);
        }

        static void RemoveSteamAction(JsonNode? node)
        {
            if (node is JsonObject map)
                foreach (string key in map.Select(entry => entry.Key)
                    .Where(key => key.Equals("GainSteam", StringComparison.OrdinalIgnoreCase)).ToArray())
                    map.Remove(key);
        }
    }

    private static void UpgradeActionWeightsAndCardRatios(JsonObject actions, JsonNode defaults)
    {
        var weights = (actions["weights"] ??= new JsonObject()).AsObject();
        foreach (var (id, defaultWeight) in defaults["weights"]!.AsObject())
        {
            if (!ActionEffect.IsRareDebuff(Enum.Parse<EffectKind>(id)))
                continue;
            if (weights[id] is not JsonObject weight)
            {
                weights[id] = defaultWeight!.DeepClone();
                continue;
            }

            double baseWeight = weight["base"]?.GetValue<double>() ?? 1;
            if (baseWeight == -1)
                continue;
            weight["base"] = baseWeight * 0.2;
            if (weight["perRoom"]?.GetValue<double>() is double perRoom)
                weight["perRoom"] = perRoom * 0.2;
        }

        var ratios = (actions["statusCardPlayerActionValueRatios"] ??= new JsonObject()).AsObject();
        foreach (var (id, ratio) in defaults["statusCardPlayerActionValueRatios"]!.AsObject())
            ratios[id] ??= ratio!.DeepClone();
    }

    private static void UpgradeBuffPrices(JsonNode settings, JsonNode defaults)
    {
        (string Id, string Term, double[] Previous)[] updates =
        [
            ("Strength", "amount*attackFrequency", [1.2, 1.65, 1.8]),
            ("Ritual", "amount*attackFrequency*survivalTurns", [0.8]),
            ("Regen", "amount*survivalTurns", [0.18, 0.2]),
            ("Plating", "amount*survivalTurns", [0.17, 0.18]),
            ("Thorns", "amount*survivalTurns", [0.65]),
            ("TemporaryThorns", "amount", [1.6, 1.8]),
            ("Vigor", "amount*attackFrequency", [0.6, 1]),
            ("Enrage", "amount*attackFrequency*survivalTurns", [1.1])
        ];
        foreach (var (id, term, previous) in updates)
            if (settings["buffs"]?[id]?["formula"] is JsonObject { Count: 1 } formula &&
                formula[term]?.GetValue<double>() is double current && previous.Any(value => Math.Abs(value - current) < 1e-9))
                formula[term] = defaults["buffs"]![id]!["formula"]![term]!.DeepClone();
    }

    internal static RuleConfiguration Parse(StoredSettings stored)
    {
        var rules = new RuleConfiguration(stored.Settings, stored.Profiles);
        if (rules.Draws.Attempts < 0 || rules.Draws.MaxPerMonster < 0 || rules.Draws.MaxEncounterValueMultiplier < 0 ||
            rules.Draws.None < 0 || rules.Draws.Positive < 0 || rules.Draws.Negative < 0)
            throw new ArgumentException("开场权重、次数和倍率须为非负数。");

        foreach (var (id, state) in rules.OpeningValues)
        {
            if (!NativeActions.SupportsOpening(id) || state.MaxAmount is < 0 ||
                state.MinAmount < 1 || state.HpPerSurvivalTurnCap is < 0 ||
                (state.MaxAmount > 0 && state.MaxAmount < state.MinAmount) ||
                Math.Sign(state.Amount) != (id == "STRENGTH_DOWN" ? -1 : 1) ||
                (id == "STUN" && (state.MinAmount != 1 || state.MaxAmount > 1 || state.HpPerSurvivalTurnCap.HasValue)))
                throw new ArgumentException($"{id} 的开场层数无效。");
        }

        foreach (var (id, monster) in rules.Monsters)
        {
            if (monster.Profile.ActionValue <= 0 ||
                monster.Profile.AttackFrequency < 0 ||
                monster.Profile.SingleHitPreference < 0 || monster.Profile.DesireHitPreference < 0 ||
                monster.Profile.Volatility < 0 || monster.Profile.NonAttackMultiplier < 0)
                throw new ArgumentException($"{id} 的行动力须为正数，其余怪物参数须为非负数。");
            if (monster.Profile.Favorites?.Any(entry => !ActionCatalog.CanFavor(entry.Key) || entry.Value < 0) == true)
                throw new ArgumentException($"{id} 的行动偏好须为可用的非攻击行动，权重须为非负数。");
            foreach (string state in monster.OpeningStates)
            {
                if (!rules.OpeningValues.ContainsKey(state) || !NativeActions.SupportsOpening(state))
                    throw new ArgumentException($"{id} 的开场状态未知：{state}");
            }
        }

        return rules;
    }

    private static JsonElement ReadResource(string name)
    {
        using var stream = typeof(ModEntry).Assembly.GetManifestResourceStream(name)!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }
}
