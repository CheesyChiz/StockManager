using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Throttlers;
using ExplorersIcebox.Util;
using System.Collections.Generic;
namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_IslandInteract
{
    public static void Enqueue(List<Vector3> List, ulong gameObjectId, bool mount = false, bool fly = false)
    {
        P.taskManager.Enqueue(() => MoveToWaypoints(List, mount, fly), "Queueing Navmesh");
        P.taskManager.Enqueue(() => WaitForNavmesh(), "Waiting for Navmesh to Finish", Utils.TaskConfig);
        if (gameObjectId != 0)
        {
            P.taskManager.Enqueue(() => TargetV2(gameObjectId), $"Checking for target: {gameObjectId}");
            P.taskManager.Enqueue(() => GatherInteract(gameObjectId), $"If target exist, gathering {gameObjectId}");
        }
    }

    internal static bool? MoveToWaypoints(List<Vector3> waypoints, bool mount, bool fly)
    {
        if (PlayerHelper.GetDistanceToPlayer(waypoints[^1]) < 0.5f)
            return true;

        if (MovementHelper.TryStartNavmesh(waypoints, mount, fly))
            return true;

        return false;
    }

    internal static bool? WaitForNavmesh()
    {
        return !P.navmesh.IsRunning() ? true : false;
    }

    internal static bool? TargetV2(ulong gameObjectId)
    {
        Utils.TryGetObjectByGameObjectId(gameObjectId, out var gameObject);

        if (gameObject == null || gameObject.IsTarget() || !gameObject.IsTargetable)
            return true;

        if (EzThrottler.Throttle($"Targeting: {gameObjectId}"))
            Utils.TargetgameObject(gameObject);

        return false;
    }

    internal static bool? GatherInteract(ulong gameObjectId)
    {
        // Actual interaction itself
        // If a target exist and can be interacted with, will do so. Probably should add a safety distance check to this for users...

        IGameObject? gameObject = null;
        Utils.TryGetObjectByGameObjectId(gameObjectId, out gameObject);

        if (gameObject == null || !gameObject.IsTargetable)
        {
            // no object was found, exiting code and continuing route
            return true;
        }
        if (!Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            if (EzThrottler.Throttle("Interacting with Island Object"))
            {
                Utils.InteractWithObject(gameObject);
            }
        }

        return false;
    }
}
