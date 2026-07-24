using ExplorersIcebox.IPC;
namespace ExplorersIcebox.Ui.DebugWindowTabs;

internal class RouteSellDebug
{
    public static void Draw()
    {
        ImGui.Text("This is where the route sell debug would be... IF I HAD ONE");
        if (NavmeshIPC.Installed)
        {
            ImGui.Text("Navmesh is installed. Woohoo!");
        }
        else
        {
            ImGui.Text("Navmesh is not installed. BOOOOO");
        }
    }
}
