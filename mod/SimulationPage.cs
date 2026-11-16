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
        var highAscension = new CheckButton { Text = "高进阶" };
        var choices = new HBoxContainer();
        panel.AddChild(choices);
        choices.AddChild(monster);
        choices.AddChild(highAscension);
        var phase = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
        panel.AddChild(phase);

        var seed = SettingsPage.NumberRow(panel, "随机种子", int.MinValue, int.MaxValue, 1);
        seed.AllowGreater = false;
        seed.Value = 42;
        var rounds = SettingsPage.NumberRow(panel, "模拟回合数", 1, 40, 1);
        rounds.AllowGreater = false;
        rounds.Value = 10;
        var rooms = SettingsPage.NumberRow(panel, "已经历房间", 0, 100, 1);
        var hp = SettingsPage.NumberRow(panel, "怪物当前血量", 1, 9999, 1);
        var allyHp = SettingsPage.NumberRow(panel, "其他怪物当前血量之和", 0, 9999, 1);
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
        string[] phases = [];
        phase.ItemSelected += _ =>
        {
            try
            {
                using var json = JsonDocument.Parse(profiles.Text);
                SetPhaseHp(json.RootElement.GetProperty("monsters").GetProperty(ids[monster.Selected]));
            }
            catch (Exception error)
            {
                output.Text = $"无法读取阶段画像。{error.Message}";
            }
        };
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
                output.Text = Run(rules, id, highAscension.ButtonPressed,
                    (int)seed.Value, (int)rounds.Value,
                    (int)rooms.Value, hp.Value, allyHp.Value, phase.Visible && phase.Selected >= 0 ? phases[phase.Selected] : null);
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
                    UpdatePhaseLabels(json.RootElement);
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
            UpdatePhaseLabels(profilesDocument);
            SetPhaseHp(entry);
            output.Text = "点击模拟生成该怪物的出招轨迹。";
        }

        void SetPhaseHp(JsonElement entry)
        {
            var phaseHp = phase.Visible && phase.Selected >= 0 &&
                entry.GetProperty("phases").GetProperty(phases[phase.Selected]).TryGetProperty("hp", out var amount)
                ? amount : entry.GetProperty("hp");
            hp.SetValueNoSignal(phaseHp.GetDouble());
        }

        void UpdatePhaseLabels(JsonElement profilesDocument)
        {
            var roster = profilesDocument.GetProperty("monsters");
            var entry = roster.GetProperty(ids[monster.Selected]);
            string? previousPhase = phase.Selected >= 0 ? phases[phase.Selected] : null;
            phase.Clear();
            phases = entry.TryGetProperty("phases", out var configuredPhases)
                ? configuredPhases.EnumerateObject().Select(item => item.Name).ToArray() : [];
            phase.Visible = phases.Length > 0;
            if (phase.Visible)
            {
                foreach (string id in phases)
                    phase.AddItem(configuredPhases.GetProperty(id).GetProperty("name").GetString() ?? id);
                phase.Select(Math.Max(0, Array.IndexOf(phases, previousPhase)));
            }
        }
    }

    private static string Run(RuleConfiguration rules, string id, bool highAscension, int seed, int rounds,
        int rooms, double hp, double allyHp, string? phaseId)
    {
        var monster = rules.Monsters[id];
        if (!monster.Enabled || rules.MovePresets.MoveIds(id) is { Length: > 0 } known && !known.Any(monster.AllowsMove))
            return "这个画像保留原版行动。";
        var phase = phaseId == null ? null : monster.Phases?.GetValueOrDefault(phaseId);
        if (phase?.Enabled == false)
            return $"{phase.Name}保留原版行动。";
        var profile = rules.PoolSettings.Adjust(phase?.Apply(monster.Profile) ?? monster.Profile, highAscension) with
        {
            BaseExpectedSurvivalTurns = rules.PlayerAction.Survival(hp, allyHp, rooms),
            PlayerActionValue = rules.PlayerAction.At(rooms),
            CurrentHp = hp,
            RoomsEntered = rooms + 1
        };
        var sequence = rules.MovePresets.SequenceFor(id, phaseId);
        var candidates = rules.OpeningStatesFor(id, monster)
            .Where(key => key != "STUN" || monster.AllowsMove(sequence?.First))
            .Select(key => rules.OpeningOption(key, profile, rooms, monsterId: id)).ToArray();
        var opening = OpeningValuation.Draw(
            new Dictionary<string, IReadOnlyList<OpeningOption>> { [id] = candidates },
            new Dictionary<string, double> { [id] = profile.ActionValue }, rules.Draws,
            MoveRandom.For(unchecked((uint)seed), rooms + 1, 0, 0));
        var turns = ActionSimulation.Trace(rules, id, profile, opening, seed, rounds,
            rooms: rooms, enemyCount: allyHp > 0 ? 2 : 1, phaseId: phaseId);
        var randomized = turns.Where(turn => turn.Plan.Action != null).ToArray();
        var result = new StringBuilder();
        result.AppendLine($"{id}　　已经历 {rooms} 个房间　　种子 {seed.ToString(CultureInfo.InvariantCulture)}");
        if (phase != null)
            result.AppendLine(phase.Name);
        result.AppendLine($"基础行动力 {profile.ActionValue:0.##}　波动 {profile.Volatility:P0}　目标攻击 {profile.AttackFrequency:0.##} 段／回合");
        if (phase?.Moves.Any(move => RuleConfiguration.UsesClawGrowth(id, move)) == true)
            result.AppendLine("阶段攻击欲望入账每个随机回合 +1");
        result.AppendLine($"攻击花费比例 {profile.BudgetedAttackShare:P0}　玩家行动力 {rules.PlayerAction.At(rooms):0.##}　" +
            $"预期生存 {profile.BaseExpectedSurvivalTurns:0.##} 回合");
        if (sequence != null)
            result.AppendLine("按预定义招式顺序模拟，每回合标明原招和替换结果。");
        string states = opening.Count == 0 ? "无" : string.Join("、", opening.Select(pick =>
            $"{ValuationPage.OpeningName(pick.State.Id)} {pick.State.Amount}{GeneratedCardName(pick.State.GeneratedCard)}（{pick.Value:+0.##;-0.##;0}）"));
        result.AppendLine($"开场状态 {states}");
        result.AppendLine($"开场净估值 {opening.Sum(pick => pick.Value):0.##}　净值上限 {profile.ActionValue * rules.Draws.MaxEncounterValueMultiplier * rules.Draws.Strength:0.##}");
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
            string name = turn.Plan.Action is { } action
                ? (turn.OriginalMove is { } original ? $"{original} → {FormatAction(action)}" : FormatAction(action)) :
                turn.Imbalanced ? "失衡" :
                turn.OriginalMove is { } kept ? $"{kept}（原版保留）" : "跳过";
            string cost = turn.Plan.Action == null ? "" : $"　消耗 {turn.Cost:0.##}";
            result.AppendLine($"第 {turn.Round,2} 回合　{name}{cost}　　行动力 {turn.Plan.Granted.ActionValue:0.##} → {turn.Plan.After.ActionValue:0.##}　" +
                $"攻击欲望 {turn.Plan.Granted.AttackDesire:0.##} → {turn.Plan.After.AttackDesire:0.##}");
        }
        return result.ToString();
    }

    private static string FormatAction(ActionCandidate action) => action.Effects!.Count == 0 ? "准备" :
        string.Join(" ＋ ", action.Effects.Select(effect =>
        effect.Kind == EffectKind.Attack
            ? $"攻击 {action.Damage / effect.Hits:0} × {effect.Hits}"
            : $"{IntentDetails.Names(effect.Kind).Chinese} {effect.Amount}{GeneratedCardName(effect.GeneratedCard)}" +
                (effect.IsStatusCard ? $"（{IntentDetails.DescribeDestinations(effect)}）" : "")));

    private static string GeneratedCardName(EffectKind? card) =>
        card.HasValue ? $"［{IntentDetails.Names(card.Value).Chinese}］" : "";
}
