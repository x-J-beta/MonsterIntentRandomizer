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
        return () =>
        {
            var actionList = actions.GetChild<VBoxContainer>(0);
            var openingList = opening.GetChild<VBoxContainer>(0);
            Clear(actionList);
            Clear(openingList);
            try
            {
                var document = JsonNode.Parse(settings.Text)!;
                actionList.AddChild(new Label { Text = "总分 = 各项（系数 × 该项因子）的和" });
                foreach (var (id, formula) in document["actions"]!["valuation"]!.AsObject())
                {
                    string name = Enum.TryParse<EffectKind>(id, ignoreCase: true, out var kind)
                        ? IntentDetails.Names(kind).Chinese : id;
                    Heading(actionList, name, id);
                    AddFormula(actionList, root => root["actions"]!["valuation"]![id]!.AsObject(), formula!.AsObject());
                    if (id == "Attack")
                        continue;
                    var config = document["actions"]!;
                    var weight = config["weights"]?[id];
                    AddNumber(actionList, "基础权重", weight?["base"]?.GetValue<double>() ?? 1, 0.001,
                        (root, number) => SetActionWeight(root, id, "base", number));
                    AddNumber(actionList, "每房间权重变化", weight?["perRoom"]?.GetValue<double>() ?? 0, 0.001,
                        (root, number) => SetActionWeight(root, id, "perRoom", number));
                    AddNumber(actionList, "每次层数下限", config["minAmounts"]?[id]?.GetValue<int>() ?? 1, 1,
                        (root, number) => root["actions"]!["minAmounts"]![id] = (int)number, minimum: 1);
                    if (config["hpPerSurvivalTurnCaps"]?[id]?.GetValue<double>() is double cap)
                        AddNumber(actionList, "动态层数上限系数", cap, 0.01,
                            (root, number) => root["actions"]!["hpPerSurvivalTurnCaps"]![id] = number, minimum: 0);
                    else
                        AddNumber(actionList, "每次层数上限", config["maxAmounts"]?[id]?.GetValue<int>() ?? 0, 1,
                            (root, number) => root["actions"]!["maxAmounts"]![id] = (int)number);
                }
                openingList.AddChild(new Label { Text = "总分 = 各项（系数 × 该项因子）的和" });
                foreach (var (id, state) in document["opening"]!["states"]!.AsObject())
                {
                    Heading(openingList, OpeningName(id), id);
                    AddFormula(openingList, root => root["opening"]!["states"]![id]!["formula"]!.AsObject(),
                        state!["formula"]!.AsObject(), openingFormula: true);
                    AddNumber(openingList, "基础权重", state["weight"]?.GetValue<double>() ?? 1, 0.001,
                        (root, number) => root["opening"]!["states"]![id]!["weight"] = number);
                    AddNumber(openingList, "每房间权重变化", state["weightPerRoom"]?.GetValue<double>() ?? 0, 0.001,
                        (root, number) => root["opening"]!["states"]![id]!["weightPerRoom"] = number);
                    AddNumber(openingList, "开场层数下限", state["minAmount"]?.GetValue<int>() ?? 1, 1,
                        (root, number) => root["opening"]!["states"]![id]!["minAmount"] = (int)number, minimum: 1);
                    if (state["hpPerSurvivalTurnCap"]?.GetValue<double>() is double cap)
                        AddNumber(openingList, "动态层数上限系数", cap, 0.01,
                            (root, number) => root["opening"]!["states"]![id]!["hpPerSurvivalTurnCap"] = number, minimum: 0);
                    else
                        AddNumber(openingList, "开场层数上限", state["maxAmount"]?.GetValue<int>() ?? 0, 1,
                            (root, number) => root["opening"]!["states"]![id]!["maxAmount"] = (int)number);
                }
            }
            catch (Exception error)
            {
                message.Text = $"无法读取估值。{error.Message}";
            }
        };

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
                name is "Amount" or "ActionValue" or "AttackFrequency" or "SurvivalTurns" or "PlayerTurnValue"
                    or "PlayerActionValue" or "HpPerSurvivalTurn" or "InverseAmount").ToArray();
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

    private static void Heading(VBoxContainer content, string name, string id)
    {
        content.AddChild(new HSeparator());
        var title = new Label { Text = $"{name}  ·  {id}" };
        title.AddThemeFontSizeOverride("font_size", 19);
        content.AddChild(title);
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
            "playerturnvalue" => "玩家回合价值", "playeractionvalue" => "玩家行动力",
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
