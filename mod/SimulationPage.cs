using System.Globalization;
using System.Text;
using System.Text.Json;
using Godot;

namespace MonsterIntentRandomizer;

internal static class SimulationPage
{
    internal static Action Create(VBoxContainer panel, TextEdit settings, TextEdit profiles)
    {
        var monster = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var act = new OptionButton();
        for (int number = 1; number <= 3; number++)
            act.AddItem($"第 {number} 幕");
        var highAscension = new CheckButton { Text = "高进阶" };
        var choices = new HBoxContainer();
        panel.AddChild(choices);
        choices.AddChild(monster);
        choices.AddChild(act);
        choices.AddChild(highAscension);

        var seed = SettingsPage.NumberRow(panel, "随机种子", int.MinValue, int.MaxValue, 1);
        seed.AllowGreater = false;
        seed.Value = 42;
        var rounds = SettingsPage.NumberRow(panel, "模拟回合数", 1, 40, 1);
        rounds.AllowGreater = false;
        rounds.Value = 10;
        var rooms = SettingsPage.NumberRow(panel, "已经历房间", 0, 100, 1);
        var hp = SettingsPage.NumberRow(panel, "怪物当前血量", 1, 9999, 1);
        var allyHp = SettingsPage.NumberRow(panel, "其他怪物当前血量之和", 0, 9999, 1);
        var allowPhase = new CheckButton { Text = "假定满足怪物的状态条件", ButtonPressed = true, Visible = false };
        panel.AddChild(allowPhase);
        var run = new Button { Name = "RunSimulation", Text = "按当前参数模拟" };
        panel.AddChild(run);
        var output = new TextEdit
        {
            Name = "SimulationOutput", Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary, Text = "调整参数后点击模拟。"
        };
        output.AddThemeFontSizeOverride("font_size", 17);
        panel.AddChild(output);

        string[] ids = [];
        monster.ItemSelected += _ =>
        {
            try
            {
                using var json = JsonDocument.Parse(profiles.Text);
                ReadSelectedMonster(json.RootElement);
            }
            catch (Exception error)
            {
                output.Text = $"无法读取画像。{error.Message}";
            }
        };
        run.Pressed += () =>
        {
            try
            {
                if (monster.Selected < 0)
                    return;
                using var settingsJson = JsonDocument.Parse(settings.Text);
                using var profilesJson = JsonDocument.Parse(profiles.Text);
                var rules = ModSettings.Parse(new StoredSettings(true, settingsJson.RootElement, profilesJson.RootElement));
                string id = ids[monster.Selected];
                output.Text = Run(rules, id, act.Selected + 1, highAscension.ButtonPressed,
                    (int)seed.Value, (int)rounds.Value, allowPhase.ButtonPressed, (int)rooms.Value, hp.Value, allyHp.Value);
            }
            catch (Exception error)
            {
                output.Text = $"无法模拟。{error.Message}";
            }
        };

        return () =>
        {
            string? previous = monster.Selected >= 0 ? ids[monster.Selected] : null;
            try
            {
                using var json = JsonDocument.Parse(profiles.Text);
                var roster = json.RootElement.GetProperty("monsters").EnumerateObject()
                    .Select(field => (Id: field.Name, Name: field.Value.TryGetProperty("name", out var title)
                        ? title.GetString() ?? field.Name : field.Name))
                    .OrderBy(entry => entry.Name).ToArray();
                ids = roster.Select(entry => entry.Id).ToArray();
                monster.Clear();
                foreach (var entry in roster)
                    monster.AddItem($"{entry.Name}  ·  {entry.Id}");
                run.Disabled = ids.Length == 0;
                if (run.Disabled)
                    return;
                int selected = Array.IndexOf(ids, previous ?? "FLYCONID");
                monster.Select(selected < 0 ? 0 : selected);
                if (previous != ids[monster.Selected])
                    ReadSelectedMonster(json.RootElement);
                else
                    UpdatePhaseLabel(json.RootElement.GetProperty("monsters").GetProperty(ids[monster.Selected]));
            }
            catch (Exception error)
            {
                run.Disabled = true;
                monster.Clear();
                output.Text = $"无法读取画像。{error.Message}";
            }
        };

        void ReadSelectedMonster(JsonElement profilesDocument)
        {
            var entry = profilesDocument.GetProperty("monsters").GetProperty(ids[monster.Selected]);
            hp.SetValueNoSignal(entry.GetProperty("hp").GetDouble());
            act.Select(entry.TryGetProperty("stage", out var stage) ? stage.GetString() switch
            {
                "act2" => 1, "act3" => 2, _ => 0
            } : 0);
            UpdatePhaseLabel(entry);
            output.Text = "点击模拟生成该怪物的出招轨迹。";
        }

        void UpdatePhaseLabel(JsonElement entry)
        {
            string? power = entry.TryGetProperty("randomizeWhenPower", out var condition) ? condition.GetString() : null;
            allowPhase.Visible = !string.IsNullOrWhiteSpace(power);
            allowPhase.Text = $"假定拥有 {power}";
        }
    }

    private static string Run(RuleConfiguration rules, string id, int act, bool highAscension, int seed, int rounds,
        bool allowPhase, int rooms, double hp, double allyHp)
    {
        var monster = rules.Monsters[id];
        if (!monster.Enabled || rules.KeepVanilla.Contains(id))
            return "这个画像保留原版行动。";
        var profile = rules.PoolSettings.Adjust(monster.AtAscension(highAscension)) with
        {
            BaseExpectedSurvivalTurns = rules.PlayerAction.Survival(hp, allyHp, rooms),
            PlayerActionValue = rules.PlayerAction.At(rooms),
            CurrentHp = hp,
            RoomsEntered = rooms + 1
        };
        var candidates = rules.OpeningStatesFor(monster).Where(key => rules.OpeningValues[key].MinAct <= act &&
            (key != "STUN" || !monster.FixedOpening))
            .Select(key => OpeningValuation.Option(key, rules.OpeningValues[key], profile, rooms)).ToArray();
        var opening = OpeningValuation.Draw(
            new Dictionary<string, IReadOnlyList<OpeningOption>> { [id] = candidates },
            new Dictionary<string, double> { [id] = profile.ActionValue }, rules.Draws, new Random(seed));
        bool phaseAllowsRandomization = string.IsNullOrWhiteSpace(monster.RandomizeWhenPower) || allowPhase;
        var turns = ActionSimulation.Trace(rules, profile, opening, act, seed, rounds, monster.FullBlockChance,
            monster.FixedOpening, phaseAllowsRandomization, rooms: rooms, enemyCount: allyHp > 0 ? 2 : 1);
        var randomized = turns.Where(turn => turn.Plan.Action != null).ToArray();
        var result = new StringBuilder();
        result.AppendLine($"{id}　　第 {act} 幕　　种子 {seed.ToString(CultureInfo.InvariantCulture)}");
        result.AppendLine($"基础行动力 {profile.ActionValue:0.##}　波动 {profile.Volatility:P0}　目标攻击 {profile.AttackFrequency:0.##} 段／回合");
        result.AppendLine($"攻击花费比例 {profile.BudgetedAttackShare:P0}　玩家行动力 {rules.PlayerAction.At(rooms):0.##}　" +
            $"预期生存 {profile.BaseExpectedSurvivalTurns:0.##} 回合");
        if (monster.RandomMoves != null)
            result.AppendLine($"可随机招式 {monster.RandomMoves.Count} 个。以下轨迹模拟符合随机条件的回合。");
        string states = opening.Count == 0 ? "无" : string.Join("、", opening.Select(pick =>
            $"{ValuationPage.OpeningName(pick.State.Id)} {pick.State.Amount}（{pick.Value:+0.##;-0.##;0}）"));
        result.AppendLine($"开场状态 {states}");
        result.AppendLine($"开场净估值 {opening.Sum(pick => pick.Value):0.##}　净值上限 {profile.ActionValue * rules.Draws.MaxEncounterValueMultiplier:0.##}");
        result.AppendLine($"随机招式 {randomized.Length}／{rounds} 回合");
        if (randomized.Length > 0)
        {
            result.AppendLine($"随机回合平均 {randomized.Average(turn => turn.Hits):0.##} 段攻击、" +
                $"{randomized.Average(turn => turn.Plan.Action!.Damage):0.##} 面板伤害、" +
                $"{randomized.Average(turn => turn.Cost):0.##} 行动分");
        }
        result.AppendLine("模拟所选怪物的连续行动，血量取当前输入。");
        result.AppendLine();
        foreach (var turn in turns)
        {
            string name = turn.Plan.Action is { } action ? FormatAction(action) :
                turn.Imbalanced ? "失衡" : turn.UsedFallback ? "回退原版" :
                !phaseAllowsRandomization || monster.FixedOpening && turn.Round == 1 ? "原版回合" : "跳过";
            string cost = turn.Plan.Action == null ? "" : $"　消耗 {turn.Cost:0.##}";
            result.AppendLine($"第 {turn.Round,2} 回合　{name}{cost}　　行动力 {turn.Plan.Granted.ActionValue:0.##} → {turn.After.ActionValue:0.##}　" +
                $"攻击欲望 {turn.Plan.Granted.AttackDesire:0.##} → {turn.After.AttackDesire:0.##}");
        }
        return result.ToString();
    }

    private static string FormatAction(ActionCandidate action) => action.Effects!.Count == 0 ? "准备" :
        string.Join(" ＋ ", action.Effects.Select(effect =>
        effect.Kind == EffectKind.Attack
            ? $"攻击 {action.Damage / effect.Hits:0} × {effect.Hits}"
            : $"{IntentDetails.Names(effect.Kind).Chinese} {effect.Amount}" +
                (effect.IsStatusCard ? $"（{IntentDetails.PlacementName(effect.Pile)}）" : "")));
}
