using ExplorersIcebox.Enums;
using ExplorersIcebox.Scheduler.Tasks;
using ExplorersIcebox.Util;
using static ExplorersIcebox.Enums.IceBoxState;

namespace ExplorersIcebox.Scheduler;

internal static class SchedulerMain
{
    internal static IceBoxState State = Idle;
    internal static bool EnablePlugin()
    {
        IslandHelper.LoopCounter = 0;
        State = Start;
        return true;
    }
    internal static bool DisablePlugin()
    {
        IslandHelper.LoopCounter = 0;
        P.taskManager.Abort();
        P.navmesh.Stop();
        Task_SellItems.Reset();
        State = Idle;
        IslandHelper.ExternalLoopOverride = null;
        return true;
    }

    internal static void Tick()
    {
        if (Throttles.GenericThrottle && P.taskManager.NumQueuedTasks == 0 && State != Idle)
        {
            switch (State)
            {
                case Start:
                    Task_ReturnToBase.Enqueue();
                    break;
                case CheckSell:
                    Task_SellCheck.Enqueue();
                    break;
                case SellToNpc:
                    Svc.Log.Information("NPC Sell State Active");
                    Task_SellItems.Enqueue();
                    break;
                case LeavingSellNpc:
                    break;
                case RunRoute:
                    Svc.Log.Information("Run Route State");
                    Task_GatherLoop.Enqueue();
                    break;
                default:
                    Svc.Log.Information("Route has been completed, stopping");
                    DisablePlugin();
                    break;
            }
        }
    }
}
