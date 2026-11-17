using Godot;

namespace MonsterIntentRandomizer;

internal static class SettingsFilters
{
    internal static OptionButton Add(BoxContainer parent, string label, IEnumerable<string> groups)
    {
        var filter = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        filter.AddItem("全部");
        foreach (string group in groups)
            filter.AddItem(group);
        filter.Select(0);
        parent.AddChild(new Label { Text = label });
        parent.AddChild(filter);
        return filter;
    }

    internal static string ActionCategory(EffectKind kind) => kind switch
    {
        EffectKind.Attack => "攻击",
        _ when ActionEffect.IsCard(kind) => "状态牌",
        _ when ActionEffect.IsCurseCard(kind) => "诅咒牌",
        _ when ActionEffect.StyleFor(kind) == IntentStyle.Defend => "防御与回复",
        _ when ActionEffect.IsMonsterBuff(kind) => "怪物强化",
        _ => "玩家减益"
    };
}

internal sealed class MonsterPickerFilters
{
    private sealed record Categories(Dictionary<string, string[]> Kinds, Dictionary<string, string[]> Maps);

    private readonly KeyValuePair<string, string[]>[] kinds;
    private readonly KeyValuePair<string, string[]>[] maps;
    private readonly OptionButton kindFilter;
    private readonly OptionButton mapFilter;
    internal event Action? Changed;

    internal MonsterPickerFilters(BoxContainer parent)
    {
        var categories = RuleConfiguration.Read<Categories>(ModSettings.ReadResource("monster-categories.json"));
        kinds = categories.Kinds.ToArray();
        maps = categories.Maps.ToArray();
        var bar = new HBoxContainer();
        parent.AddChild(bar);
        kindFilter = SettingsFilters.Add(bar, "怪物种类", kinds.Select(group => group.Key));
        mapFilter = SettingsFilters.Add(bar, "出现地图", maps.Select(group => group.Key));
        kindFilter.ItemSelected += _ => Changed?.Invoke();
        mapFilter.ItemSelected += _ => Changed?.Invoke();
    }

    internal bool Matches(string id) =>
        (kindFilter.Selected <= 0 || kinds[kindFilter.Selected - 1].Value.Contains(id)) &&
        (mapFilter.Selected <= 0 || maps[mapFilter.Selected - 1].Value.Contains(id));

    internal int ReferenceRooms(string id, string? stage)
    {
        var map = mapFilter.Selected > 0 ? maps[mapFilter.Selected - 1].Key : null;
        if (map?.StartsWith("一幕", StringComparison.Ordinal) == true)
            return 5;
        if (map?.StartsWith("二幕", StringComparison.Ordinal) == true)
            return 18;
        if (map?.StartsWith("三幕", StringComparison.Ordinal) == true)
            return 33;
        var locations = maps.Where(group => group.Value.Contains(id)).Select(group => group.Key).ToArray();
        if (stage == "act3" || stage == null && locations.Any(name => name.StartsWith("三幕", StringComparison.Ordinal)))
            return 33;
        if (stage == "act2" || stage == null && locations.Any(name => name.StartsWith("二幕", StringComparison.Ordinal)))
            return 18;
        return 5;
    }
}
