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
                    foreach (var (term, value) in formula!.AsObject())
                        AddTerm(actionList, id, term, value!.GetValue<double>());
                }
                openingList.AddChild(new Label { Text = "状态值 = 层数绝对值 × 计价基数 × 系数" });
                foreach (var (id, state) in document["opening"]!["states"]!.AsObject())
                {
                    Heading(openingList, OpeningName(id), id);
                    var basis = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    var bases = Enum.GetValues<OpeningBasis>();
                    foreach (var choice in bases)
                        basis.AddItem(BasisLabel(choice));
                    Enum.TryParse(state!["basis"]?.GetValue<string>(), ignoreCase: true, out OpeningBasis current);
                    basis.Select(Array.IndexOf(bases, current));
                    basis.ItemSelected += selected => Edit(root =>
                        root["opening"]!["states"]![id]!["basis"] = bases[(int)selected].ToString());
                    openingList.AddChild(basis);
                    var input = Coefficient(openingList, "每层系数", state["turns"]!.GetValue<double>());
                    input.ValueChanged += coefficient =>
                        Edit(root => root["opening"]!["states"]![id]!["turns"] = Math.Round(coefficient, 4));
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

        void AddTerm(VBoxContainer content, string id, string term, double value)
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
            var factors = ActionValuation.FactorNames;
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
                    var formula = root["actions"]!["valuation"]![id]!.AsObject();
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
            input.ValueChanged += coefficient => Edit(root =>
                root["actions"]!["valuation"]![id]![term] = Math.Round(coefficient, 4));
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
            "playerturnvalue" => "玩家回合价值", _ => factor
        }));

    private static string BasisLabel(OpeningBasis basis) => basis switch
    {
        OpeningBasis.ActionValue => "基础行动力", OpeningBasis.Flat => "固定值",
        OpeningBasis.SurvivalTurns => "预期寿命", OpeningBasis.AttackFrequency => "攻击频率",
        OpeningBasis.AttacksOverLifetime => "攻击频率 × 预期寿命", _ => basis.ToString()
    };

    internal static string OpeningName(string id) => id switch
    {
        "STRENGTH_DOWN" => "负力量", "REGEN" => "再生", "PLATING" => "甲覆", "ARTIFACT" => "人工制品",
        "DEMISE" => "消亡", "DOOM" => "灾厄", "IMBALANCED" => "失衡", "STUN" => "眩晕",
        _ => Enum.TryParse<EffectKind>(id.Replace("_", ""), ignoreCase: true, out var kind)
            ? IntentDetails.Names(kind).Chinese : id
    };
}
