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
    private const int SettingsRevision = 26;
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

        var profiles = JsonNode.Parse(stored.Profiles.GetRawText())!;
        var defaultProfiles = JsonNode.Parse(Defaults.Profiles.GetRawText())!["monsters"]!.AsObject();
        UpgradePhaseProfiles(profiles, defaultProfiles);
        if (stored.Revision == 25)
            return stored with { Profiles = JsonSerializer.SerializeToElement(profiles), Revision = SettingsRevision };

        var settings = JsonNode.Parse(stored.Settings.GetRawText())!;
        var actions = settings["actions"]!.AsObject();
        var defaultSettings = JsonNode.Parse(Defaults.Settings.GetRawText())!;
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

    private static void UpgradePhaseProfiles(JsonNode profiles, JsonObject defaults)
    {
        var monsters = profiles["monsters"]!.AsObject();
        if (monsters["CEREMONIAL_BEAST"]?["phases"]?["rush"] is JsonObject rush)
            rush["allowedBuffs"] ??= defaults["CEREMONIAL_BEAST"]!["phases"]!["rush"]!["allowedBuffs"]!.DeepClone();

        var added = new[] { "CRUSHER", "LIVING_SHIELD", "PHANTASMAL_GARDENER", "SNEAKY_GREMLIN", "THIEVING_HOPPER" };
        var excluded = profiles["keepVanilla"] as JsonArray;
        foreach (string id in added)
        {
            if (monsters[id] is JsonObject profile)
                profile["randomMoves"] ??= defaults[id]!["randomMoves"]!.DeepClone();
            else
                monsters[id] = defaults[id]!.DeepClone();

            var previous = excluded?.FirstOrDefault(entry => entry?.GetValue<string>() == id);
            if (previous != null)
                excluded!.Remove(previous);
        }
    }

    private static void UpgradeMonsterCoverage(JsonNode profiles, JsonObject defaults)
    {
        var added = new HashSet<string>(["PHROG_PARASITE", "WRIGGLER", "DECIMILLIPEDE_SEGMENT",
            "DECIMILLIPEDE_SEGMENT_FRONT", "DECIMILLIPEDE_SEGMENT_MIDDLE", "DECIMILLIPEDE_SEGMENT_BACK",
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
                beast["randomMoves"] = definition["randomMoves"]!.DeepClone();
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
            profile["randomMoves"] = definition["randomMoves"]!.DeepClone();
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
