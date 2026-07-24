using ExplorersIcebox.Enums;
using ExplorersIcebox.Util;
namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_GatherLoop
{
    public static void Enqueue()
    {
        Task_GatherMode.Enqueue();
        foreach (var wpList in IslandHelper.CurrentRoute.Value.BaseToLocation)
        {
            Task_BaseToGather.Enqueue(wpList.Waypoints, wpList.Mount, wpList.Fly);
        }

        var totalLoops = IslandHelper.GoalLoopAmount;
        if (C.RunMaxLoops)
            totalLoops = IslandHelper.MaxRouteLoops;
        if (IslandHelper.ExternalLoopOverride is { } externalLoops)
            totalLoops = externalLoops;
        for (var i = 0; i < totalLoops; i++)
        {
            foreach (var entry in IslandHelper.CurrentRoute.Value.RouteWaypoints)
            {
                Task_IslandInteract.Enqueue(entry.Waypoints, entry.TargetId, entry.Mount, entry.Fly);
            }
        }
        P.taskManager.Enqueue(() => CheckLoopCount(), "Checking loop count");
    }

    internal static bool? CheckLoopCount()
    {
        IslandHelper.LoopCounter += 1;
        if (C.RunMultiple && IslandHelper.LoopCounter < C.RunAmount)
            SchedulerMain.State = IceBoxState.Start;
        else
            SchedulerMain.State = IceBoxState.EndProcess;

        return true;
    }
}
