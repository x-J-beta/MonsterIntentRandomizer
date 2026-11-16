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
    private const int SettingsRevision = 39;
    internal static StoredSettings Defaults { get; } = new(true, ReadResource("settings.json"), ReadResource("monster-profiles.json"), SettingsRevision);
    internal static StoredSettings Current { get; private set; } = Defaults;
    private static MonsterMovePresets MovePresets { get; } = RuleConfiguration.Read<MonsterMovePresets>(ReadResource("monster-moves.json"));
    internal static RuleConfiguration Rules { get; private set; } = new(Defaults.Settings, Defaults.Profiles, MovePresets);

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

        var profiles = JsonNode.Parse(stored.Profiles.GetRawText())!;
        var defaultProfiles = JsonNode.Parse(Defaults.Profiles.GetRawText())!["monsters"]!.AsObject();
        if (stored.Revision < 26)
            UpgradePhaseProfiles(profiles, defaultProfiles);
        var settings = JsonNode.Parse(stored.Settings.GetRawText())!;
        var defaultSettings = JsonNode.Parse(Defaults.Settings.GetRawText())!;
        if (stored.Revision >= 25)
            return Updated();

        var actions = settings["actions"]!.AsObject();
        if (stored.Revision < 21)
        {
            actions.Remove("specialDebuffChance");
            UpgradeActionWeightsAndCardRatios(actions, defaultSettings["actions"]!);
        }
        if (stored.Revision < 22)
            foreach (var kind in Enum.GetValues<EffectKind>().Where(ActionEffect.UsesThreshold))
                if (settings["buffs"]?[kind.ToString()] is JsonObject rule)
                    rule["stackable"] = true;
        if (stored.Revision < 24)
            UpgradeGeneratedCards(settings["buffs"] as JsonObject, defaultSettings["buffs"]!);
        if (stored.Revision < 23 && settings["simulation"] is JsonObject simulation)
        {
            foreach (string key in new[] { "fallback", "targetPriority", "targetBlock", "targetFirstPlayerWindow",
                "firstPlayerWindowFraction", "recentPlayerDamage" })
                simulation.Remove(key);
            (simulation["turnContext"] as JsonObject)?.Remove("expectedDamageAgainstMonster");
            if (simulation["allies"] is JsonArray allies)
                foreach (var ally in allies.OfType<JsonObject>())
                    foreach (string key in new[] { "maxHp", "block", "priority", "firstPlayerWindow", "targetable" })
                        ally.Remove(key);
        }
        settings["opening"]!["states"]!["NEUROSURGE"] ??=
            defaultSettings["opening"]!["states"]!["NEUROSURGE"]!.DeepClone();
        UpgradeMonsterCoverage(profiles, defaultProfiles);
        UpgradeNaturalStatusWeights(settings, defaultSettings);
        foreach (var (_, entry) in profiles["monsters"]!.AsObject())
        {
            (entry as JsonObject)?.Remove("effects");
            if (entry?["phases"] is JsonObject phases)
                foreach (var (_, phase) in phases)
                    (phase as JsonObject)?.Remove("effects");
        }
        if (stored.Revision >= 20)
            return Updated();

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
        var monsters = profiles["monsters"]!.AsObject();
        (profiles["defaults"] as JsonObject)?.Remove("attackSpend");
        foreach (var (id, profile) in monsters)
        {
            if (profile is not JsonObject entry)
                continue;
            entry.Remove("attackSpend");
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
        var allMoves = new HashSet<string>(["AXEBOT", "BOWLBUG_EGG", "BOWLBUG_ROCK", "INFESTED_PRISM",
            "LOUSE_PROGENITOR", "SCROLL_OF_BITING", "THE_FORGOTTEN", "THE_LOST", "TOADPOLE"], StringComparer.Ordinal);
        foreach (string id in allMoves)
        {
            if (monsters[id] is not JsonObject profile)
                continue;
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
            profile.Remove("randomMoves");
            if (id is "LAGAVULIN_MATRIARCH" or "SLUMBERING_BEETLE")
            {
                profile.Remove("fixedOpening");
                profile["openingStates"] = new JsonArray();
            }
        }
        if (profiles["keepVanilla"] is JsonArray excluded)
        {
            allMoves.UnionWith(["TORCH_HEAD_AMALGAM", "WATERFALL_GIANT", "LAGAVULIN_MATRIARCH", "SLUMBERING_BEETLE"]);
            foreach (var excludedMonster in excluded.Where(entry => entry != null && allMoves.Contains(entry.GetValue<string>())).ToArray())
                excluded.Remove(excludedMonster);
        }

        return Updated();

        StoredSettings Updated()
        {
            if (stored.Revision < 28)
            {
                UpgradeMoveRules(profiles, defaultProfiles, stored.Revision);
                settings["buffs"]!["SteamEruption"] ??= defaultSettings["buffs"]!["SteamEruption"]!.DeepClone();
            }
            if (stored.Revision < 29)
                UpgradeRoster(profiles["monsters"]!.AsObject(), defaultProfiles);
            if (stored.Revision < 31)
                MergeAliasedProfiles(profiles["monsters"]!.AsObject(), defaultProfiles);
            settings["playerAction"] ??= defaultSettings["playerAction"]!.DeepClone();
            settings["playerAction"]!["useCombatDamage"] ??= false;
            if (stored.Revision < 32)
                UpgradeDisabledWeights(settings);
            if (stored.Revision < 34)
            {
                UpgradeStatReductionWeights(settings, defaultSettings);
                AddStatReductionFavorites(profiles["monsters"]!.AsObject(), defaultProfiles);
            }
            if (stored.Revision < 35)
            {
                UpgradeChaos(settings, defaultSettings);
                AddActionDefaults(settings, defaultSettings, "Dampen", "Hex");
            }
            var curseIds = Enum.GetValues<EffectKind>().Where(ActionEffect.IsCurseCard)
                .Select(kind => kind.ToString()).ToArray();
            if (stored.Revision < 36)
                AddActionDefaults(settings, defaultSettings, curseIds);
            else if (stored.Revision == 36)
                UpgradeCurseStacking(settings, curseIds);
            var currentMonsters = profiles["monsters"]!.AsObject();
            if (stored.Revision < 38)
            {
                AddLaterMonsterValuationDefaults(settings, defaultSettings);
                AddDefaultFavorites(currentMonsters, defaultProfiles);
            }
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
            return stored with
            {
                Settings = JsonSerializer.SerializeToElement(settings),
                Profiles = JsonSerializer.SerializeToElement(profiles),
                Revision = SettingsRevision
            };
        }

        static void CleanProfile(JsonNode? profile)
        {
            (profile as JsonObject)?.Remove("hitWeights");
            (profile as JsonObject)?.Remove("deadlyEnemies");
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

    private static void UpgradeChaos(JsonNode settings, JsonNode defaults)
    {
        var pools = settings["pools"]!.AsObject();
        var defaultPools = defaults["pools"]!;
        if (pools["chaos"] == null)
        {
            pools["chaos"] = pools["chaosMultiplier"]?.GetValue<double>() is double oldChaos
                ? Math.Clamp((oldChaos - 1) / 4, 0, 1) : defaultPools["chaos"]!.GetValue<double>();
            pools["fullChaosBudgetAt"] = pools["fullChaosBudgetAt"]?.GetValue<double>() is double oldThreshold && oldThreshold > 1
                ? (oldThreshold - 1) / 4 : defaultPools["fullChaosBudgetAt"]!.GetValue<double>();
        }
        pools.Remove("chaosMultiplier");
    }

    private static void AddActionDefaults(JsonNode settings, JsonNode defaults, params string[] effects)
    {
        var actions = settings["actions"]!;
        var defaultActions = defaults["actions"]!;
        foreach (string section in new[] { "valuation", "maxAmounts", "minAmounts", "weights", "policies" })
        {
            var entries = (actions[section] ??= new JsonObject()).AsObject();
            foreach (string effect in effects)
                if (defaultActions[section]?[effect] is { } value)
                    entries[effect] ??= value.DeepClone();
        }
    }

    private static void UpgradeCurseStacking(JsonNode settings, IEnumerable<string> effects)
    {
        var policies = settings["actions"]?["policies"];
        var oldDefault = new JsonObject { ["target"] = "player", ["stackable"] = false };
        foreach (string effect in effects)
            if (policies?[effect] is JsonObject policy && JsonNode.DeepEquals(policy, oldDefault))
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

    private static void UpgradeStatReductionWeights(JsonNode settings, JsonNode defaults)
    {
        if (settings["actions"]?["weights"] is not JsonObject weights)
            return;

        var defaultWeights = defaults["actions"]!["weights"]!;
        foreach (var (id, baseWeight, perRoom) in new (string, double, double)[]
            { ("StrengthDown", -0.15, 0.008), ("StrengthDown", -0.12, 0.02),
                ("DexterityDown", RoomWeight.DisabledBase, 0), ("DexterityDown", -0.10, 0.015) })
            if (weights[id] is JsonObject weight && weight["base"]?.GetValue<double>() == baseWeight &&
                (weight["perRoom"]?.GetValue<double>() ?? 0) == perRoom)
                weights[id] = defaultWeights[id]!.DeepClone();
    }

    private static void AddStatReductionFavorites(JsonObject monsters, JsonObject defaults)
    {
        foreach (string id in new[] { "LAGAVULIN_MATRIARCH", "THE_LOST", "THE_FORGOTTEN" })
        {
            if (monsters[id] is not JsonObject monster)
                continue;
            var favorites = (monster["favorites"] ??= new JsonObject()).AsObject();
            var defaultFavorites = defaults[id]!["favorites"]!;
            foreach (string effect in new[] { "StrengthDown", "DexterityDown" })
                if (defaultFavorites[effect] is { } weight)
                    favorites[effect] ??= weight.DeepClone();
        }
    }

    private static void UpgradeDisabledWeights(JsonNode settings)
    {
        MoveDisabledValues(settings["actions"]?["weights"], "base");
        MoveDisabledValues(settings["buffs"], "weight");
        MoveDisabledValues(settings["opening"]?["states"], "weight");

        static void MoveDisabledValues(JsonNode? group, string field)
        {
            if (group is not JsonObject weights)
                return;
            foreach (var (_, entry) in weights)
                if (entry is JsonObject weight && weight[field]?.GetValue<double>() == -1)
                    weight[field] = RoomWeight.DisabledBase;
        }
    }

    private static void MergeAliasedProfiles(JsonObject monsters, JsonObject defaults)
    {
        foreach (var (alias, id) in MovePresets.Aliases ?? [])
        {
            if (monsters[alias] is not JsonObject separate)
                continue;

            if (monsters[id] == null)
            {
                var shared = separate.DeepClone().AsObject();
                shared["name"] = defaults[id]?["name"]?.DeepClone();
                monsters[id] = shared;
            }
            monsters.Remove(alias);
        }
    }

    private static void UpgradeRoster(JsonObject monsters, JsonObject defaults)
    {
        foreach (string id in new[] { "ARCHITECT", "BATTLE_FRIEND_V1", "BATTLE_FRIEND_V2", "BATTLE_FRIEND_V3",
            "BYRDINE_MONSTER", "BYRDPIP", "OSTY", "PAELS_LEGION", "THE_ADVERSARY_MK_ONE", "THE_ADVERSARY_MK_TWO",
            "THE_ADVERSARY_MK_THREE", "WUPPY" })
            monsters.Remove(id);

        var definition = defaults["TEST_SUBJECT"]!;
        var subject = (monsters["TEST_SUBJECT"] ??= definition.DeepClone()).AsObject();
        var kept = (subject["keepMoves"] ??= new JsonArray()).AsArray();
        var existing = kept.Select(move => move!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var native = MovePresets.MoveIds("TEST_SUBJECT").ToHashSet(StringComparer.Ordinal);
        const string respawn = "RESPAWN_MOVE";
        if (native.IsSubsetOf(existing))
            foreach (var move in kept.Where(move => move!.GetValue<string>() != respawn && native.Contains(move.GetValue<string>())).ToArray())
                kept.Remove(move);
        if (!existing.Contains(respawn))
            kept.Add(respawn);

        subject["phases"] ??= definition["phases"]!.DeepClone();
        subject["animations"] ??= definition["animations"]!.DeepClone();
        if (subject["hp"]?.GetValue<double>() == 1)
            subject["hp"] = definition["hp"]!.DeepClone();
    }

    private static void UpgradePhaseProfiles(JsonNode profiles, JsonObject defaults)
    {
        var monsters = profiles["monsters"]!.AsObject();
        if (monsters["CEREMONIAL_BEAST"]?["phases"]?["rush"] is JsonObject rush)
            rush["allowedBuffs"] ??= defaults["CEREMONIAL_BEAST"]!["phases"]!["rush"]!["allowedBuffs"]!.DeepClone();

        var added = new[] { "PHANTASMAL_GARDENER", "SNEAKY_GREMLIN", "THIEVING_HOPPER" };
        var excluded = profiles["keepVanilla"] as JsonArray;
        foreach (string id in added)
        {
            monsters[id] ??= defaults[id]!.DeepClone();

            var previous = excluded?.FirstOrDefault(entry => entry?.GetValue<string>() == id);
            if (previous != null)
                excluded!.Remove(previous);
        }
    }

    private static void UpgradeMoveRules(JsonNode profiles, JsonObject defaults, int revision)
    {
        var excluded = MoveSet(profiles["keepVanilla"]);
        if (revision < 27)
            excluded.UnionWith(["CRUSHER", "ROCKET", "LIVING_SHIELD"]);
        profiles.AsObject().Remove("keepVanilla");
        var monsters = profiles["monsters"]!.AsObject();
        foreach (var (id, entry) in monsters)
        {
            var profile = entry!.AsObject();
            profile["animations"] ??= defaults[id]?["animations"]?.DeepClone();
            var kept = MoveSet(profile["keepMoves"]);
            var known = MovePresets.MoveIds(id);
            var stock = MoveSet(defaults[id]?["keepMoves"]);
            if (excluded.Contains(id))
                kept.UnionWith(known);
            else if (!known.All(stock.Contains))
                kept.UnionWith(stock);
            if (profile["randomMoves"] is JsonArray allowlist)
                kept.UnionWith(known.Except(MoveSet(allowlist)));
            if (profile["fixedOpening"]?.GetValue<bool>() == true && MovePresets.SequenceFor(id)?.First is { } first)
                kept.Add(first);
            if (kept.Count > 0)
                profile["keepMoves"] = JsonSerializer.SerializeToNode(kept);
            foreach (string key in new[] { "randomMoves", "fixedOpening", "randomizeWhenPower", "keepVanillaWhileAllyAlive", "randomizeAllMoves" })
                profile.Remove(key);
            if (profile["phases"] is JsonObject phases)
                foreach (var (_, phase) in phases)
                    (phase as JsonObject)?.Remove("randomizeWhenPower");
        }

        if (monsters["WATERFALL_GIANT"] is JsonObject giant)
        {
            var favorites = (giant["favorites"] ??= new JsonObject()).AsObject();
            favorites["SteamEruption"] ??= defaults["WATERFALL_GIANT"]!["favorites"]!["SteamEruption"]!.DeepClone();
        }

        static HashSet<string> MoveSet(JsonNode? node) => node is JsonArray array
            ? array.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : [];
    }

    private static void UpgradeMonsterCoverage(JsonNode profiles, JsonObject defaults)
    {
        var added = new HashSet<string>(["PHROG_PARASITE", "WRIGGLER", "DECIMILLIPEDE_SEGMENT",
            "EYE_WITH_TEETH", "PARAFRIGHT", "TOUGH_EGG", "TWO_TAILED_RAT"]);
        if (profiles["keepVanilla"] is JsonArray excluded)
            foreach (var entry in excluded.Where(entry => entry != null && added.Contains(entry.GetValue<string>())).ToArray())
                excluded.Remove(entry);

        var monsters = profiles["monsters"]!.AsObject();
        if (monsters["CEREMONIAL_BEAST"] is JsonObject beast)
        {
            var definition = defaults["CEREMONIAL_BEAST"]!;
            beast["phases"] ??= definition["phases"]!.DeepClone();
            if (beast["randomMoves"] == null ||
                JsonNode.DeepEquals(beast["randomMoves"], new JsonArray("STOMP_MOVE", "CRUSH_MOVE")))
                beast.AsObject().Remove("randomMoves");
        }
        foreach (string id in added)
        {
            var definition = defaults[id]!.AsObject();
            if (monsters[id] is not JsonObject profile)
            {
                monsters[id] = definition.DeepClone();
                continue;
            }
            foreach (var (key, value) in definition)
                profile[key] ??= value?.DeepClone();
            bool millipede = id.StartsWith("DECIMILLIPEDE_", StringComparison.Ordinal);
            if (millipede && profile["attackFrequency"]?.GetValue<double>() == 1)
                profile["attackFrequency"] = definition["attackFrequency"]!.DeepClone();
            if (millipede && MatchesWeights(profile, 25, 25, 25, 25) ||
                id == "PHROG_PARASITE" && MatchesWeights(profile, 50, 0, 0, 50))
                profile["preferences"] = definition["preferences"]!.DeepClone();
        }
        UpgradeSummoner("LIVING_FOG", new JsonArray("SUPER_GAS_BLAST_MOVE"), 100, 0, removeFixedOpening: true);
        UpgradeSummoner("OVICOPTER", new JsonArray("SMASH_MOVE", "TENDERIZER_MOVE"), 74.97, 25.03);

        void UpgradeSummoner(string id, JsonArray oldMoves, double attack, double debuff, bool removeFixedOpening = false)
        {
            if (monsters[id] is not JsonObject profile ||
                profile["randomMoves"] != null && !JsonNode.DeepEquals(profile["randomMoves"], oldMoves))
                return;
            var definition = defaults[id]!;
            profile.AsObject().Remove("randomMoves");
            profile["favorites"] ??= definition["favorites"]!.DeepClone();
            if (removeFixedOpening)
                profile.Remove("fixedOpening");
            if (MatchesWeights(profile, attack, 0, 0, debuff))
                profile["preferences"] = definition["preferences"]!.DeepClone();
            if (profile["attackFrequency"]?.GetValue<double>() == 1)
                profile["attackFrequency"] = definition["attackFrequency"]!.DeepClone();
        }

        static bool MatchesWeights(JsonObject profile, double attack, double buff, double defend, double debuff) =>
            profile["preferences"] is JsonObject weights &&
            weights["attack"]?.GetValue<double>() == attack && weights["buff"]?.GetValue<double>() == buff &&
            weights["defend"]?.GetValue<double>() == defend && weights["playerDebuff"]?.GetValue<double>() == debuff;
    }

    private static void UpgradeNaturalStatusWeights(JsonNode settings, JsonNode defaults)
    {
        var weights = (settings["actions"]!["weights"] ??= new JsonObject()).AsObject();
        foreach (string id in new[] { "Tangled", "Poison", "Ringing", "Smoggy", "Tender", "Shrink",
            "StrengthDown", "DexterityDown", "Soulbind", "DrawDown", "EnergyDown" })
        {
            var existing = weights[id];
            double oldBase = id == "Soulbind" ? 0 : 0.2;
            double oldRoom = id == "Soulbind" ? 0.002 : 0;
            if (existing == null ||
                existing["base"]?.GetValue<double>() is double weight && Math.Abs(weight - oldBase) < 1e-9 &&
                Math.Abs((existing["perRoom"]?.GetValue<double>() ?? 0) - oldRoom) < 1e-9)
                weights[id] = defaults["actions"]!["weights"]![id]!.DeepClone();
        }

        if (settings["opening"]?["states"]?["IMBALANCED"] is JsonObject imbalance &&
            imbalance["weight"]?.GetValue<double>() is double previous && Math.Abs(previous - 0.35) < 1e-9)
            imbalance["weight"] = defaults["opening"]!["states"]!["IMBALANCED"]!["weight"]!.DeepClone();
        if (settings["buffs"]?["Intangible"] is JsonObject intangible &&
            (intangible["weight"]?.GetValue<double>() ?? 1) == 1 && (intangible["weightPerRoom"]?.GetValue<double>() ?? 0) == 0)
        {
            intangible["weight"] = defaults["buffs"]!["Intangible"]!["weight"]!.DeepClone();
            intangible["weightPerRoom"] = defaults["buffs"]!["Intangible"]!["weightPerRoom"]!.DeepClone();
        }
    }

    private static void UpgradeGeneratedCards(JsonObject? buffs, JsonNode defaults)
    {
        if (buffs == null)
            return;
        foreach (var kind in TriggeredCardBuffs.Types.Keys)
        {
            string id = kind.ToString();
            if (buffs[id] is JsonObject buff)
                buff["generatedCards"] ??= defaults[id]!["generatedCards"]!.DeepClone();
        }

        if (buffs["WitheringPresence"] is not JsonObject withering)
            return;
        if ((withering["maxAmount"]?.GetValue<int>() ?? 1) == 1)
        {
            withering["maxAmount"] = defaults["WitheringPresence"]!["maxAmount"]!.DeepClone();
            withering["minAmount"] = defaults["WitheringPresence"]!["minAmount"]!.DeepClone();
        }
        withering["stackable"] = true;
        if (withering["formula"] is JsonObject { Count: 1 } formula)
        {
            string term = formula.First().Key;
            if (term.Equals("survivalTurns", StringComparison.OrdinalIgnoreCase))
                withering["formula"] = new JsonObject { ["survivalTurns*inverseAmount"] = formula[term]!.GetValue<double>() * 6 };
        }
    }

    private static void UpgradeActionWeightsAndCardRatios(JsonObject actions, JsonNode defaults)
    {
        var weights = (actions["weights"] ??= new JsonObject()).AsObject();
        foreach (var (id, defaultWeight) in defaults["weights"]!.AsObject())
        {
            if (id is not ("Ringing" or "Smoggy" or "Tender" or "Shrink" or "StrengthDown" or "DexterityDown" or
                "Soulbind" or "DrawDown" or "EnergyDown" or "MindRot" or "WasteAway" or "Sloth" or "Disintegration"))
                continue;
            if (weights[id] is not JsonObject weight)
            {
                weights[id] = defaultWeight!.DeepClone();
                continue;
            }

            double baseWeight = weight["base"]?.GetValue<double>() ?? 1;
            if (baseWeight is -1 or RoomWeight.DisabledBase)
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
