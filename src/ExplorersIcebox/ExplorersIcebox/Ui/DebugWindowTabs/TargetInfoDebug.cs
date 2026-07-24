namespace ExplorersIcebox.Ui.DebugWindowTabs;

internal class TargetInfoDebug
{
    public static void Draw()
    {
        var target = Svc.Targets?.Target;

        if (target != null)
        {
            if (ImGui.Button($"Name: {target.Name}"))
            {
                ImGui.SetClipboardText($"GatherName = \"{target.Name}\",");
            }
            if (ImGui.Button($"Object ID: {target.GameObjectId}"))
            {
                ImGui.SetClipboardText($"{target.GameObjectId}");
            }
            if (ImGui.Button($"Data ID: {target.BaseId}"))
            {
                ImGui.SetClipboardText($"{target.BaseId}");
            }
        }
    }
}
