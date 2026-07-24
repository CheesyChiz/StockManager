using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices.Legacy;
using ECommons.GameFunctions;
using ECommons.Logging;
using ECommons.Reflection;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
namespace ExplorersIcebox.Util;

/// <summary>
///     Misc of utilities that don't really belong in one place
/// </summary>
public class Utils
{
    public static TaskManagerConfiguration TaskConfig => new(10 * 60 * 3000, false);
    public static bool HasPlugin(string name) => DalamudReflector.TryGetDalamudPlugin(name, out var _, false, true);

    public static void TargetgameObject(IGameObject? gameObject)
    {
        if (gameObject == null || gameObject.IsTarget())
            return;

        if (EzThrottler.Throttle($"Throttle Targeting {gameObject.Name}"))
        {
            Svc.Targets.SetTarget(gameObject);
            PluginLog.Information($"Setting the target to {gameObject.Name}");
        }
    }

    internal static bool TryGetObjectByDataId(ulong dataId, out IGameObject? gameObject) =>
        (gameObject = Svc.Objects.FirstOrDefault(x => x.BaseId == dataId)) != null;

    internal static bool TryGetObjectByGameObjectId(ulong gameObjectId, out IGameObject? gameObject) =>
        (gameObject = Svc.Objects.FirstOrDefault(x => x.GameObjectId == gameObjectId)) != null;

    internal static unsafe void InteractWithObject(IGameObject? gameObject)
    {
        if (gameObject == null || !gameObject.IsTargetable)
            return;
        TargetSystem.Instance()->InteractWithObject(gameObject.Struct(), false);
    }

    public static void FancyCheckmark(bool enabled)
    {
        var columnWidth = ImGui.GetColumnWidth();
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();

        var iconSize = ImGui.CalcTextSize($"{FontAwesome.Cross}");

        var cursorX = ImGui.GetCursorPosX() + (columnWidth - iconSize.X) * 0.5f;
        var cursorY = ImGui.GetCursorPosY() + (rowHeight - iconSize.Y) * 0.5f;

        cursorX = Math.Max(cursorX, ImGui.GetCursorPosX());
        cursorY = Math.Max(cursorY, ImGui.GetCursorPosY());

        ImGui.SetCursorPos(new(cursorX, cursorY));

        if (enabled)
            FontAwesome.Print(ImGuiColors.HealerGreen, FontAwesome.Check);
        else
            FontAwesome.Print(ImGuiColors.DalamudRed, FontAwesome.Cross);
    }

    public static unsafe void OpenPlayerSearch(uint commandId)
    {
        UIModule.Instance()->ExecuteMainCommand(commandId);
    }

    public static unsafe void ShowText(int position, string text)
    {
        UIModule.Instance()->ShowText(position, text);
    }
}
