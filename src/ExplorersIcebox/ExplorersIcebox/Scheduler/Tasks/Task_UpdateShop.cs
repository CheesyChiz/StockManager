using ECommons.Throttlers;
using ExplorersIcebox.Util;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using Callback = ECommons.Automation.Callback;

namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_UpdateShop
{
    public static void Enqueue()
    {
        P.taskManager.Enqueue(() => OpenPouch(), "Opening MJI Pouch");
        P.taskManager.Enqueue(() => UpdateCallbacks(), "Updating item callbacks");
        P.taskManager.Enqueue(() => ClosePouch(), "Closing MJI Pouch");
    }

    internal static unsafe bool? OpenPouch()
    {
        if (TryGetAddonByName("MJIPouch", out AtkUnitBase* mjiPouch) && IsAddonReady(mjiPouch))
        {
            return true;
        }
        if (TryGetAddonMaster("MJIHud", out MJIHud mjiHud) && mjiHud.IsAddonReady)
        {
            if (EzThrottler.Throttle("Open MJI Inventory Pouch"))
            {
                mjiHud.Isleventory();
            }
        }

        return false;
    }

    internal static unsafe bool? UpdateCallbacks()
    {
        if (TryGetAddonByName("MJIPouch", out AtkUnitBase* mjiPouch) && IsAddonReady(mjiPouch))
        {
            IslandHelper.UpdateShopCallback();
            return true;
        }

        return false;
    }

    internal static unsafe bool? ClosePouch()
    {
        if (TryGetAddonByName("MJIPouch", out AtkUnitBase* mjiPouch))
        {
            if (IsAddonReady(mjiPouch))
            {
                if (EzThrottler.Throttle("Closing the pouch"))
                    Callback.Fire(mjiPouch, true, 1);
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
