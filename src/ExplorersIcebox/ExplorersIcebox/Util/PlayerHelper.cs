using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
namespace ExplorersIcebox.Util;

public class PlayerHelper
{
    public static bool IsBetweenAreas => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];
    public static bool IsInZone(uint zoneID) => Svc.ClientState.TerritoryType == zoneID;
    public static unsafe uint CurrentTerritory() => GameMain.Instance()->CurrentTerritoryTypeId;

    public static bool IsPlayerNotBusy() =>
        Player.Available
        && Player.Object!.CastActionId == 0
        && !IsOccupied()
        && !Player.IsJumping
        && Player.Object.IsTargetable
        && !Player.IsAnimationLocked;

    public static unsafe float GetDistanceToPlayer(Vector3 v3) => Vector3.Distance(v3, Player.GameObject->Position);
    public static float GetDistanceToPlayer(IGameObject gameObject) => GetDistanceToPlayer(gameObject.Position);

    public static unsafe bool GetItemCount(int itemID, out int count)
    {
        var im = InventoryManager.Instance();
        count = im->GetInventoryItemCount((uint)itemID)
              + im->GetInventoryItemCount((uint)itemID + 500_000);
        return true;
    }
}
