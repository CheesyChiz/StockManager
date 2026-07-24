using ExplorersIcebox.Ui.DebugWindowTabs;
namespace ExplorersIcebox.Ui;

internal class DebugWindow : Window
{
    private string[] debugTypes =
    [
        "Player Info", "Navmesh Debug", "Misc Info",
        "Route Sell", "Target Info", "Imgui Testing",
        "Island Node Finder", "Island Item Info", "Route Editor V4",
        "Simple Route Creator", "Picto Testing", "Shop Export"
    ];
    private int selectedDebugIndex; // This should be stored somewhere persistent
    public DebugWindow() : base("Explorer's IceBox Debug ###Explorer's Icebox Debug")
    {
        Flags = ImGuiWindowFlags.None;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(100, 100)
        };
        P.windowSystem.AddWindow(this);
    }

    public void Dispose()
    {
        P.windowSystem.RemoveWindow(this);
    }

    public override void Draw()
    {
        var spacing = 10f;
        var leftPanelWidth = 200f;
        var rightPanelWidth = ImGui.GetContentRegionAvail().X - leftPanelWidth - spacing;
        var childHeight = ImGui.GetContentRegionAvail().Y;

        if (ImGui.BeginChild("DebugSelector", new(leftPanelWidth, childHeight), true))
        {
            for (var i = 0; i < debugTypes.Length; i++)
            {
                var isSelected = (selectedDebugIndex == i);
                var label = isSelected ? $"→ {debugTypes[i]}" : $"   {debugTypes[i]}"; // Add space for alignment

                if (ImGui.Selectable(label, isSelected))
                {
                    selectedDebugIndex = i;
                }
            }
            ImGui.EndChild();
        }

        ImGui.SameLine(0, spacing);

        if (ImGui.BeginChild("DebugContent", new(rightPanelWidth, childHeight), true))
        {
            switch (selectedDebugIndex)
            {
                case 0: PlayerInfoDebug.Draw(); break;
                case 1: ImGui.Text("Need to fix navmesh info"); break;
                case 2: MiscInfoDebug.Draw(); break;
                case 3: RouteSellDebug.Draw(); break;
                case 4: TargetInfoDebug.Draw(); break;
                case 5:
                    TestGuiDebug.Draw();
                    EcomsTestingDebug.Draw();
                    break;
                case 6: IslandGatherPointData.GatherPointDataDraw(); break;
                case 7: IslandItemInfoDebug.Draw(); break;
                case 8: RouteEditorV4Debug.Draw(); break;
                case 9: BaseRouteEditor.Draw(); break;
                case 10: PictoTestDebug.Draw(); break;
                case 11: MJIShopUi.Draw(); break;
                default: ImGui.Text("Unknown Debug View"); break;
            }

            ImGui.EndChild();
        }
    }
}
