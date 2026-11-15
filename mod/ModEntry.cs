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
    private const int SettingsRevision = 5;
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
        var defaults = JsonNode.Parse(Defaults.Settings.GetRawText())!;
        var savedValuations = settings["actions"]!["valuation"]!.AsObject();
        var defaultValuations = defaults["actions"]!["valuation"]!;
        foreach (string id in new[] { "Tangled", "Ringing", "Smoggy" }.Where(_ => stored.Revision < 3))
        {
            if (savedValuations[id] is JsonObject old &&
                !old.Any(term => term.Key.Split('*', StringSplitOptions.TrimEntries)
                    .Contains("playerTurnValue", StringComparer.OrdinalIgnoreCase)))
                savedValuations[id] = defaultValuations[id]!.DeepClone();
        }
        // A formula present in a save owns its term keys, including replacements made in the editor.
        foreach (var (id, formula) in defaultValuations.AsObject())
            if (!savedValuations.ContainsKey(id))
                savedValuations[id] = formula!.DeepClone();
        defaults["actions"]!.AsObject().Remove("valuation");
        FillMissing(settings.AsObject(), defaults.AsObject());

        var profiles = JsonNode.Parse(stored.Profiles.GetRawText())!;
        ApplyActionCalibration(profiles);
        if (stored.Revision >= 2)
            return stored with
            {
                Settings = JsonSerializer.SerializeToElement(settings),
                Profiles = JsonSerializer.SerializeToElement(profiles),
                Revision = SettingsRevision
            };

        var opening = settings["opening"]!;
        if (stored.Revision == 0 && opening["maxEncounterValueMultiplier"]!.GetValue<double>() == 0.5)
            opening["maxEncounterValueMultiplier"] = 1;

        foreach (var (id, oldCoefficient) in OldOpeningCoefficients.Where(_ => stored.Revision == 0))
        {
            var state = opening["states"]![id]!;
            if (state["turns"]?.GetValue<double>() == oldCoefficient)
                state["turns"] = defaults["opening"]!["states"]![id]!["turns"]!.DeepClone();
        }
        foreach (var (id, defaultState) in defaults["opening"]!["states"]!.AsObject().Where(_ => stored.Revision == 0))
        {
            var state = opening["states"]![id]!;
            if (state["amount"]?.GetValue<int>() != defaultState!["amount"]?.GetValue<int>())
                state.AsObject().Remove("amounts");
        }

        var defaultProfiles = JsonNode.Parse(Defaults.Profiles.GetRawText())!;
        foreach (var (id, defaultProfile) in defaultProfiles["monsters"]!.AsObject())
        {
            if (profiles["monsters"]?[id] is not JsonObject profile)
                continue;
            FillMissing(profile, defaultProfile!.AsObject());
            if (profile["openingStates"] is JsonArray pool && defaultProfile["openingStates"] is JsonArray candidates)
            {
                foreach (var candidate in candidates.Where(value => value != null &&
                    NewOpeningIds.Contains(value.GetValue<string>())))
                {
                    if (!pool.Any(existing => JsonNode.DeepEquals(existing, candidate)))
                        pool.Add(candidate!.DeepClone());
                }
            }
        }
        if (profiles["keepVanilla"] is JsonArray exclusions)
        {
            foreach (var entry in exclusions.Where(value => value != null &&
                (value.GetValue<string>() == "TUNNELER" ||
                    stored.Revision == 0 && NewlyEligible.Contains(value.GetValue<string>()))).ToArray())
                exclusions.Remove(entry);
        }
        foreach (string section in new[] { "valuation", "amounts", "minActs" })
            (settings["actions"]?[section] as JsonObject)?.Remove("PlayerDoom");
        var doom = opening["states"]!["DOOM"]!;
        if (JsonNode.DeepEquals(doom["amounts"], JsonNode.Parse("[2,3,5,7]")))
            doom["amounts"] = defaults["opening"]!["states"]!["DOOM"]!["amounts"]!.DeepClone();
        var monsters = profiles["monsters"]!;
        foreach (string id in new[] { "EXOSKELETON", "MYTE", "NIBBIT" })
            (monsters[id] as JsonObject)?.Remove("fixedOpening");
        RemoveKeptMove("SHRINKER_BEETLE", "SHRINKER_MOVE");
        RemoveKeptMove("FROG_KNIGHT", "BEETLE_CHARGE");
        foreach (var (_, profile) in monsters.AsObject())
            (profile?["effects"] as JsonObject)?.Remove("PlayerDoom");
        UpdatePreference("SHRINKER_BEETLE", "attackFrequency", "1");
        UpdatePreference("SHRINKER_BEETLE", "preferences", """{"attack":100,"buff":0,"defend":0,"playerDebuff":0}""");
        UpdatePreference("TUNNELER", "attackFrequency", "0.67");
        UpdatePreference("TUNNELER", "preferences", """{"attack":66.67,"buff":16.67,"defend":16.67,"playerDebuff":0}""");
        return new StoredSettings(stored.Enabled, JsonSerializer.SerializeToElement(settings),
            JsonSerializer.SerializeToElement(profiles), SettingsRevision, stored.ShowIntentDetails);

        void RemoveKeptMove(string id, string move)
        {
            if (monsters[id]?["keepMoves"] is JsonArray list)
                foreach (var entry in list.Where(entry => entry?.GetValue<string>() == move).ToArray())
                    list.Remove(entry);
        }

        void UpdatePreference(string id, string key, string previous)
        {
            if (monsters[id] is JsonObject monster && JsonNode.DeepEquals(monster[key], JsonNode.Parse(previous)))
                monster[key] = defaultProfiles["monsters"]![id]![key]!.DeepClone();
        }
    }

    private static void ApplyActionCalibration(JsonNode profiles)
    {
        var data = ReadResource("action-value-calibration.json").GetProperty("monsters");
        foreach (var entry in data.EnumerateObject())
        {
            if (profiles["monsters"]?[entry.Name] is not JsonObject profile)
                continue;

            var previous = entry.Value.GetProperty("previous");
            var proposed = entry.Value.GetProperty("proposed");
            var previousBase = previous[0].GetDouble();
            var previousHigh = OptionalNumber(previous[1]);
            var nextBase = proposed[0].GetDouble();
            var nextHigh = OptionalNumber(proposed[1]);
            bool usesDefaultBase = profile["actionValue"]?.GetValue<double>() == previousBase;
            if (usesDefaultBase && previousBase != nextBase)
                profile["actionValue"] = nextBase;
            if (previousHigh != nextHigh && profile["deadlyEnemies"]?.GetValue<double>() == previousHigh &&
                (previousHigh.HasValue || usesDefaultBase))
            {
                if (nextHigh is double value)
                    profile["deadlyEnemies"] = value;
                else
                    profile.Remove("deadlyEnemies");
            }
        }

        static double? OptionalNumber(JsonElement number) => number.ValueKind == JsonValueKind.Null ? null : number.GetDouble();
    }

    private static readonly Dictionary<string, double> OldOpeningCoefficients = new()
    {
        ["STRENGTH"] = 0.2, ["RITUAL"] = 0.4, ["STRENGTH_DOWN"] = -0.2,
        ["REGEN"] = 0.08, ["PLATING"] = 0.06, ["DOOM"] = -0.04
    };

    private static readonly HashSet<string> NewOpeningIds =
        ["PERSONAL_HIVE", "VITAL_SPARK", "WITHERING_PRESENCE", "ENVENOM", "PAPER_CUTS"];
    private static readonly HashSet<string> NewlyEligible =
        ["CORPSE_SLUG", "EXOSKELETON", "FROG_KNIGHT", "MYTE", "NIBBIT", "SEWER_CLAM", "SHRINKER_BEETLE", "SKULKING_COLONY"];

    private static void FillMissing(JsonObject stored, JsonObject defaults)
    {
        foreach (var (key, value) in defaults)
        {
            if (!stored.TryGetPropertyValue(key, out var current))
                stored[key] = value?.DeepClone();
            else if (current is JsonObject nested && value is JsonObject nestedDefaults)
                FillMissing(nested, nestedDefaults);
        }
    }

    internal static RuleConfiguration Parse(StoredSettings stored)
    {
        var rules = new RuleConfiguration(stored.Settings, stored.Profiles);
        if (rules.Draws.Attempts < 0 || rules.Draws.MaxPerMonster < 0 || rules.Draws.MaxEncounterValueMultiplier < 0 ||
            rules.Draws.None < 0 || rules.Draws.Positive < 0 || rules.Draws.Negative < 0)
            throw new ArgumentException("开场权重、次数和倍率须为非负数。");

        foreach (var (id, state) in rules.OpeningValues)
        {
            int[] doses = state.Amounts ?? [state.Amount];
            if (doses.Length == 0 || doses.Any(amount => Math.Sign(amount) != (id == "STRENGTH_DOWN" ? -1 : 1)) ||
                (id == "STUN" && doses.Any(amount => amount != 1)) || state.Weight < 0 || state.MinAct < 1)
                throw new ArgumentException($"{id} 的开场层数、权重或幕数无效。");
        }

        foreach (var (id, monster) in rules.Monsters)
        {
            if (monster.Profile.ActionValue <= 0 || monster.DeadlyEnemies is <= 0 ||
                monster.Profile.AttackFrequency < 0 || monster.Profile.BaseExpectedSurvivalTurns < 0 ||
                monster.Profile.Volatility < 0 || monster.Profile.NonAttackMultiplier < 0)
                throw new ArgumentException($"{id} 的行动力须为正数，其余怪物参数须为非负数。");
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
