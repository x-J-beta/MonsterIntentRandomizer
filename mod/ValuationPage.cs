using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace MonsterIntentRandomizer;

internal static class ValuationPage
{
    internal static Action Create(VBoxContainer page, TextEdit settings, Label message)
    {
        var navigation = new HBoxContainer { Name = "估值分类" };
        page.AddChild(navigation);
        var actions = AddScroll("出招效果");
        var opening = AddScroll("开场状态");
        var group = new ButtonGroup();
        AddCategory(actions, true);
        AddCategory(opening, false);
        string? selectedAction = null;
        string? selectedOpening = null;
        return () =>
        {
            var actionList = actions.GetChild<VBoxContainer>(0);
            var openingList = opening.GetChild<VBoxContainer>(0);
            Clear(actionList);
            Clear(openingList);
            try
            {
                var document = JsonNode.Parse(settings.Text)!;
                AddSelector(actionList, document["actions"]!["valuation"]!.AsObject().Select(entry => entry.Key),
                    ActionName, selectedAction, (detail, id) =>
                    {
                        selectedAction = id;
                        RenderAction(detail, JsonNode.Parse(settings.Text)!["actions"]!, id);
                    });
                AddSelector(openingList, document["opening"]!["states"]!.AsObject().Select(entry => entry.Key),
                    OpeningName, selectedOpening, (detail, id) =>
                    {
                        selectedOpening = id;
                        RenderOpening(detail, JsonNode.Parse(settings.Text)!["opening"]!["states"]![id]!, id);
                    });
            }
            catch (Exception error)
            {
                message.Text = $"无法读取估值。{error.Message}";
            }
        };

        static string ActionName(string id) => Enum.TryParse<EffectKind>(id, ignoreCase: true, out var kind)
            ? IntentDetails.Names(kind).Chinese : id;

        void AddSelector(VBoxContainer content, IEnumerable<string> ids, Func<string, string> title,
            string? previous, Action<VBoxContainer, string> render)
        {
            var entries = ids.ToArray();
            var selector = new OptionButton { Name = "状态选择", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            foreach (string id in entries)
                selector.AddItem($"{title(id)}  ·  {id}");
            content.AddChild(selector);
            var detail = new VBoxContainer();
            detail.AddThemeConstantOverride("separation", 6);
            content.AddChild(detail);
            if (entries.Length == 0)
                return;
            int index = Math.Max(0, Array.IndexOf(entries, previous));
            selector.Select(index);
            selector.ItemSelected += selected => Show((int)selected);
            Show(index);

            void Show(int selected)
            {
                Clear(detail);
                try { render(detail, entries[selected]); }
                catch (Exception error) { message.Text = $"无法读取估值。{error.Message}"; }
            }
        }

        void RenderAction(VBoxContainer content, JsonNode config, string id)
        {
            var kind = Enum.Parse<EffectKind>(id, ignoreCase: true);
            var defaultPolicy = EffectPolicy.Default(kind);
            var policy = config["policies"]?[id];
            content.AddChild(new Label
            {
                Text = $"目标：{(ActionEffect.TargetFor(kind) == EffectTarget.Self ? "自身" : "玩家")}"
            });
            AddFormula(content, root => root["actions"]!["valuation"]![id]!.AsObject(), config["valuation"]![id]!.AsObject());
            if (id == "Attack")
                return;

            AddCheck(content, "可叠加", policy?["stackable"]?.GetValue<bool>() ?? defaultPolicy.Stackable,
                (root, value) => SetPolicy(root, id, "stackable", value));
            AddCheck(content, "战斗中独特", policy?["unique"]?.GetValue<bool>() ?? defaultPolicy.Unique,
                (root, value) => SetPolicy(root, id, "unique", value));
            var weight = config["weights"]?[id];
            AddNumber(content, "基础权重（-1 停用）", weight?["base"]?.GetValue<double>() ?? 1, 0.001,
                (root, number) => SetActionWeight(root, id, "base", number));
            AddNumber(content, "每房间权重变化", weight?["perRoom"]?.GetValue<double>() ?? 0, 0.001,
                (root, number) => SetActionWeight(root, id, "perRoom", number));
            AddNumber(content, "每次层数下限", config["minAmounts"]?[id]?.GetValue<int>() ?? 1, 1,
                (root, number) => root["actions"]!["minAmounts"]![id] = (int)number, minimum: 1);
            if (config["hpPerSurvivalTurnCaps"]?[id]?.GetValue<double>() is double cap)
                AddNumber(content, "动态层数上限系数", cap, 0.01,
                    (root, number) => root["actions"]!["hpPerSurvivalTurnCaps"]![id] = number, minimum: 0);
            else
                AddNumber(content, "每次层数上限", config["maxAmounts"]?[id]?.GetValue<int>() ?? 0, 1,
                    (root, number) => root["actions"]!["maxAmounts"]![id] = (int)number);
        }

        void RenderOpening(VBoxContainer content, JsonNode state, string id)
        {
            JsonNode At(JsonNode root) => root["opening"]!["states"]![id]!;
            content.AddChild(new Label { Text = "目标：自身" });
            AddFormula(content, root => At(root)["formula"]!.AsObject(), state["formula"]!.AsObject(), openingFormula: true);
            AddCheck(content, "可叠加", state["stackable"]?.GetValue<bool>() ?? false,
                (root, value) => At(root)["stackable"] = value);
            AddCheck(content, "战斗中独特", state["unique"]?.GetValue<bool>() ?? false,
                (root, value) => At(root)["unique"] = value);
            AddNumber(content, "基础权重（-1 停用）", state["weight"]?.GetValue<double>() ?? 1, 0.001,
                (root, number) => At(root)["weight"] = number);
            AddNumber(content, "每房间权重变化", state["weightPerRoom"]?.GetValue<double>() ?? 0, 0.001,
                (root, number) => At(root)["weightPerRoom"] = number);
            AddNumber(content, "开场层数下限", state["minAmount"]?.GetValue<int>() ?? 1, 1,
                (root, number) => At(root)["minAmount"] = (int)number, minimum: 1);
            if (state["hpPerSurvivalTurnCap"]?.GetValue<double>() is double cap)
                AddNumber(content, "动态层数上限系数", cap, 0.01,
                    (root, number) => At(root)["hpPerSurvivalTurnCap"] = number, minimum: 0);
            else
                AddNumber(content, "开场层数上限", state["maxAmount"]?.GetValue<int>() ?? 0, 1,
                    (root, number) => At(root)["maxAmount"] = (int)number);
        }

        void AddCheck(VBoxContainer content, string label, bool current, Action<JsonNode, bool> update)
        {
            var button = new CheckButton { Text = label, ButtonPressed = current };
            content.AddChild(button);
            button.Toggled += value => Edit(root => update(root, value));
        }

        static void SetPolicy(JsonNode root, string id, string key, bool value)
        {
            var config = root["actions"]!.AsObject();
            config["policies"] ??= new JsonObject();
            if (config["policies"]![id] == null)
            {
                var defaults = EffectPolicy.Default(Enum.Parse<EffectKind>(id, ignoreCase: true));
                config["policies"]![id] = new JsonObject
                {
                    ["stackable"] = defaults.Stackable, ["unique"] = defaults.Unique,
                    ["target"] = defaults.Target!.Value.ToString().ToLowerInvariant()
                };
            }
            config["policies"]![id]![key] = value;
        }

        ScrollContainer AddScroll(string name)
        {
            var scroll = new ScrollContainer
            {
                Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            content.AddThemeConstantOverride("separation", 6);
            page.AddChild(scroll);
            scroll.AddChild(content);
            return scroll;
        }

        void AddCategory(ScrollContainer scroll, bool selected)
        {
            var button = new Button
            {
                Name = scroll.Name, Text = scroll.Name.ToString(), ButtonGroup = group,
                ToggleMode = true, ButtonPressed = selected
            };
            navigation.AddChild(button);
            scroll.Visible = selected;
            button.Pressed += () =>
            {
                actions.Visible = scroll == actions;
                opening.Visible = scroll == opening;
            };
        }

        static void SetActionWeight(JsonNode root, string id, string key, double number)
        {
            var actionsConfig = root["actions"]!.AsObject();
            if (actionsConfig["weights"] is not JsonObject)
                actionsConfig["weights"] = new JsonObject();
            var weights = actionsConfig["weights"]!.AsObject();
            if (weights[id] is not JsonObject)
                weights[id] = new JsonObject();
            weights[id]![key] = number;
        }

        void AddNumber(VBoxContainer content, string caption, double value, double step, Action<JsonNode, double> update,
            double? minimum = null)
        {
            var input = SettingsPage.NumberRow(content, caption, minimum ?? (step == 1 ? 0 : -100), 100, step);
            input.AllowLesser = minimum == null && step != 1;
            input.SetValueNoSignal(value);
            input.ValueChanged += number => Edit(root => update(root, number));
        }

        void AddFormula(VBoxContainer content, Func<JsonNode, JsonObject> getFormula, JsonObject initial,
            bool openingFormula = false)
        {
            var terms = new VBoxContainer();
            content.AddChild(terms);
            var factors = ActionValuation.FactorNames.Where(name => !openingFormula ||
                name is "Amount" or "ActionValue" or "AttackFrequency" or "SurvivalTurns"
                    or "PlayerActionValue" or "HpPerSurvivalTurn" or "InverseAmount" or "RoomsEntered").ToArray();
            foreach (var (term, value) in initial)
                AddTerm(terms, getFormula, factors, term, value!.GetValue<double>());
            var add = new Button { Text = "新增估值项", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
            content.AddChild(add);
            add.Pressed += () =>
            {
                string term = "";
                if (Edit(root =>
                {
                    var formula = getFormula(root);
                    var names = new[] { "fixed" }.Concat(factors).Concat(
                        factors.SelectMany((first, i) => factors.Skip(i + 1).Select(second => $"{first}*{second}")));
                    term = names.FirstOrDefault(name => !formula.Any(entry => SameFactors(entry.Key, name))) ??
                        throw new ArgumentException("请先删除一个估值项。");
                    formula.Add(term, 0);
                }))
                    AddTerm(terms, getFormula, factors, term, 0);
            };
        }

        void AddTerm(VBoxContainer content, Func<JsonNode, JsonObject> getFormula, IReadOnlyList<string> factors,
            string term, double value)
        {
            var input = Coefficient(content, "系数", value);
            var row = input.GetParent<HBoxContainer>();
            var selector = new MenuButton
            {
                Text = FactorLabel(term), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "选择乘数并应用。未选乘数时使用固定值。"
            };
            row.AddChild(selector);
            row.MoveChild(selector, 0);

            var popup = selector.GetPopup();
            for (int i = 0; i < factors.Count; i++)
                popup.AddCheckItem(FactorLabel(factors[i]), i);
            popup.AddSeparator();
            popup.AddItem("应用因子", factors.Count);
            popup.HideOnCheckableItemSelection = false;
            selector.AboutToPopup += () =>
            {
                var current = term.Split('*', StringSplitOptions.TrimEntries);
                for (int i = 0; i < factors.Count; i++)
                    popup.SetItemChecked(i, current.Contains(factors[i], StringComparer.OrdinalIgnoreCase));
            };
            popup.IdPressed += selected =>
            {
                if (selected < factors.Count)
                {
                    int index = (int)selected;
                    popup.SetItemChecked(index, !popup.IsItemChecked(index));
                    return;
                }

                string next = string.Join("*", factors.Where((_, i) => popup.IsItemChecked(i)));
                next = next.Length == 0 ? "fixed" : next;
                if (SameFactors(term, next))
                    return;

                if (Edit(root =>
                {
                    var formula = getFormula(root);
                    if (formula.Any(existing => SameFactors(existing.Key, next)))
                        throw new ArgumentException($"此效果已有「{FactorLabel(next)}」这一项。");
                    var coefficient = formula[term]!.DeepClone();
                    formula.Remove(term);
                    formula.Add(next, coefficient);
                }))
                {
                    term = next;
                    selector.Text = FactorLabel(term);
                }
            };
            input.ValueChanged += coefficient => Edit(root => getFormula(root)[term] = Math.Round(coefficient, 4));
            var remove = new Button { Text = "删除" };
            row.AddChild(remove);
            remove.Pressed += () =>
            {
                if (Edit(root => getFormula(root).Remove(term)))
                {
                    content.RemoveChild(row);
                    row.QueueFree();
                }
            };
        }

        bool Edit(Action<JsonNode> write)
        {
            try
            {
                var document = JsonNode.Parse(settings.Text)!;
                write(document);
                settings.Text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                message.Text = "";
                return true;
            }
            catch (Exception error)
            {
                message.Text = $"无法同步估值。{error.Message}";
                return false;
            }
        }
    }

    private static bool SameFactors(string left, string right) => left.Split('*', StringSplitOptions.TrimEntries)
        .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            right.Split('*', StringSplitOptions.TrimEntries).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static void Clear(VBoxContainer content)
    {
        foreach (var node in content.GetChildren())
        {
            content.RemoveChild(node);
            node.QueueFree();
        }
    }

    private static SpinBox Coefficient(VBoxContainer content, string caption, double value)
    {
        var input = SettingsPage.NumberRow(content, caption, -100, 100, 0.0001);
        input.AllowLesser = true;
        input.SetValueNoSignal(value);
        return input;
    }

    private static string FactorLabel(string term) => string.Join(" × ", term.Split('*', StringSplitOptions.TrimEntries)
        .Select(factor => factor.ToLowerInvariant() switch
        {
            "fixed" => "固定值", "amount" => "层数／数量", "actionvalue" => "基础行动力",
            "attackfrequency" => "攻击频率", "survivalturns" => "预期寿命", "effectiveamount" => "有效数量",
            "hits" => "攻击段数", "draws" => "预计抽到次数", "triggers" => "预计触发次数",
            "playeractionvalue" => "玩家行动力", "roomsentered" => "进入房间数",
            "hppersurvivalturn" => "当前血量／预期生存回合", "inverseamount" => "1／层数", _ => factor
        }));

    internal static string OpeningName(string id) => id switch
    {
        "STRENGTH_DOWN" => "负力量", "REGEN" => "再生", "PLATING" => "甲覆", "ARTIFACT" => "人工制品",
        "DEMISE" => "消亡", "DOOM" => "灾厄", "IMBALANCED" => "失衡", "STUN" => "眩晕",
        _ => Enum.TryParse<EffectKind>(id.Replace("_", ""), ignoreCase: true, out var kind)
            ? IntentDetails.Names(kind).Chinese : id
    };
}
