using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ExplorersIcebox.Ui.DebugWindowTabs;

internal class EcomsTestingDebug
{
    public static void Draw()
    {
        if (TryGetAddonMaster("MJIHud", out MJIHud mjiHud) && mjiHud.IsAddonReady)
        {
            ImGui.Text($"Current Level: {mjiHud.SanctuaryRank}");
            ImGui.Text($"Current Island XP: {mjiHud.CurrentIslandXP} | {mjiHud.NextIslandLevelXP}");
            ImGui.Text($"Island Cowries: {mjiHud.IslandersCowrie}");
            ImGui.Text($"Seafarers Cowries: {mjiHud.SeafarersCowrie}");
        }
    }
}
