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
    private const int SettingsRevision = 12;
    private static readonly HashSet<string> RetiredOpeningStates = ["DEXTERITY", "DEXTERITY_DOWN", "FRAIL", "FLUTTER"];
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
        var existingCardPrices = (settings["actions"]!["statusCardValues"] as JsonObject)?
            .Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
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
        UpgradeRoomRules(settings, defaults, stored.Revision);
        defaults["actions"]!.AsObject().Remove("valuation");
        FillMissing(settings.AsObject(), defaults.AsObject());
        UpgradeDefenseAmounts(settings, defaults);
        UpgradeProgressValuation(settings);
        ApplyEffectCalibration(settings);
        UpgradeStatusCardPrices(settings, existingCardPrices);
        UpgradeOpeningAmounts(settings, defaults);
        UpgradeSharedBuffs(settings);

        var profiles = JsonNode.Parse(stored.Profiles.GetRawText())!;
        RemoveRetiredOpeningStates(profiles["defaults"]);
        (profiles["defaults"] as JsonObject)?.Remove("survivalTurns");
        foreach (var (_, profile) in profiles["monsters"]!.AsObject())
        {
            RemoveRetiredOpeningStates(profile);
            (profile as JsonObject)?.Remove("survivalTurns");
        }
        if (stored.Revision < 5)
            ApplyActionCalibration(profiles);
        UpgradeMixedProfiles(profiles);
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
        foreach (string section in new[] { "valuation", "maxAmounts", "minActs" })
            (settings["actions"]?[section] as JsonObject)?.Remove("PlayerDoom");
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

    private static void UpgradeRoomRules(JsonNode settings, JsonNode defaults, int revision)
    {
        var actions = settings["actions"]!.AsObject();
        if (actions["amounts"] is JsonObject oldAmounts && actions["maxAmounts"] is not JsonObject)
        {
            var maximums = new JsonObject();
            foreach (var (id, amounts) in oldAmounts)
            {
                int max = amounts!.AsArray().Select(amount => amount!.GetValue<int>()).DefaultIfEmpty(0).Max();
                int oldDefault = id is "Strength" or "Ritual" ? 1 : 8;
                if ((id is "Strength" or "Ritual" or "Block" or "Heal") && max == oldDefault)
                    continue;
                maximums[id] = max;
            }
            actions["maxAmounts"] = maximums;
        }
        foreach (string key in new[] { "amounts", "maxAddedStrength", "maxAddedRitual", "maxInsertedCards",
                     "playerDebuffCooldownTurns", "pureBlockCooldownTurns" })
            actions.Remove(key);
        settings.AsObject().Remove("forecast");

        var defaultStates = defaults["opening"]!["states"]!.AsObject();
        foreach (var (id, node) in settings["opening"]!["states"]!.AsObject())
        {
            var state = node!.AsObject();
            var defaultState = defaultStates[id] as JsonObject;
            int amount = state["amount"]?.GetValue<int>() ?? (id == "STRENGTH_DOWN" ? -1 : 1);
            if (state["maxAmount"] == null && state["amount"] != null)
            {
                var oldDoses = state["amounts"] as JsonArray;
                state["maxAmount"] = id == "DOOM" && revision < 2 && JsonNode.DeepEquals(oldDoses, JsonNode.Parse("[2,3,5,7]"))
                    ? defaultState!["maxAmount"]!.GetValue<int>()
                    : oldDoses?.Select(dose => Math.Abs(dose!.GetValue<int>())).DefaultIfEmpty(0).Max() ?? Math.Abs(amount);
            }
            if (state["formula"] == null && state["turns"]?.GetValue<double>() is double coefficient)
            {
                string factor = state["basis"]?.GetValue<string>().ToLowerInvariant() switch
                {
                    "flat" => "amount", "attackfrequency" => "amount*attackFrequency",
                    "survivalturns" => "amount*survivalTurns",
                    "attacksoverlifetime" => "amount*attackFrequency*survivalTurns",
                    _ => "amount*actionValue"
                };
                var defaultFormula = defaultState?["formula"];
                if (defaultFormula == null && Enum.TryParse<EffectKind>(id.Replace("_", ""), true, out var kind))
                    defaultFormula = defaults["buffs"]?[kind.ToString()]?["formula"];
                state["formula"] = revision == 0 && OldOpeningCoefficients.TryGetValue(id, out var previous) &&
                    coefficient == previous && defaultFormula != null
                        ? defaultFormula.DeepClone() : new JsonObject { [factor] = coefficient };
            }
            if (state["formula"] != null)
                defaultState?.Remove("formula");
            state.Remove("amounts");
            state.Remove("turns");
            state.Remove("basis");
            if (amount < 0)
                state["amount"] = -1;
            else
                state.Remove("amount");
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

    private static void UpgradeDefenseAmounts(JsonNode settings, JsonNode defaults)
    {
        var actions = settings["actions"]!;
        UpgradeMaximum(actions["maxAmounts"]!.AsObject(), "Regen", 4,
            8, actions["minAmounts"]?["Regen"]?.GetValue<int>() ?? 5);
        foreach (var (id, buffId) in new[] { ("REGEN", "Regen"), ("PLATING", "Plating") })
        {
            var fallback = defaults["buffs"]![buffId]!;
            if (settings["opening"]!["states"]![id] is JsonObject state)
                UpgradeMaximum(state, "maxAmount", 4, fallback["maxAmount"]!.GetValue<int>(),
                    state["minAmount"]?.GetValue<int>() ?? fallback["minAmount"]!.GetValue<int>());
        }

        static void UpgradeMaximum(JsonObject owner, string key, int oldMaximum, int defaultMaximum, int minimum)
        {
            if (owner[key]?.GetValue<int>() is int previous && previous > 0)
                owner[key] = previous == oldMaximum ? Math.Max(defaultMaximum, minimum) : Math.Max(previous, minimum);
        }
    }

    private static void UpgradeProgressValuation(JsonNode settings)
    {
        var actions = settings["actions"]!.AsObject();
        actions.Remove("playerTurnValue");
        foreach (var (_, formula) in actions["valuation"]!.AsObject())
            ReplaceLegacyFactor(formula!.AsObject());
        foreach (var (_, state) in settings["opening"]!["states"]!.AsObject())
            if (state?["formula"] is JsonObject formula)
                ReplaceLegacyFactor(formula);

        var placement = actions["cardPlacementValues"]!.AsObject();
        if (placement["top"]?.GetValue<double>() == 1.4)
            placement["top"] = 1.33;
        if (placement["discard"]?.GetValue<double>() == 0.6)
            placement["discard"] = 0.75;
        foreach (var (id, slope) in new[] { ("StrengthDown", 0.1), ("DexterityDown", 0.08) })
        {
            var formula = actions["valuation"]![id]!.AsObject();
            if (!formula.Any(term => term.Key.Contains("roomsEntered", StringComparison.OrdinalIgnoreCase)))
                formula["amount*roomsEntered"] = slope;
        }

        static void ReplaceLegacyFactor(JsonObject formula)
        {
            foreach (var (key, value) in formula.Where(term => term.Key.Split('*', StringSplitOptions.TrimEntries)
                         .Contains("playerTurnValue", StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                string next = string.Join("*", key.Split('*', StringSplitOptions.TrimEntries)
                    .Select(factor => factor.Equals("playerTurnValue", StringComparison.OrdinalIgnoreCase) ? "playerActionValue" : factor));
                var factors = next.Split('*').Order(StringComparer.OrdinalIgnoreCase);
                string? existing = formula.Select(term => term.Key).FirstOrDefault(candidate =>
                    candidate.Split('*', StringSplitOptions.TrimEntries).Order(StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(factors, StringComparer.OrdinalIgnoreCase));
                double coefficient = value!.GetValue<double>() + (existing == null ? 0 : formula[existing]!.GetValue<double>());
                formula.Remove(key);
                formula[existing ?? next] = coefficient;
            }
        }
    }

    private static void ApplyEffectCalibration(JsonNode settings)
    {
        var changes = ReadResource("effect-valuation-tuning.json").GetProperty("changes");
        foreach (var change in changes.EnumerateArray())
        {
            string id = change.GetProperty("id").GetString()!;
            bool action = change.GetProperty("scope").GetString() == "action";
            var owner = action ? settings["actions"]!["valuation"] : settings["opening"]!["states"]![id];
            string key = action ? id : "formula";
            if (owner?[key] is not JsonObject current)
                continue;
            var previous = change.GetProperty("before").EnumerateObject().ToArray();
            if (previous.Length == current.Count && previous.All(term => current.Any(entry =>
                    Normalize(entry.Key) == Normalize(term.Name) &&
                    Math.Abs(entry.Value!.GetValue<double>() - term.Value.GetDouble()) < 1e-9)))
                owner[key] = JsonNode.Parse(change.GetProperty("after").GetRawText());
        }

        static string Normalize(string term) => string.Join("*", term.ToLowerInvariant()
            .Split('*', StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
    }

    private static void UpgradeStatusCardPrices(JsonNode settings, HashSet<string> existingPrices)
    {
        var actions = settings["actions"]!.AsObject();
        var formulas = actions["valuation"]!.AsObject();
        var prices = actions["statusCardValues"]!.AsObject();
        foreach (var (id, node) in formulas.ToArray())
        {
            if (Enum.TryParse<EffectKind>(id, ignoreCase: true, out var kind) && ActionEffect.IsCard(kind))
            {
                var old = node!.AsObject();
                if (!existingPrices.Contains(id) && old.All(term => term.Key.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                                                                     term.Key.Equals("fixed", StringComparison.OrdinalIgnoreCase)))
                    prices[kind.ToString()] = Math.Max(0, old.Sum(term => term.Value!.GetValue<double>()));
                formulas.Remove(id);
            }
            else
                RemoveObsoleteTerms(node as JsonObject);
        }
        foreach (var (_, state) in settings["opening"]!["states"]!.AsObject())
            RemoveObsoleteTerms(state?["formula"] as JsonObject);

        actions.Remove("handBurnDamageChance");
        actions.Remove("playableStatusRetention");
        var context = settings["simulation"]!["turnContext"]!.AsObject();
        foreach (string key in new[] { "dazedDraws", "woundDraws", "burnDraws", "beckonDraws", "otherStatusDraws" })
            context.Remove(key);

        static void RemoveObsoleteTerms(JsonObject? formula)
        {
            if (formula == null)
                return;
            foreach (string key in formula.Select(term => term.Key).Where(key =>
                key.Split('*', StringSplitOptions.TrimEntries).Any(factor =>
                    factor.Equals("draws", StringComparison.OrdinalIgnoreCase) ||
                    factor.Equals("triggers", StringComparison.OrdinalIgnoreCase))).ToArray())
                formula.Remove(key);
        }
    }

    private static void UpgradeOpeningAmounts(JsonNode settings, JsonNode defaults)
    {
        var states = settings["opening"]!["states"]!;
        var newStates = defaults["opening"]!["states"]!;
        foreach (var (id, oldAmount) in new Dictionary<string, int>
        {
            ["WEAK"] = 3, ["VULNERABLE"] = 3, ["DEMISE"] = 2, ["DOOM"] = 14
        })
            if (states[id]?["maxAmount"]?.GetValue<int>() == oldAmount)
                states[id]!["maxAmount"] = newStates[id]!["maxAmount"]!.DeepClone();
    }

    private static void UpgradeSharedBuffs(JsonNode settings)
    {
        var actions = settings["actions"]!;
        var opening = settings["opening"]!["states"]!.AsObject();
        foreach (var (id, buff) in settings["buffs"]!.AsObject())
        {
            string openingId = EffectPolicy.Id(Enum.Parse<EffectKind>(id));
            var previous = opening[openingId];
            var oldWeight = actions["weights"]?[id];
            var oldPolicy = actions["policies"]?[id];
            var shared = buff!.AsObject();
            Copy("formula", actions["valuation"]?[id] ?? previous?["formula"]);
            Copy("minAmount", actions["minAmounts"]?[id] ?? previous?["minAmount"]);
            Copy("hpPerSurvivalTurnCap", actions["hpPerSurvivalTurnCaps"]?[id] ?? previous?["hpPerSurvivalTurnCap"]);
            Copy("stackable", oldPolicy?["stackable"] ?? previous?["stackable"]);
            Copy("unique", oldPolicy?["unique"] ?? previous?["unique"]);
            if (previous?["common"]?.GetValue<bool>() == true)
                shared["common"] = true;
            if (shared["maxAmount"] != null)
            {
                int? actionCap = actions["maxAmounts"]?[id]?.GetValue<int>();
                int? openingCap = previous?["maxAmount"]?.GetValue<int>();
                if (actionCap.HasValue || openingCap.HasValue)
                {
                    int cap = Math.Max(actionCap ?? 0, openingCap ?? 0);
                    shared["maxAmount"] = cap == 0 ? 0 : Math.Max(cap, shared["minAmount"]?.GetValue<int>() ?? 1);
                }
            }

            double? oldBase = oldWeight?["base"]?.GetValue<double>();
            double? openingBase = previous?["weight"]?.GetValue<double>();
            double oldSlope = oldWeight?["perRoom"]?.GetValue<double>() ?? previous?["weightPerRoom"]?.GetValue<double>() ?? 0;
            if (oldBase == -1 || openingBase == -1)
                shared["weight"] = -1;
            else if ((oldBase ?? openingBase) is double weight)
            {
                double slope = shared["weightPerRoom"]?.GetValue<double>() ?? 0;
                double lateWeight = shared["weight"]!.GetValue<double>() + 30 * slope;
                if (oldSlope == 0 && slope > 0 && weight > 0 && lateWeight > 0)
                {
                    double scale = weight / lateWeight;
                    shared["weight"] = shared["weight"]!.GetValue<double>() * scale;
                    shared["weightPerRoom"] = slope * scale;
                }
                else
                {
                    shared["weight"] = weight;
                    shared["weightPerRoom"] = oldSlope;
                }
            }

            foreach (string section in new[] { "valuation", "minAmounts", "maxAmounts", "hpPerSurvivalTurnCaps", "weights", "policies", "minActs" })
                (actions[section] as JsonObject)?.Remove(id);
            opening.Remove(openingId);

            void Copy(string key, JsonNode? value)
            {
                if (value != null)
                    shared[key] = value.DeepClone();
            }
        }
        foreach (string id in RetiredOpeningStates)
            opening.Remove(id);
    }

    private static void RemoveRetiredOpeningStates(JsonNode? profile)
    {
        if (profile?["openingStates"] is JsonArray pool)
            foreach (var state in pool.Where(state => state != null && RetiredOpeningStates.Contains(state.GetValue<string>())).ToArray())
                pool.Remove(state);
    }

    private static void UpgradeMixedProfiles(JsonNode profiles)
    {
        var reviewed = ReadResource("mixed-monster-actions.json").GetProperty("monsters");
        var defaults = JsonNode.Parse(Defaults.Profiles.GetRawText())!["monsters"]!;
        var saved = profiles["monsters"]!;
        var exclusions = profiles["keepVanilla"] as JsonArray;
        foreach (var entry in reviewed.EnumerateObject())
        {
            string id = entry.Name;
            if (saved[id] is not JsonObject monster || defaults[id] is not JsonObject defaultProfile)
                continue;
            if (exclusions != null)
                foreach (var exclusion in exclusions.Where(value => value?.GetValue<string>() == id).ToArray())
                    exclusions.Remove(exclusion);

            if (monster["randomMoves"] != null)
                continue;
            monster["randomMoves"] = defaultProfile["randomMoves"]!.DeepClone();
            foreach (var field in entry.Value.GetProperty("previous").EnumerateObject())
                if (defaultProfile[field.Name] is { } next &&
                    JsonNode.DeepEquals(monster[field.Name], JsonNode.Parse(field.Value.GetRawText())))
                    monster[field.Name] = next.DeepClone();
        }
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
            if (!NativeActions.SupportsOpening(id) || state.MaxAmount is < 0 ||
                state.MinAmount < 1 || state.HpPerSurvivalTurnCap is < 0 ||
                (state.MaxAmount > 0 && state.MaxAmount < state.MinAmount) ||
                Math.Sign(state.Amount) != (id == "STRENGTH_DOWN" ? -1 : 1) ||
                (id == "STUN" && (state.MinAmount != 1 || state.MaxAmount > 1 || state.HpPerSurvivalTurnCap.HasValue)) ||
                state.MinAct < 1)
                throw new ArgumentException($"{id} 的开场层数或幕数无效。");
        }

        foreach (var (id, monster) in rules.Monsters)
        {
            if (monster.Profile.ActionValue <= 0 || monster.DeadlyEnemies is <= 0 ||
                monster.Profile.AttackFrequency < 0 || monster.Profile.AttackSpend is < 0 or > 1 ||
                monster.Profile.SingleHitPreference < 0 || monster.Profile.DesireHitPreference < 0 ||
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
