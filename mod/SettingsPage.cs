using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace MonsterIntentRandomizer;

[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen.OnSubmenuOpened))]
internal static class SettingsPage
{
    private const string ButtonName = "MonsterIntentRandomizerSettingsButton";
    private const string WindowName = "MonsterIntentRandomizerSettings";
    private const string QuickWindowName = "MonsterIntentRandomizerQuickSettings";
    private const string ActionValueMultiplier = "actionValueMultiplier";
    private const string Chaos = "chaos";
    private const string ChineseTiming = "单人从下一场战斗生效。联机使用主机建房时的设置。";
    private const string EnglishTiming = "Changes apply next combat in solo play. Co-op uses the host's settings from when the lobby was created.";
    private static bool Chinese => LocManager.Instance.Language is "zhs" or "zht";
    private static string SettingsTitle => Chinese ? "怪物意图随机 · 设置" : "Monster Intent Randomizer · Settings";

    private static void Postfix(NSettingsScreen __instance)
    {
        var panel = __instance.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
        if (panel?.Content is not { } content)
            return;
        if (content.GetNodeOrNull<Button>(ButtonName) is { } existing)
        {
            existing.Text = SettingsTitle;
            return;
        }

        var button = new Button
        {
            Name = ButtonName, Text = SettingsTitle, CustomMinimumSize = new Vector2(0, 52)
        };
        button.AddThemeFontSizeOverride("font_size", 25);
        button.Pressed += () => OpenQuick(__instance);
        content.AddChild(button);
        content.MoveChild(button, 0);
        panel.CallDeferred("RefreshSize");
    }

    private static void OpenQuick(NSettingsScreen screen)
    {
        if (screen.GetNodeOrNull<Window>(WindowName) is { } existing)
        {
            existing.GrabFocus();
            return;
        }
        if (screen.GetNodeOrNull<Window>(QuickWindowName) is { } quick)
        {
            quick.GrabFocus();
            return;
        }

        bool chinese = Chinese;
        var (dialog, body) = CreateDialog(screen, QuickWindowName, new Vector2I(760, 410), new Vector2I(540, 410));
        var description = new Label
        {
            Text = chinese
                ? "本模组按怪物画像随机生成开场状态和招式。提高行动力倍率会增加出招预算。" +
                    "提高混沌会扩大招式波动，并提高部分高级状态的出现机会。滑动条自动保存。" + ChineseTiming
                : "Monsters roll starting effects and moves from their profiles. Higher Action Power raises their move budget. " +
                    "Higher Chaos adds variety and makes some advanced effects more likely. Sliders save automatically. " + EnglishTiming,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        description.AddThemeFontSizeOverride("font_size", 19);
        body.AddChild(description);
        var saveTimer = new Godot.Timer { OneShot = true, WaitTime = 0.3, ProcessMode = Node.ProcessModeEnum.Always };
        dialog.AddChild(saveTimer);
        var pending = new Dictionary<string, double>();
        var pools = ModSettings.Rules.PoolSettings;
        AddSlider(chinese ? "全局行动力倍率" : "Global Action Power", ActionValueMultiplier, 0.01, Math.Max(5, pools.ActionValueMultiplier),
            pools.ActionValueMultiplier, "0.00 ×");
        AddSlider(chinese ? "混沌值" : "Chaos", Chaos, 0, 1, pools.Chaos, "0.00");
        var message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        body.AddChild(message);
        var full = new Button { Text = chinese ? "完整设置" : "Advanced Settings", CustomMinimumSize = new Vector2(0, 45) };
        full.AddThemeFontSizeOverride("font_size", 22);
        body.AddChild(full);

        saveTimer.Timeout += () => SaveChanges();
        dialog.CloseRequested += () =>
        {
            if (SaveChanges())
                dialog.QueueFree();
        };
        full.Pressed += () =>
        {
            if (!SaveChanges())
                return;
            dialog.Hide();
            dialog.QueueFree();
            OpenFull(screen);
        };
        dialog.PopupCenteredClamped(dialog.Size, 0.96f);

        void AddSlider(string title, string key, double minimum, double maximum, double value, string valueFormat)
        {
            var row = new HBoxContainer();
            var label = new Label { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var display = new Label { Text = value.ToString(valueFormat) };
            label.AddThemeFontSizeOverride("font_size", 22);
            display.AddThemeFontSizeOverride("font_size", 22);
            row.AddChild(label);
            row.AddChild(display);
            body.AddChild(row);
            var slider = new HSlider
            {
                Name = key,
                MinValue = minimum,
                MaxValue = maximum,
                Step = 0.01,
                CustomMinimumSize = new Vector2(0, 28)
            };
            slider.SetValueNoSignal(value);
            body.AddChild(slider);
            slider.ValueChanged += changed =>
            {
                display.Text = changed.ToString(valueFormat);
                pending[key] = Math.Round(changed, 2);
                saveTimer.Start();
            };
        }

        bool SaveChanges()
        {
            saveTimer.Stop();
            if (pending.Count == 0)
                return true;
            try
            {
                var current = ModSettings.Current;
                var settings = JsonNode.Parse(current.Settings.GetRawText())!;
                foreach (var (key, value) in pending)
                    settings["pools"]![key] = value;
                ModSettings.Save(current with { Settings = JsonSerializer.SerializeToElement(settings) });
                pending.Clear();
                message.Hide();
                return true;
            }
            catch (Exception error)
            {
                message.Text = chinese ? $"保存失败。{error.Message}" : $"Could not save settings. {error.Message}";
                message.Show();
                return false;
            }
        }
    }

    private static void OpenFull(NSettingsScreen screen)
    {
        var (dialog, body) = CreateDialog(screen, WindowName, new Vector2I(1280, 870), new Vector2I(720, 500));
        dialog.CloseRequested += dialog.QueueFree;

        var enabled = new CheckButton { Text = "启用怪物意图随机", ButtonPressed = ModSettings.Current.Enabled };
        body.AddChild(enabled);
        var intentDetails = new CheckButton { Text = "显示意图的具体效果", ButtonPressed = ModSettings.Current.ShowIntentDetails };
        body.AddChild(intentDetails);
        body.AddChild(new Label { Text = ChineseTiming });
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddChild(tabs);

        var quick = new ScrollContainer
        {
            Name = "常用参数", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        tabs.AddChild(quick);
        var fields = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        fields.AddThemeConstantOverride("separation", 8);
        quick.AddChild(fields);
        var monsterScroll = new ScrollContainer { Name = "逐怪参数", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(monsterScroll);
        var monsters = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        monsters.AddThemeConstantOverride("separation", 8);
        monsterScroll.AddChild(monsters);
        var valuations = new VBoxContainer { Name = "状态估值" };
        tabs.AddChild(valuations);
        var simulation = new VBoxContainer { Name = "模拟" };
        simulation.AddThemeConstantOverride("separation", 8);
        tabs.AddChild(simulation);
        var settings = AddEditor(tabs, "配置 JSON", ModSettings.Current.Settings);
        var profiles = AddEditor(tabs, "画像 JSON", ModSettings.Current.Profiles);

        var message = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 24)
        };
        body.AddChild(message);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        body.AddChild(buttons);
        var defaults = new Button { Text = "载入默认值" };
        var cancel = new Button { Text = "关闭" };
        var save = new Button { Text = "保存" };
        buttons.AddChild(defaults);
        buttons.AddChild(cancel);
        buttons.AddChild(save);

        var quickInputs = new List<(string[] Path, SpinBox Input)>();
        var quickToggles = new List<(string Section, string Key, bool Default, CheckButton Input)>();
        AddNumber("全局行动力倍率", 0.01, 5, 0.01, "pools", ActionValueMultiplier);
        AddNumber("高进阶行动力倍率", 1, 5, 0.01, "pools", "highAscensionActionValueMultiplier");
        AddNumber("混沌值", 0, 1, 0.01, "pools", Chaos);
        AddToggle("实战出招随机跳样", "每轮用系统随机数判定一次。触发时，跳过一个出招随机数。",
            "pools", "externalMoveRandomSkip", defaultValue: true);
        AddNumber("行动力每回合保留比例", 0, 1, 0.01, "pools", "actionValueRetention");
        AddNumber("攻击欲望每回合保留比例", 0, 1, 0.01, "pools", "attackDesireRetention");
        AddNumber("起始行动力比例", 0, 1, 0.01, "pools", "initialActionValueShare");
        AddNumber("余额对出招花费的系数", 0, 5, 0.01, "pools", "balanceTargetWeight");
        AddNumber("抽取预算倍率", 0.01, 5, 0.01, "pools", "drawBudgetMultiplier");
        AddNumber("局部透支占基础行动力比例", 0, 1, 0.01, "pools", "localOverdraftShare");
        AddNumber("非攻击候选数", 1, 20, 1, "pools", "nonAttackCandidates");
        AddNumber("偏好直占比例基础值（0.10 = 10%）", 0, 1, 0.01, "pools", "favoriteDirectBaseShare");
        AddNumber("偏好强度对直占比例的系数", 0, 1, 0.01, "pools", "favoriteDirectStrengthMultiplier");
        AddNumber("无攻击时转给其他行动的比例", 0, 1, 0.01, "pools", "unusedAttackBudgetShare");
        AddNumber("预算达到目标的混沌值", 0.0025, 2.25, 0.0025, "pools", "fullChaosBudgetAt");
        foreach (var (id, label) in new[] { ("attack", "攻击"), ("buff", "强化"), ("defend", "防御"), ("playerDebuff", "减益") })
            AddNumber($"高混沌{label}分配权重", 0, 100, 0.01, "pools", "chaosBudgetWeights", id);
        AddNumber("玩家行动力基础值", 0.001, 100, 0.001, "playerAction", "base");
        AddNumber("玩家行动力房间除数", 0.001, 100, 0.001, "playerAction", "roomsPerStep");
        AddNumber("玩家行动力指数", 0.0001, 5, 0.0001, "playerAction", "exponent");
        AddToggle("按本场玩家平均输出调整玩家行动力（10%）",
            "每个玩家回合结束时更新，本场平均伤害占 10%，房间曲线占 90%。",
            "playerAction", "useCombatDamage", defaultValue: false);
        AddNumber("开场抽取次数", 0, 20, 1, "opening", "attempts");
        AddNumber("单怪开场状态数量", 0, 10, 1, "opening", "maxPerMonster");
        AddNumber("开场净值上限倍率", 0, 3, 0.01, "opening", "maxEncounterValueMultiplier");
        AddNumber("满混沌时的开场强度倍率", 1, 3, 0.01, "pools", "openingStrengthAtMax");
        AddNumber("开场无变化权重", 0, 100, 1, "opening", "weights", "none");
        AddNumber("开场正面权重", 0, 100, 1, "opening", "weights", "positive");
        AddNumber("开场负面权重", 0, 100, 1, "opening", "weights", "negative");
        foreach (var (id, label) in new[] { ("top", "置顶"), ("draw", "洗入抽牌堆"), ("discard", "弃牌堆") })
            AddNumber($"状态牌{label}估值倍率", 0, 5, 0.01, "actions", "cardPlacementValues", id);
        RefreshNumbers();
        var refreshMonsters = AddMonsterControls(monsters, profiles, message);
        var refreshValuations = ValuationPage.Create(valuations, settings, message);
        var refreshSimulation = SimulationPage.Create(simulation, settings, profiles);

        tabs.TabChanged += index =>
        {
            if (index == 0)
                RefreshNumbers();
            if (index == 1)
                refreshMonsters();
            if (index == 2)
                refreshValuations();
            if (index == 3)
                refreshSimulation();
        };
        defaults.Pressed += () =>
        {
            enabled.ButtonPressed = ModSettings.Defaults.Enabled;
            intentDetails.ButtonPressed = ModSettings.Defaults.ShowIntentDetails;
            settings.Text = ModSettings.Format(ModSettings.Defaults.Settings);
            profiles.Text = ModSettings.Format(ModSettings.Defaults.Profiles);
            RefreshNumbers();
            refreshMonsters();
            refreshValuations();
            refreshSimulation();
            message.Text = "已载入默认值。点击保存后生效。";
        };
        cancel.Pressed += dialog.QueueFree;
        save.Pressed += () =>
        {
            try
            {
                using var settingsJson = JsonDocument.Parse(settings.Text);
                using var profilesJson = JsonDocument.Parse(profiles.Text);
                bool refreshIntent = intentDetails.ButtonPressed != ModSettings.Current.ShowIntentDetails;
                ModSettings.Save(new StoredSettings(enabled.ButtonPressed,
                    settingsJson.RootElement.Clone(), profilesJson.RootElement.Clone(), ShowIntentDetails: intentDetails.ButtonPressed));
                if (refreshIntent)
                    IntentDetails.Refresh();
                message.Text = "已保存。";
            }
            catch (Exception error)
            {
                message.Text = $"保存失败。{error.Message}";
            }
        };
        dialog.PopupCenteredClamped(dialog.Size, 0.96f);

        void AddNumber(string label, double min, double max, double step, params string[] path)
        {
            var input = NumberRow(fields, label, min, max, step);
            quickInputs.Add((path, input));
            input.ValueChanged += value =>
            {
                try
                {
                    var json = JsonNode.Parse(settings.Text)!;
                    var owner = json;
                    foreach (string key in path[..^1])
                        owner = owner[key]!;
                    if (step == 1)
                        owner[path[^1]] = (int)Math.Round(value);
                    else
                        owner[path[^1]] = value;
                    settings.Text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    message.Text = "";
                }
                catch (Exception error)
                {
                    message.Text = $"无法同步全局参数。{error.Message}";
                }
            };
        }

        void AddToggle(string label, string tooltip, string section, string key, bool defaultValue)
        {
            var input = new CheckButton { Text = label, TooltipText = tooltip };
            fields.AddChild(input);
            quickToggles.Add((section, key, defaultValue, input));
            input.Toggled += selected =>
            {
                try
                {
                    var json = JsonNode.Parse(settings.Text)!.AsObject();
                    var owner = json[section] ??= new JsonObject();
                    owner[key] = selected;
                    settings.Text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    message.Text = "";
                }
                catch (Exception error)
                {
                    message.Text = $"无法同步全局参数。{error.Message}";
                }
            };
        }

        void RefreshNumbers()
        {
            try
            {
                using var json = JsonDocument.Parse(settings.Text);
                bool isObject = json.RootElement.ValueKind == JsonValueKind.Object;
                foreach (var (section, key, defaultValue, input) in quickToggles)
                {
                    input.Disabled = !isObject;
                    bool selected = isObject && json.RootElement.TryGetProperty(section, out var owner) &&
                        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var option)
                        ? option.ValueKind == JsonValueKind.True : defaultValue;
                    input.SetPressedNoSignal(isObject && selected);
                }
                foreach (var (path, input) in quickInputs)
                {
                    var current = json.RootElement;
                    foreach (string key in path)
                    {
                        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                            break;
                    }
                    bool present = current.ValueKind == JsonValueKind.Number;
                    input.Editable = present;
                    input.SetValueNoSignal(present ? current.GetDouble() : 0);
                }
            }
            catch (JsonException)
            {
                foreach (var (_, _, _, input) in quickToggles)
                    input.Disabled = true;
                foreach (var (_, input) in quickInputs)
                    input.Editable = false;
                message.Text = "请先修复配置 JSON。";
            }
        }
    }

    private static (Window Dialog, VBoxContainer Body) CreateDialog(NSettingsScreen screen, string name,
        Vector2I size, Vector2I minimumSize)
    {
        var dialog = new Window
        {
            Name = name, Title = SettingsTitle, Size = size, MinSize = minimumSize,
            Transient = true, Exclusive = true, Visible = false
        };
        screen.AddChild(dialog);
        var panel = new PanelContainer();
        dialog.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var inset = new MarginContainer();
        foreach (string edge in new[] { "left", "right", "top", "bottom" })
            inset.AddThemeConstantOverride($"margin_{edge}", 18);
        panel.AddChild(inset);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        inset.AddChild(body);
        return (dialog, body);
    }

    private static Action AddMonsterControls(VBoxContainer panel, TextEdit profiles, Label message)
    {
        var list = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddChild(list);
        var favoriteHeader = new Label { Text = "行动偏好" };
        favoriteHeader.AddThemeFontSizeOverride("font_size", 22);
        panel.AddChild(favoriteHeader);
        var favoritePicker = new HBoxContainer();
        var effects = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var addFavorite = new Button { Text = "添加偏好" };
        favoritePicker.AddChild(effects);
        favoritePicker.AddChild(addFavorite);
        panel.AddChild(favoritePicker);
        var favorites = new VBoxContainer();
        panel.AddChild(favorites);
        var availability = new Label();
        panel.AddChild(availability);
        var randomize = new CheckButton { Text = "启用该怪物的随机出招" };
        panel.AddChild(randomize);
        var keptMoves = new LineEdit { PlaceholderText = "例如 DAMPEN_MOVE, HEX" };
        panel.AddChild(new Label { Text = "保留的原版招式 ID（逗号分隔）" });
        panel.AddChild(keptMoves);
        var values = new[]
        {
            (Group: "", Key: "actionValue", Label: "基础行动力"),
            (Group: "", Key: "attackFrequency", Label: "每回合目标攻击段数"),
            (Group: "", Key: "singleHitPreference", Label: "单段攻击偏好倍率"),
            (Group: "", Key: "desireHitPreference", Label: "欲望段数偏好倍率"),
            (Group: "", Key: "volatility", Label: "行动力波动"),
            (Group: "preferences", Key: "attack", Label: "纯攻击权重"),
            (Group: "preferences", Key: "buff", Label: "强化权重"),
            (Group: "preferences", Key: "defend", Label: "防御权重"),
            (Group: "preferences", Key: "playerDebuff", Label: "减益权重")
        };
        var inputs = new List<SpinBox>();
        var entries = new List<(string MonsterId, string? PhaseId)>();
        (string MonsterId, string? PhaseId)? selected = null;
        foreach (var (group, key, label) in values)
        {
            var input = NumberRow(panel, label, key == "actionValue" ? 0.01 : 0, 100, 0.01);
            inputs.Add(input);
            input.ValueChanged += value => EditMonster((monster, parent) =>
            {
                if (group.Length == 0)
                    monster[key] = value;
                else
                {
                    monster[group] ??= parent[group]!.DeepClone();
                    monster[group]![key] = value;
                }
            });
        }
        addFavorite.Pressed += () =>
        {
            string key = ((EffectKind)effects.GetSelectedId()).ToString();
            EditMonster((monster, parent) => EditableFavorites(monster, parent)[key] ??= JsonValue.Create(3.0), refresh: true);
        };
        randomize.Toggled += value => EditMonster((monster, _) => monster["enabled"] = value, refresh: true);
        keptMoves.TextChanged += value => EditMonster((_, parent) =>
        {
            var moves = value.Split([',', '、', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.ToUpperInvariant()).Distinct().ToArray();
            if (moves.Length == 0)
                parent.AsObject().Remove("keepMoves");
            else
                parent["keepMoves"] = JsonSerializer.SerializeToNode(moves);
        });
        list.ItemSelected += i =>
        {
            selected = entries[(int)i];
            RefreshValues(JsonNode.Parse(profiles.Text)!);
        };

        return () =>
        {
            try
            {
                var document = JsonNode.Parse(profiles.Text)!;
                var monsters = document["monsters"]!.AsObject();
                entries.Clear();
                list.Clear();
                randomize.Disabled = true;
                keptMoves.Editable = false;
                foreach (var (id, monster) in monsters.OrderBy(pair => pair.Value?["name"]?.GetValue<string>()))
                {
                    string name = monster?["name"]?.GetValue<string>() ?? id;
                    if (monster?["phases"] is JsonObject { Count: > 0 } phases)
                        foreach (var (phaseId, phase) in phases)
                        {
                            entries.Add((id, phaseId));
                            list.AddItem($"{name}（{phase?["name"]?.GetValue<string>() ?? phaseId}）  ·  {id}");
                        }
                    else
                    {
                        entries.Add((id, null));
                        list.AddItem($"{name}  ·  {id}");
                    }
                }
                int index = selected is { } current ? Math.Max(0, entries.IndexOf(current)) : 0;
                selected = entries.Count > 0 ? entries[index] : null;
                if (selected != null)
                    list.Select(index);
                RefreshValues(document);
            }
            catch (Exception)
            {
                list.Clear();
                RefreshFavorites(null, false, null);
                foreach (var input in inputs)
                    input.Editable = false;
                availability.Text = "请先修复画像 JSON。";
            }
        };

        void RefreshValues(JsonNode document)
        {
            var (parent, monster) = SelectedNodes(document);
            bool isPhase = selected?.PhaseId != null;
            bool parentDisabled = isPhase && parent?["enabled"]?.GetValue<bool>() == false;
            bool active = monster != null && !parentDisabled && monster["enabled"]?.GetValue<bool>() != false;
            randomize.Text = isPhase ? "启用该阶段的随机出招" : "启用该怪物的随机出招";
            randomize.Disabled = monster == null || parentDisabled;
            randomize.SetPressedNoSignal(active);
            keptMoves.Editable = parent != null;
            keptMoves.Text = parent?["keepMoves"] is JsonArray kept
                ? string.Join(", ", kept.Select(move => move!.GetValue<string>())) : "";
            var allowed = monster?["allowedBuffs"] is JsonArray buffs
                ? RuleConfiguration.Read<EffectKind[]>(JsonSerializer.SerializeToElement(buffs)) : null;
            availability.Text = parentDisabled ? "画像 JSON 中的总开关已关闭" :
                allowed != null ? $"可随机强化　{string.Join("、", allowed.Select(kind => IntentDetails.Names(kind).Chinese))}" : "";
            for (int i = 0; i < values.Length; i++)
            {
                var (group, key, _) = values[i];
                var value = group.Length == 0 ? monster?[key] ?? parent?[key] ?? document["defaults"]?[key] :
                    (monster?[group] ?? parent?[group])?[key];
                if (value == null && key is "singleHitPreference" or "desireHitPreference")
                    value = JsonValue.Create(1.0);
                bool present = value is JsonValue number && number.TryGetValue<double>(out _);
                inputs[i].Editable = present && active;
                inputs[i].SetValueNoSignal(present ? value!.GetValue<double>() : 0);
            }
            RefreshFavorites((monster?["favorites"] ?? parent?["favorites"]) as JsonObject, active, allowed);
        }

        void RefreshFavorites(JsonObject? configured, bool active, EffectKind[]? allowed)
        {
            effects.Clear();
            foreach (var child in favorites.GetChildren())
            {
                favorites.RemoveChild(child);
                child.QueueFree();
            }

            foreach (var (key, value) in configured?.AsEnumerable() ?? [])
            {
                string label = Enum.TryParse(key, true, out EffectKind kind) ? IntentDetails.Names(kind).Chinese : key;
                var row = new HBoxContainer();
                favorites.AddChild(row);
                var input = NumberRow(row, label, 0, 100, 0.01);
                ((Control)input.GetParent()).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                input.Editable = active;
                input.SetValueNoSignal(value?.GetValue<double>() ?? 0);
                input.ValueChanged += weight => EditMonster((monster, parent) => EditableFavorites(monster, parent)[key] = weight);
                var remove = new Button { Text = "移除", Disabled = !active };
                remove.Pressed += () => EditMonster((monster, parent) => EditableFavorites(monster, parent).Remove(key), refresh: true);
                row.AddChild(remove);
            }

            foreach (var kind in Enum.GetValues<EffectKind>().OrderBy(kind => IntentDetails.Names(kind).Chinese))
                if (ActionCatalog.CanFavor(kind) &&
                    EffectPolicy.CanTargetMonster(kind, selected?.MonsterId) &&
                    (allowed == null || !ActionEffect.IsMonsterBuff(kind) || allowed.Contains(kind)) &&
                    configured?.Any(pair => string.Equals(pair.Key, kind.ToString(), StringComparison.OrdinalIgnoreCase)) != true)
                    effects.AddItem(IntentDetails.Names(kind).Chinese, (int)kind);
            if (effects.ItemCount > 0)
                effects.Select(0);
            effects.Disabled = !active || effects.ItemCount == 0;
            effects.TooltipText = "选中招式后点击添加。数值增加该招式的共享权重和直占权重。";
            addFavorite.Disabled = effects.Disabled;
        }

        (JsonNode? Parent, JsonNode? Profile) SelectedNodes(JsonNode document)
        {
            if (selected is not { } entry)
                return (null, null);
            var parent = document["monsters"]?[entry.MonsterId];
            return (parent, entry.PhaseId is { } phase ? parent?["phases"]?[phase] : parent);
        }

        static JsonObject EditableFavorites(JsonNode monster, JsonNode parent) =>
            (monster["favorites"] ??= parent["favorites"]?.DeepClone() ?? new JsonObject()).AsObject();

        void EditMonster(Action<JsonNode, JsonNode> edit, bool refresh = false)
        {
            try
            {
                var document = JsonNode.Parse(profiles.Text)!;
                var (parent, monster) = SelectedNodes(document);
                if (parent == null || monster == null)
                    return;
                edit(monster, parent);
                profiles.Text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                if (refresh)
                    RefreshValues(document);
                message.Text = "";
            }
            catch (Exception error)
            {
                message.Text = $"无法同步怪物画像。{error.Message}";
            }
        }
    }

    internal static SpinBox NumberRow(BoxContainer container, string label, double min, double max, double step)
    {
        var row = new HBoxContainer();
        var caption = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var input = new SpinBox
        {
            MinValue = min, MaxValue = max, Step = step, AllowGreater = true,
            CustomMinimumSize = new Vector2(145, 34), UpdateOnTextChanged = true
        };
        row.AddChild(caption);
        row.AddChild(input);
        container.AddChild(row);
        return input;
    }

    private static TextEdit AddEditor(TabContainer tabs, string title, JsonElement value)
    {
        var editor = new TextEdit { Name = title, Text = ModSettings.Format(value) };
        editor.AddThemeFontSizeOverride("font_size", 17);
        tabs.AddChild(editor);
        return editor;
    }
}
