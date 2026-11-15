using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace MonsterIntentRandomizer;

internal static class ValuationPage
{
    internal static Action Create(TabContainer tabs, TextEdit settings, Label message)
    {
        var actions = AddScroll("出招效果");
        var opening = AddScroll("开场状态");
        return () =>
        {
            Clear(actions);
            Clear(opening);
            try
            {
                var document = JsonNode.Parse(settings.Text)!;
                actions.AddChild(new Label { Text = "总分 = 各项（系数 × 该项因子）的和" });
                foreach (var (id, formula) in document["actions"]!["valuation"]!.AsObject())
                {
                    string name = Enum.TryParse<EffectKind>(id, ignoreCase: true, out var kind)
                        ? IntentDetails.Names(kind).Chinese : id;
                    Heading(actions, name, id);
                    foreach (var (term, value) in formula!.AsObject())
                    {
                        var input = Coefficient(actions, FactorLabel(term), value!.GetValue<double>());
                        input.ValueChanged += coefficient =>
                            Edit(root => root["actions"]!["valuation"]![id]![term] = Math.Round(coefficient, 4));
                    }
                }
                opening.AddChild(new Label { Text = "状态值 = 层数绝对值 × 计价基数 × 系数" });
                foreach (var (id, state) in document["opening"]!["states"]!.AsObject())
                {
                    Heading(opening, OpeningName(id), id);
                    var basis = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    var bases = Enum.GetValues<OpeningBasis>();
                    foreach (var choice in bases)
                        basis.AddItem(BasisLabel(choice));
                    Enum.TryParse(state!["basis"]?.GetValue<string>(), ignoreCase: true, out OpeningBasis current);
                    basis.Select(Array.IndexOf(bases, current));
                    basis.ItemSelected += selected => Edit(root =>
                        root["opening"]!["states"]![id]!["basis"] = bases[(int)selected].ToString());
                    opening.AddChild(basis);
                    var input = Coefficient(opening, "每层系数", state["turns"]!.GetValue<double>());
                    input.ValueChanged += coefficient =>
                        Edit(root => root["opening"]!["states"]![id]!["turns"] = Math.Round(coefficient, 4));
                }
            }
            catch (Exception error)
            {
                message.Text = $"无法读取估值。{error.Message}";
            }
        };

        VBoxContainer AddScroll(string name)
        {
            var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            content.AddThemeConstantOverride("separation", 6);
            tabs.AddChild(scroll);
            scroll.AddChild(content);
            return content;
        }

        void Edit(Action<JsonNode> write)
        {
            try
            {
                var document = JsonNode.Parse(settings.Text)!;
                write(document);
                settings.Text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                message.Text = "";
            }
            catch (Exception error)
            {
                message.Text = $"无法同步估值。{error.Message}";
            }
        }
    }

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
