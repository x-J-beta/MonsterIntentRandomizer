using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace MonsterIntentRandomizer;

public abstract class CustomPower : PowerModel
{
    private static readonly Func<LocManager, string, LocTable> GetTable =
        AccessTools.MethodDelegate<Func<LocManager, string, LocTable>>(AccessTools.Method(typeof(LocManager), "GetTable"));

    internal abstract PowerModel NativePower { get; }
    protected abstract (string Chinese, string English) DescriptionText { get; }
    public override LocString Title => NativePower.Title;

    public override LocString Description
    {
        get
        {
            var manager = LocManager.Instance;
            var table = GetTable(manager, "powers");
            string key = Id.Entry + ".description";
            if (!table.IsLocalKey(key))
                table.MergeWith(new() { [key] = manager.Language == "zhs" ? DescriptionText.Chinese : DescriptionText.English });
            return table.GetLocString(key);
        }
    }
}
