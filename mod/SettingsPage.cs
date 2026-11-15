using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace MonsterIntentRandomizer;

[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen.OnSubmenuOpened))]
internal static class SettingsPage
{
    private const string ButtonName = "MonsterIntentRandomizerSettingsButton";
    private const string WindowName = "MonsterIntentRandomizerSettings";

    private static void Postfix(NSettingsScreen __instance)
    {
        var panel = __instance.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
        if (panel?.Content is not { } content || content.GetNodeOrNull<Button>(ButtonName) != null)
            return;

        var button = new Button
        {
            Name = ButtonName, Text = "怪物意图随机 · 设置", CustomMinimumSize = new Vector2(0, 52)
        };
        button.AddThemeFontSizeOverride("font_size", 25);
        button.Pressed += () => Open(__instance);
        content.AddChild(button);
        content.MoveChild(button, 0);
        panel.CallDeferred("RefreshSize");
    }

    private static void Open(NSettingsScreen screen)
    {
        if (screen.GetNodeOrNull<Window>(WindowName) is { } existing)
        {
            existing.GrabFocus();
            return;
        }

        var dialog = new Window
        {
            Name = WindowName, Title = "怪物意图随机 · 设置", Size = new Vector2I(900, 670),
            MinSize = new Vector2I(600, 430), Transient = true, Exclusive = true, Visible = false
        };
        screen.AddChild(dialog);
        dialog.CloseRequested += dialog.QueueFree;

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

        var enabled = new CheckButton { Text = "启用单人怪物意图随机", ButtonPressed = ModSettings.Current.Enabled };
        body.AddChild(enabled);
        var intentDetails = new CheckButton { Text = "显示意图的具体效果", ButtonPressed = ModSettings.Current.ShowIntentDetails };
        body.AddChild(intentDetails);
        body.AddChild(new Label { Text = "战斗参数从下一场战斗生效。" });
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
        var settings = AddEditor(tabs, "全局参数", ModSettings.Current.Settings);
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
        AddNumber("行动力每回合保留比例", 0, 1, 0.01, "pools", "actionValueRetention");
        AddNumber("攻击欲望每回合保留比例", 0, 1, 0.01, "pools", "attackDesireRetention");
        AddNumber("起始行动力比例", 0, 1, 0.01, "pools", "initialActionValueShare");
        AddNumber("开场抽取次数", 0, 20, 1, "opening", "attempts");
        AddNumber("单怪开场状态数量", 0, 10, 1, "opening", "maxPerMonster");
        AddNumber("开场净值上限倍率", 0, 3, 0.01, "opening", "maxEncounterValueMultiplier");
        AddNumber("开场无变化权重", 0, 100, 1, "opening", "weights", "none");
        AddNumber("开场正面权重", 0, 100, 1, "opening", "weights", "positive");
        AddNumber("开场负面权重", 0, 100, 1, "opening", "weights", "negative");
        AddNumber("力量估值系数", 0, 10, 0.01, "actions", "valuation", "Strength", "amount*attackFrequency");
        AddNumber("仪式估值系数", 0, 10, 0.01, "actions", "valuation", "Ritual", "amount*attackFrequency*survivalTurns");
        AddNumber("晕眩牌每次出现的估值", 0, 10, 0.01, "actions", "valuation", "Dazed", "amount*draws");
        RefreshNumbers();
        var refreshMonsters = AddMonsterControls(monsters, profiles, message);

        tabs.TabChanged += index =>
        {
            if (index == 0)
                RefreshNumbers();
            if (index == 1)
                refreshMonsters();
        };
        defaults.Pressed += () =>
        {
            enabled.ButtonPressed = ModSettings.Defaults.Enabled;
            intentDetails.ButtonPressed = ModSettings.Defaults.ShowIntentDetails;
            settings.Text = ModSettings.Format(ModSettings.Defaults.Settings);
            profiles.Text = ModSettings.Format(ModSettings.Defaults.Profiles);
            RefreshNumbers();
            refreshMonsters();
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
        dialog.PopupCenteredClamped(dialog.Size, 0.88f);

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

        void RefreshNumbers()
        {
            try
            {
                using var json = JsonDocument.Parse(settings.Text);
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
                foreach (var (_, input) in quickInputs)
                    input.Editable = false;
                message.Text = "请先修复全局参数页中的 JSON。";
            }
        }
    }

    private static Action AddMonsterControls(VBoxContainer panel, TextEdit profiles, Label message)
    {
        var list = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddChild(list);
        var availability = new Label();
        panel.AddChild(availability);
        var randomize = new CheckButton { Text = "启用该怪物的随机出招" };
        panel.AddChild(randomize);
        var requiredPower = new LineEdit { PlaceholderText = "留空则不限状态，例如 BURROWED" };
        panel.AddChild(new Label { Text = "仅拥有此状态时随机（状态 ID）" });
        panel.AddChild(requiredPower);
        var values = new[]
        {
            (Group: "", Key: "actionValue", Label: "基础行动力"),
            (Group: "", Key: "attackFrequency", Label: "每回合目标攻击段数"),
            (Group: "", Key: "survivalTurns", Label: "基础预期生存回合"),
            (Group: "", Key: "volatility", Label: "行动力波动"),
            (Group: "preferences", Key: "attack", Label: "纯攻击权重"),
            (Group: "preferences", Key: "buff", Label: "强化权重"),
            (Group: "preferences", Key: "defend", Label: "防御权重"),
            (Group: "preferences", Key: "playerDebuff", Label: "减益权重")
        };
        var inputs = new List<SpinBox>();
        string[] ids = [];
        string? selected = null;
        foreach (var (group, key, label) in values)
        {
            var input = NumberRow(panel, label, key == "actionValue" ? 0.01 : 0, 100, 0.01);
            inputs.Add(input);
            input.ValueChanged += value =>
                EditMonster(monster => (group.Length == 0 ? monster : monster[group]!)[key] = value);
        }
        randomize.Toggled += value => EditMonster(monster => monster["enabled"] = value, refresh: true);
        requiredPower.TextChanged += value => EditMonster(monster =>
        {
            string id = value.Trim().ToUpperInvariant();
            if (id.Length == 0)
                monster.AsObject().Remove("randomizeWhenPower");
            else
                monster["randomizeWhenPower"] = id;
        });
        list.ItemSelected += i =>
        {
            selected = ids[(int)i];
            RefreshValues(JsonNode.Parse(profiles.Text)!);
        };

        return () =>
        {
            try
            {
                var document = JsonNode.Parse(profiles.Text)!;
                var monsters = document["monsters"]!.AsObject();
                ids = monsters.OrderBy(pair => pair.Value?["name"]?.GetValue<string>())
                    .Select(pair => pair.Key).ToArray();
                list.Clear();
                randomize.Disabled = true;
                requiredPower.Editable = false;
                foreach (var id in ids)
                    list.AddItem($"{monsters[id]?["name"]?.GetValue<string>() ?? id}  ·  {id}");
                int index = Array.IndexOf(ids, selected);
                selected = index >= 0 ? ids[index] : ids.FirstOrDefault();
                if (selected != null)
                    list.Select(index < 0 ? 0 : index);
                RefreshValues(document);
            }
            catch (Exception)
            {
                list.Clear();
                foreach (var input in inputs)
                    input.Editable = false;
                availability.Text = "请先修复画像 JSON。";
            }
        };

        void RefreshValues(JsonNode document)
        {
            var monster = selected != null ? document["monsters"]?[selected] : null;
            bool excluded = document["keepVanilla"]?.AsArray().Any(id => id?.GetValue<string>() == selected) ?? false;
            bool active = monster != null && !excluded && monster["enabled"]?.GetValue<bool>() != false;
            randomize.Disabled = monster == null || excluded;
            randomize.SetPressedNoSignal(active);
            requiredPower.Editable = active;
            requiredPower.Text = monster?["randomizeWhenPower"]?.GetValue<string>() ?? "";
            availability.Text = excluded ? "专属行为保留原版" : "";
            for (int i = 0; i < values.Length; i++)
            {
                var (group, key, _) = values[i];
                var value = group.Length == 0 ? monster?[key] ?? document["defaults"]?[key] : monster?[group]?[key];
                bool present = value is JsonValue number && number.TryGetValue<double>(out _);
                inputs[i].Editable = present && active;
                inputs[i].SetValueNoSignal(present ? value!.GetValue<double>() : 0);
            }
        }

        void EditMonster(Action<JsonNode> edit, bool refresh = false)
        {
            try
            {
                var document = JsonNode.Parse(profiles.Text)!;
                if (selected == null || document["monsters"]?[selected] is not { } monster)
                    return;
                edit(monster);
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

    private static SpinBox NumberRow(BoxContainer container, string label, double min, double max, double step)
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
