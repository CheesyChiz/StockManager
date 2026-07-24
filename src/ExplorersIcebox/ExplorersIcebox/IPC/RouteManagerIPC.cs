using Dalamud.Plugin.Ipc;
using ExplorersIcebox.Enums;
using ExplorersIcebox.Scheduler;
using ExplorersIcebox.Util;
using System.Collections.Generic;
using System.Text.Json;

namespace ExplorersIcebox.IPC;

internal sealed class RouteManagerIPC : IDisposable
{
    private const string Prefix = "ExplorersIcebox.RouteManager";
    private readonly ICallGateProvider<string> snapshot;
    private readonly ICallGateProvider<string, int, bool> startRoute;
    private readonly ICallGateProvider<object?> stop;

    public RouteManagerIPC()
    {
        snapshot = Svc.PluginInterface.GetIpcProvider<string>($"{Prefix}.Snapshot");
        startRoute = Svc.PluginInterface.GetIpcProvider<string, int, bool>($"{Prefix}.StartRoute");
        stop = Svc.PluginInterface.GetIpcProvider<object?>($"{Prefix}.Stop");

        snapshot.RegisterFunc(GetSnapshot);
        startRoute.RegisterFunc(StartRoute);
        stop.RegisterAction(Stop);
    }

    public void Dispose()
    {
        snapshot.UnregisterFunc();
        startRoute.UnregisterFunc();
        stop.UnregisterAction();
    }

    private static string GetSnapshot()
    {
        var routes = new List<RouteSnapshot>();
        foreach (var route in EmbedRoutes.Routes)
        {
            var items = BuildRouteItems(route.Value);
            routes.Add(new RouteSnapshot(route.Key, items.Values.OrderBy(x => x.Name).ToList()));
        }

        return JsonSerializer.Serialize(new Snapshot(
            1,
            SchedulerMain.State != IceBoxState.Idle,
            SchedulerMain.State.ToString(),
            routes));
    }

    private static bool StartRoute(string routeName, int loops)
    {
        if (loops < 1 || loops > 999 || SchedulerMain.State != IceBoxState.Idle || Svc.Objects.LocalPlayer == null)
            return false;

        if (!EmbedRoutes.Routes.TryGetValue(routeName, out var route))
            return false;

        IslandHelper.CurrentRoute = new(routeName, route);
        IslandHelper.ExternalLoopOverride = loops;
        IslandHelper.GoalLoopAmount = loops;
        IslandHelper.MaxRouteLoops = loops;
        IslandHelper.UpdateNumbers();
        IslandHelper.GoalLoopAmount = loops;
        return SchedulerMain.EnablePlugin();
    }

    private static void Stop() => SchedulerMain.DisablePlugin();

    private static Dictionary<int, ItemSnapshot> BuildRouteItems(Util.PathCreation.RouteClass.RouteUtil route)
    {
        var result = new Dictionary<int, ItemSnapshot>();
        foreach (var waypoint in route.RouteWaypoints)
        {
            if (waypoint.TargetId == 0)
                continue;

            var node = ItemData.IslandNodeInfo.FirstOrDefault(x => x.Nodes.Contains(waypoint.TargetId));
            if (node == null)
                continue;

            foreach (var itemId in node.ItemIds)
            {
                if (!ItemData.IslandItems.TryGetValue(itemId, out var item))
                    continue;

                if (!result.TryGetValue(itemId, out var existing))
                {
                    PlayerHelper.GetItemCount(itemId, out var count);
                    result[itemId] = new(itemId, item.ItemName, 1, count);
                }
                else
                {
                    result[itemId] = existing with { PerLoop = existing.PerLoop + 1 };
                }
            }
        }

        return result;
    }

    private sealed record Snapshot(int ApiVersion, bool IsRunning, string State, List<RouteSnapshot> Routes);
    private sealed record RouteSnapshot(string Name, List<ItemSnapshot> Items);
    private sealed record ItemSnapshot(int Id, string Name, int PerLoop, int CurrentCount);
}
