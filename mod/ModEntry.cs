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
    private const int SettingsRevision = 41;
    private const int MinimumSettingsRevision = SettingsRevision - 5;
    internal static StoredSettings Defaults { get; } = new(true, ReadResource("settings.json"), ReadResource("monster-profiles.json"), SettingsRevision);
    internal static StoredSettings Current { get; private set; } = Defaults;
    private static MonsterMovePresets MovePresets { get; } = RuleConfiguration.Read<MonsterMovePresets>(ReadResource("monster-moves.json"));
    internal static RuleConfiguration Rules { get; private set; } = new(Defaults.Settings, Defaults.Profiles, MovePresets);

    internal static void Load()
    {
        try
        {
            string path = SavePath;
            if (File.Exists(path))
            {
                var stored = Upgrade(JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path))!);
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
        if (stored.Revision < MinimumSettingsRevision)
            throw new ArgumentException($"Settings revision {stored.Revision} predates the supported revision {MinimumSettingsRevision}.");
        if (stored.Revision >= SettingsRevision)
            return stored;

        var profiles = JsonNode.Parse(stored.Profiles.GetRawText())!;
        var defaultProfiles = JsonNode.Parse(Defaults.Profiles.GetRawText())!["monsters"]!.AsObject();
        var settings = JsonNode.Parse(stored.Settings.GetRawText())!;
        var defaultSettings = JsonNode.Parse(Defaults.Settings.GetRawText())!;

        if (stored.Revision == 36)
            UpgradeCurseStacking(settings);

        var currentMonsters = profiles["monsters"]!.AsObject();
        if (stored.Revision < 38)
        {
            AddLaterMonsterValuationDefaults(settings, defaultSettings);
            AddDefaultFavorites(currentMonsters, defaultProfiles);
        }
        if (stored.Revision < 39)
        {
            if (currentMonsters["THIEVING_HOPPER"] is JsonObject hopper)
            {
                var favorites = (hopper["favorites"] ??= new JsonObject()).AsObject();
                if (stored.Revision == 38 && favorites["Debt"]?.GetValue<double>() == 2)
                    favorites.Remove("Debt");
                favorites["DrawDown"] ??= defaultProfiles["THIEVING_HOPPER"]!["favorites"]!["DrawDown"]!.DeepClone();
            }
            if (currentMonsters["GREMLIN_MERC"] is JsonObject merc)
            {
                var favorites = merc["favorites"] ??= new JsonObject();
                favorites["Debt"] ??= defaultProfiles["GREMLIN_MERC"]!["favorites"]!["Debt"]!.DeepClone();
            }
            settings["pools"]!["externalMoveRandomSkip"] ??= defaultSettings["pools"]!["externalMoveRandomSkip"]!.DeepClone();
        }
        if (stored.Revision < 40)
            UpgradeUniqueTerms(settings, defaultSettings);
        if (currentMonsters["TEST_SUBJECT"]?["animations"] is JsonObject animations)
        {
            if (animations["buff"]?.GetValue<string>() == "BurnTrigger")
                animations["buff"] = defaultProfiles["TEST_SUBJECT"]!["animations"]!["buff"]!.DeepClone();
            if (animations["playerDebuff"]?.GetValue<string>() == "BurnTrigger")
                animations.Remove("playerDebuff");
        }
        return stored with
        {
            Settings = JsonSerializer.SerializeToElement(settings),
            Profiles = JsonSerializer.SerializeToElement(profiles),
            Revision = SettingsRevision
        };
    }

    private static void UpgradeCurseStacking(JsonNode settings)
    {
        var policies = settings["actions"]?["policies"];
        var oldDefault = new JsonObject { ["target"] = "player", ["stackable"] = false };
        foreach (var effect in Enum.GetValues<EffectKind>().Where(ActionEffect.IsCurseCard))
            if (policies?[effect.ToString()] is JsonObject policy && JsonNode.DeepEquals(policy, oldDefault))
                policy.Remove("stackable");
    }

    private static void AddLaterMonsterValuationDefaults(JsonNode settings, JsonNode defaults)
    {
        const string field = "laterMonsterPercent";
        foreach (string id in new[] { "Envenom", "PaperCuts", "PainfulStabs" })
            if (settings["buffs"]?[id] is JsonObject state)
                state[field] ??= defaults["buffs"]![id]![field]!.DeepClone();
        if (settings["opening"]?["states"]?["IMBALANCED"] is JsonObject imbalance)
            imbalance[field] ??= defaults["opening"]!["states"]!["IMBALANCED"]![field]!.DeepClone();
    }

    private static void AddDefaultFavorites(JsonObject monsters, JsonObject defaults)
    {
        foreach (var (id, monster) in monsters)
        {
            if (monster is not JsonObject entry || defaults[id]?["favorites"] is not JsonObject defaultFavorites)
                continue;
            var favorites = (entry["favorites"] ??= new JsonObject()).AsObject();
            foreach (var (effect, weight) in defaultFavorites)
                favorites[effect] ??= weight?.DeepClone();
        }
    }

    private static void UpgradeUniqueTerms(JsonNode settings, JsonNode defaults)
    {
        var actions = settings["actions"]!;
        var policies = actions["policies"] ??= new JsonObject();
        foreach (var (id, policy) in defaults["actions"]!["policies"]!.AsObject())
            if (policy?["unique"]?.GetValue<bool>() == true)
            {
                var saved = policies[id] ??= policy.DeepClone();
                saved["unique"] = true;
            }

        var buffs = settings["buffs"]!;
        foreach (var (id, buff) in defaults["buffs"]!.AsObject())
            if (buff?["unique"]?.GetValue<bool>() == true && buffs[id] is JsonObject saved)
                saved["unique"] = true;

        // The legacy default made Vital Spark single-type without charging the shared allowance.
        if (buffs["VitalSpark"] is JsonObject spark && spark["unique"]?.GetValue<bool>() == true)
            spark.Remove("unique");
    }


    internal static RuleConfiguration Parse(StoredSettings stored)
    {
        var rules = new RuleConfiguration(stored.Settings, stored.Profiles, MovePresets);
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
