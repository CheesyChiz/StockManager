using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Callback = ECommons.Automation.Callback;

namespace ExplorersIcebox.Scheduler.Handers;

internal class GenericHandlers
{
    internal static bool? Throttle(string name, int ms) => EzThrottler.Throttle(name, ms);

    internal static bool? WaitFor(string name) => EzThrottler.Check(name);
    internal static unsafe bool? FireCallback(string AddonName, bool visibilty, params int[] callback_fires)
    {
        if (TryGetAddonByName(AddonName, out AtkUnitBase* addon) && IsAddonReady(addon))
        {
            Callback.Fire(addon, visibilty, callback_fires.Cast<object>().ToArray());
            return true;
        }
        return false;
    }
}
