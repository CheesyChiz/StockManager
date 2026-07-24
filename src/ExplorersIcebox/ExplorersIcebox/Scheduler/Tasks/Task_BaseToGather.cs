using ExplorersIcebox.Util;
using System.Collections.Generic;
namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_BaseToGather
{
    public static void Enqueue(List<Vector3> BaseWPList, bool mount, bool fly)
    {
        P.taskManager.Enqueue(() => BaseToGather(BaseWPList, mount, fly), "Moving from base -> gather point", Utils.TaskConfig);
    }

    internal static bool? BaseToGather(List<Vector3> BaseWPList, bool mount, bool fly)
    {
        if (PlayerHelper.GetDistanceToPlayer(BaseWPList[^1]) < 0.5f)
            return true;

        if (!P.navmesh.IsRunning())
            MovementHelper.TryStartNavmesh(BaseWPList, mount, fly);

        return false;
    }
}
