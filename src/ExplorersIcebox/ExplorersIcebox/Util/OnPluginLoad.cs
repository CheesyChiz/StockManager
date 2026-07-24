using Lumina.Excel.Sheets;
using System.Collections.Generic;
namespace ExplorersIcebox.Util;

public static class OnPluginLoad
{
    public static Dictionary<int, string> IslandItemInfo = new();

    public static readonly int[] ItemIds =
    [
        37551, 37553, 37554,
        37555, 37556, 37557,
        37558, 37559, 37562,
        37563, 37552, 37560,
        37561, 37564, 37565,
        37566, 37570, 37571,
        37567, 37568, 37569,
        37575, 37576, 37577,
        37572, 37573, 37574,
        39228, 39224, 39225,
        39226, 39227, 39887,
        39889, 39892, 39888,
        39890, 39891, 39893,
        41630, 41631, 41632,
        41633, 41634
    ];

    public static void UpdateItemNames()
    {
        var sheet = Svc.Data.GetExcelSheet<Item>();
        foreach (var itemId in ItemIds)
        {
            IslandItemInfo[itemId] = sheet.GetRow((uint)itemId).Name.ToString();
        }
    }
}
