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
    private const int SettingsRevision = 47;
    private const int MinimumSettingsRevision = SettingsRevision - 5;
    internal static StoredSettings Defaults { get; } = new(true, ReadResource("settings.json"), ReadResource("monster-profiles.json"), SettingsRevision);
    internal static StoredSettings Current { get; private set; } = Defaults;
    private static MonsterMovePresets MovePresets { get; } = RuleConfiguration.Read<MonsterMovePresets>(ReadResource("monster-moves.json"));
    internal static RuleConfiguration Rules { get; private set; } = new(Defaults.Settings, Defaults.Profiles, MovePresets);

    internal static void Load()
    {
        string path;
        StoredSettings previous;
        try
        {
            path = SavePath;
            if (!File.Exists(path))
                return;
            previous = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path))!;
            var stored = Upgrade(previous);
            var rules = Parse(stored);
            Current = stored;
            Rules = rules;
        }
        catch (Exception error)
        {
            Log.Warn($"[MonsterIntentRandomizer] Using default settings: {error.Message}");
            return;
        }

        if (Current.Revision != previous.Revision)
        {
            try { WriteSettings(path, Current); }
            catch (Exception error)
            {
                Log.Warn($"[MonsterIntentRandomizer] Could not save the migrated settings: {error.Message}");
            }
        }
    }

    internal static void Save(StoredSettings edited)
    {
        edited = edited with { Revision = SettingsRevision };
        var rules = Parse(edited);
        WriteSettings(SavePath, edited);
        Current = edited;
        Rules = rules;
    }

    private static void WriteSettings(string path, StoredSettings edited)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(edited, Pretty));
        File.Move(path + ".tmp", path, overwrite: true);
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

        var currentMonsters = profiles["monsters"]!.AsObject();
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
        if (stored.Revision < 42)
            UpgradeSelectionDefaults(settings, defaultSettings);
        if (stored.Revision < 43)
        {
            UpgradeSpendLimits(settings, defaultSettings);
            if (currentMonsters["TEST_SUBJECT"]?["animations"] is JsonObject animations)
            {
                if (animations["buff"]?.GetValue<string>() == "BurnTrigger")
                    animations["buff"] = defaultProfiles["TEST_SUBJECT"]!["animations"]!["buff"]!.DeepClone();
                if (animations["playerDebuff"]?.GetValue<string>() == "BurnTrigger")
                    animations.Remove("playerDebuff");
            }
        }
        if (stored.Revision < 44)
        {
            RemoveProfileDrawSettings(profiles["defaults"]!.AsObject());
            foreach (var (id, node) in currentMonsters)
            {
                var monster = node!.AsObject();
                RemoveProfileDrawSettings(monster);
                if (monster["phases"] is JsonObject phases)
                    foreach (var (_, phase) in phases)
                        RemoveProfileDrawSettings(phase!.AsObject());
                if (monster["animations"] is JsonObject custom &&
                    defaultProfiles[id]?["animations"] is JsonObject defaults &&
                    JsonNode.DeepEquals(custom["attack"], defaults["attack"]))
                    foreach (string key in new[] { "singleAttack", "multiAttack" })
                        if (defaults[key] is { } trigger)
                            custom[key] ??= trigger.DeepClone();
            }
        }
        var pools = settings["pools"]!.AsObject();
        foreach (string key in new[] { "drawBudgetMultiplier", "unusedAttackBudgetShare", "localOverdraftShare", "minimumSelectionSpread",
            "actionValueRetention", "attackDesireRetention" })
            pools.Remove(key);
        UpgradeCombatHorizon(settings, defaultSettings, currentMonsters);
        return stored with
        {
            Settings = JsonSerializer.SerializeToElement(settings),
            Profiles = JsonSerializer.SerializeToElement(profiles),
            Revision = SettingsRevision
        };

        static void RemoveProfileDrawSettings(JsonObject profile)
        {
            profile.Remove("volatility");
            profile.Remove("singleHitPreference");
            profile.Remove("desireHitPreference");
        }
    }

    private static void UpgradeCombatHorizon(JsonNode settings, JsonNode defaults, JsonObject monsters)
    {
        var actions = settings["actions"]!;
        var placements = actions["cardPlacementValues"]!.AsObject();
        placements.Remove("bottom");
        placements.Remove("hand");
        foreach (var (_, monster) in monsters)
        {
            if (monster?["cards"] is not JsonArray cards)
                continue;
            foreach (var placement in cards.Where(card => card?["pile"]?.GetValue<string>() is { } pile &&
                (pile.Equals("Bottom", StringComparison.OrdinalIgnoreCase) ||
                 pile.Equals("Hand", StringComparison.OrdinalIgnoreCase))).ToArray())
                cards.Remove(placement);
            if (cards.Count == 0)
                monster.AsObject().Remove("cards");
        }

        var valuation = actions["valuation"]!;
        var reference = defaults["actions"]!["valuation"]!;
        Update("Smoggy", "playerActionValue", "playerActionValue*combatTurns", 0.22);
        Update("Tender", "fixed", "combatTurns", 12);
        Update("StrengthDown", "amount", "amount*combatTurns", 2.8);
        Update("DexterityDown", "amount", "amount*combatTurns", 2.3);

        var cardPrices = actions["statusCardValues"]!;
        var referencePrices = defaults["actions"]!["statusCardValues"]!;
        foreach (var (id, oldPrice) in new[]
        {
            ("Dazed", 0.85), ("Wound", 1.2), ("Burn", 2.4), ("Toxic", 3.35), ("Void", 3.45), ("Slimed", 1.05)
        })
            if (cardPrices[id]?.GetValue<double>() == oldPrice)
                cardPrices[id] = referencePrices[id]!.DeepClone();

        void Update(string id, string baseTerm, string durationTerm, double oldDefault)
        {
            if (valuation[id] is not JsonObject formula || formula[durationTerm] != null ||
                formula[baseTerm] is not JsonValue previous)
                return;
            double current = previous.GetValue<double>();
            double constant = reference[id]![baseTerm]!.GetValue<double>();
            double perRound = reference[id]![durationTerm]!.GetValue<double>();
            // Preserve tuned values at three rounds when adding duration.
            double scale = current == oldDefault ? 1 : current / (constant + 3 * perRound);
            formula[baseTerm] = constant * scale;
            formula[durationTerm] = perRound * scale;
        }
    }

    private static void UpgradeSpendLimits(JsonNode settings, JsonNode defaults)
    {
        var actions = settings["actions"]!;
        var defaultActions = defaults["actions"]!;
        var limits = (actions["maxCostMultipliers"] ??= new JsonObject()).AsObject();
        var oldLimits = actions["maxAmounts"];
        foreach (var (id, value) in defaultActions["maxCostMultipliers"]!.AsObject())
        {
            if (oldLimits?[id]?.GetValue<int>() == 0)
                limits[id] = 0d;
            else
                limits[id] ??= value!.DeepClone();
        }
        actions.AsObject().Remove("maxAmounts");

        var opening = settings["opening"]!["states"]!.AsObject();
        UpgradeStates(settings["buffs"]!.AsObject(), defaults["buffs"]!);
        UpgradeStates(opening, defaults["opening"]!["states"]!);
        UseFlatPrice(actions["valuation"]?["Shrink"]);
        UseFlatPrice(opening["IMBALANCED"]?["formula"]);
        UseFlatPrice(opening["STUN"]?["formula"]);

        static void UpgradeStates(JsonObject states, JsonNode defaultStates)
        {
            foreach (var (id, value) in states)
            {
                if (value is not JsonObject state)
                    continue;
                state["maxCostMultiplier"] ??= state["maxAmount"]?.GetValue<int>() == 0 ? JsonValue.Create(0d) :
                    defaultStates[id]?["maxCostMultiplier"]?.DeepClone() ?? JsonValue.Create(1d);
                state.Remove("maxAmount");
                state.Remove("hpPerSurvivalTurnCap");
            }
        }

        static void UseFlatPrice(JsonNode? node)
        {
            if (node is not JsonObject formula || formula.Count != 1)
                return;
            var (term, coefficient) = formula.First();
            if (term.Equals("amount*actionValue", StringComparison.OrdinalIgnoreCase))
            {
                formula.Remove(term);
                formula["actionValue"] = coefficient;
            }
        }
    }

    private static void UpgradeSelectionDefaults(JsonNode settings, JsonNode defaults)
    {
        var actions = settings["actions"]!;
        var weights = (actions["weights"] ??= new JsonObject()).AsObject();
        foreach (string id in new[] { "Weak", "Frail", "Dazed" })
            weights[id] ??= defaults["actions"]!["weights"]![id]!.DeepClone();
        var strength = settings["buffs"]?["Strength"];
        if (strength?["weight"]?.GetValue<double>() == 2.5)
            strength["weight"] = defaults["buffs"]!["Strength"]!["weight"]!.DeepClone();
        var pools = settings["pools"]!;
        foreach (var (key, previous) in new[] { ("favoriteDirectBaseShare", 0.10), ("favoriteDirectStrengthMultiplier", 0.02) })
            if (pools[key] == null || pools[key]!.GetValue<double>() == previous)
                pools[key] = defaults["pools"]![key]!.DeepClone();
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
            if (!NativeActions.SupportsOpening(id) || state.MinAmount < 1 ||
                Math.Sign(state.Amount) != (id == "STRENGTH_DOWN" ? -1 : 1))
                throw new ArgumentException($"{id} 的开场层数无效。");
        }

        foreach (var (id, monster) in rules.Monsters)
        {
            if (monster.Profile.ActionValue <= 0 ||
                monster.Profile.AttackFrequency < 0 || monster.Profile.NonAttackMultiplier < 0)
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

    internal static JsonElement ReadResource(string name)
    {
        using var stream = typeof(ModEntry).Assembly.GetManifestResourceStream(name)!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }
}
