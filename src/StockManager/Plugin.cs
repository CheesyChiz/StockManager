using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace StockManager;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/stockmanager";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Services services;
    private readonly Configuration config;
    private readonly VislandAdapter adapter;
    private VislandSnapshot? snapshot;
    private DateTime nextPoll = DateTime.MinValue;
    private DateTime nextStartAttempt = DateTime.MinValue;
    private DateTime exportTripStarted;
    private DateTime closeExportAfter = DateTime.MaxValue;
    private bool windowOpen;
    private bool exportTrip;
    private string status = "Waiting for Visland...";
    private string? activeRoute;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        services = pluginInterface.Create<Services>() ?? throw new InvalidOperationException("Dalamud services are unavailable.");
        adapter = new VislandAdapter(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        services.Commands.AddHandler(Command, new CommandInfo((_, _) => windowOpen = true) { HelpMessage = "Open Stock Manager" });
        services.Framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public string Name => "Stock Manager";

    public void Dispose()
    {
        config.Enabled = false;
        services.Framework.Update -= OnUpdate;
        services.Commands.RemoveHandler(Command);
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
    }

    private void OpenMainUi() => windowOpen = true;

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);
        if (!adapter.TryGetSnapshot(out snapshot, out var error)) { status = error; snapshot = null; return; }
        if (snapshot == null) { status = "Visland returned no route data."; return; }
        InitializeDefaults(snapshot);
        if (!config.Enabled) { status = "Stopped"; return; }
        if (!services.ClientState.IsLoggedIn) { status = "Log in and travel to Island Sanctuary before starting."; return; }
        if (exportTrip && HandleExportTrip(snapshot)) return;
        if (snapshot.IsRunning) { status = activeRoute == null ? "Visland is running a route" : $"Running: {activeRoute}"; return; }
        if (DateTime.UtcNow < nextStartAttempt) return;

        var choice = SelectNextRoute(snapshot);
        if (choice == null)
        {
            if (config.CompletionAction == CompletionAction.Stop)
            {
                config.Enabled = false; activeRoute = null; Save();
                status = "All available targets have been reached.";
                return;
            }
            if (!snapshot.AutoExportEnabled)
            {
                status = "Enable Auto Export in Visland before using Farm and export.";
                return;
            }
            var cowrieRoute = SelectCowrieRoute(snapshot);
            if (cowrieRoute == null) { status = "No compatible unlocked resource routes are enabled."; return; }
            var highest = UniqueItems(snapshot).Where(x => x.IsAvailable).Select(x => x.CurrentCount).DefaultIfEmpty().Max();
            if (highest >= config.ExportTrigger)
            {
                if (adapter.TryStartExportTrip(out error))
                {
                    exportTrip = true; exportTripStarted = DateTime.UtcNow; closeExportAfter = DateTime.MaxValue;
                    activeRoute = "Export materials";
                    status = $"Resource reached {highest}; going to export surplus above {snapshot.AutoExportLimit}.";
                }
                else status = $"Could not start export trip: {error}";
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
            choice = cowrieRoute;
        }

        if (adapter.TryStartRoute(choice.Value.Route, out error))
        {
            activeRoute = choice.Value.Route.Name;
            status = $"Starting {activeRoute} for {choice.Value.Item.Name}.";
        }
        else status = $"Visland rejected start: {error}";
        nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectNextRoute(VislandSnapshot data)
    {
        var items = UniqueItems(data).Where(x => x.IsAvailable)
            .Where(x => config.Targets.TryGetValue(x.Id, out var target) && target > x.CurrentCount)
            .OrderBy(x => (double)x.CurrentCount / config.Targets[x.Id]).ThenBy(x => x.CurrentCount);
        foreach (var item in items)
        {
            var route = CompatibleRoutes(data)
                .Select(x => (Route: x, Item: x.Items.FirstOrDefault(y => y.Id == item.Id)))
                .Where(x => x.Item is { PerLoop: > 0 }).OrderByDescending(x => x.Item!.PerLoop)
                .ThenByDescending(x => RouteUtility(x.Route)).FirstOrDefault();
            if (route.Item != null) return (route.Route, item);
        }
        return null;
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectCowrieRoute(VislandSnapshot data) =>
        CompatibleRoutes(data).SelectMany(route => route.Items.Where(x => x.IsAvailable).Select(item => (Route: route, Item: item)))
            .Where(x => x.Item.CurrentCount < config.ExportTrigger).OrderBy(x => x.Item.CurrentCount)
            .ThenByDescending(x => x.Item.PerLoop).Select(x => ((RouteSnapshot, ItemSnapshot)?)x).FirstOrDefault();

    private IEnumerable<RouteSnapshot> CompatibleRoutes(VislandSnapshot data) => data.Routes
        .Where(x => !config.ExcludedRoutes.Contains(x.Name))
        .Where(x => config.MovementMode == RouteMovementMode.GroundAndFlying || !x.RequiresFlying);

    private static IEnumerable<ItemSnapshot> UniqueItems(VislandSnapshot data) =>
        data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First());

    private double RouteUtility(RouteSnapshot route) => route.Items.Sum(item =>
        item.IsAvailable && config.Targets.TryGetValue(item.Id, out var target) && target > 0 && item.CurrentCount < target
            ? ((double)(target - item.CurrentCount) / target) * item.PerLoop : 0);

    private void InitializeDefaults(VislandSnapshot data)
    {
        var changed = false;
        foreach (var item in UniqueItems(data)) if (config.Targets.TryAdd(item.Id, config.BulkTarget)) changed = true;
        if (changed) Save();
    }

    private void Draw()
    {
        if (!windowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(720, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Stock Manager###StockManager", ref windowOpen)) { ImGui.End(); return; }

        ImGui.TextColored(config.Enabled ? new Vector4(.35f, .9f, .45f, 1) : new Vector4(.7f, .7f, .7f, 1), config.Enabled ? "ACTIVE" : "STOPPED");
        ImGui.SameLine(); ImGui.TextWrapped(status);
        if (ImGui.Button(config.Enabled ? "Stop automation" : "Start automation"))
        {
            config.Enabled = !config.Enabled; exportTrip = false; activeRoute = null; nextStartAttempt = DateTime.MinValue;
            if (!config.Enabled) adapter.Stop(); Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Emergency stop")) { config.Enabled = false; exportTrip = false; adapter.Stop(); Save(); }
        ImGui.Separator();

        if (ImGui.CollapsingHeader("1. Resource targets", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (snapshot == null) ImGui.TextWrapped("Install Visland and import gathering routes into its Island group.");
            else DrawTargets(snapshot);
        }
        if (ImGui.CollapsingHeader("2. Automation behavior", ImGuiTreeNodeFlags.DefaultOpen)) DrawBehavior();
        if (snapshot != null && ImGui.CollapsingHeader("3. Routes", ImGuiTreeNodeFlags.DefaultOpen)) DrawRoutes(snapshot);
        ImGui.End();
    }

    private void DrawTargets(VislandSnapshot data)
    {
        var bulk = config.BulkTarget;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Same target for every resource", ref bulk)) { config.BulkTarget = Math.Clamp(bulk, 0, 999); Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Apply")) { foreach (var id in config.Targets.Keys.ToList()) config.Targets[id] = config.BulkTarget; Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Set all to 0")) { foreach (var id in config.Targets.Keys.ToList()) config.Targets[id] = 0; Save(); }

        if (!ImGui.BeginTable("Targets", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 280))) return;
        ImGui.TableSetupColumn("Resource"); ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 100); ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90); ImGui.TableHeadersRow();
        foreach (var item in UniqueItems(data).OrderBy(x => x.Name))
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (!item.IsAvailable) ImGui.TextDisabled($"{item.Name} (locked)"); else ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.CurrentCount.ToString());
            ImGui.TableNextColumn(); var target = config.Targets[item.Id]; ImGui.SetNextItemWidth(80);
            if (!item.IsAvailable) ImGui.BeginDisabled();
            if (ImGui.InputInt($"##target{item.Id}", ref target)) { config.Targets[item.Id] = Math.Clamp(target, 0, 999); Save(); }
            if (!item.IsAvailable) ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(!item.IsAvailable ? "ignored" : target <= 0 ? "disabled" : item.CurrentCount >= target ? "done" : $"{item.CurrentCount * 100 / target}%");
        }
        ImGui.EndTable();
    }

    private void DrawBehavior()
    {
        var movement = (int)config.MovementMode; ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Allowed routes", ref movement, "Ground only\0Ground and flying\0")) { config.MovementMode = (RouteMovementMode)movement; Save(); }
        var completion = (int)config.CompletionAction; ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("When targets are complete", ref completion, "Stop\0Farm and export for cowries\0")) { config.CompletionAction = (CompletionAction)completion; Save(); }
        if (config.CompletionAction != CompletionAction.FarmAndExport) return;
        var trigger = config.ExportTrigger; ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Visit exporter when any resource reaches", ref trigger)) { config.ExportTrigger = Math.Clamp(trigger, 1, 999); Save(); }
        if (snapshot != null) ImGui.TextWrapped(snapshot.AutoExportEnabled
            ? $"Visland Auto Export: enabled, keeps {snapshot.AutoExportLimit} normal materials."
            : "Visland Auto Export is disabled. Enable it in Visland's Exports window first.");
    }

    private void DrawRoutes(VislandSnapshot data)
    {
        ImGui.TextDisabled("Ground-only mode automatically ignores routes containing MountFly waypoints.");
        foreach (var route in data.Routes.OrderBy(x => x.RequiresFlying).ThenBy(x => x.Name))
        {
            var compatible = config.MovementMode == RouteMovementMode.GroundAndFlying || !route.RequiresFlying;
            var enabled = !config.ExcludedRoutes.Contains(route.Name);
            if (!compatible) ImGui.BeginDisabled();
            if (ImGui.Checkbox($"{(route.RequiresFlying ? "[Flying]" : "[Ground]")} {route.Name}##route", ref enabled))
            { if (enabled) config.ExcludedRoutes.Remove(route.Name); else config.ExcludedRoutes.Add(route.Name); Save(); }
            if (!compatible) ImGui.EndDisabled();
        }
    }

    private unsafe bool HandleExportTrip(VislandSnapshot data)
    {
        if (data.IsRunning) { status = "Going to the Island exporter..."; return true; }
        var select = (AtkUnitBase*)services.GameGui.GetAddonByName("SelectString").Address;
        if (select != null && select->IsVisible && select->IsReady)
        {
            var value = stackalloc AtkValue[1]; value[0].Type = AtkValueType.Int; value[0].Int = 0; select->FireCallback(1, value);
            closeExportAfter = DateTime.UtcNow.AddSeconds(6); status = "Visland Auto Export is selling the surplus."; return true;
        }
        var shop = (AtkUnitBase*)services.GameGui.GetAddonByName("MJIDisposeShop").Address;
        if (shop != null && shop->IsVisible)
        {
            if (DateTime.UtcNow >= closeExportAfter)
            {
                var value = stackalloc AtkValue[1]; value[0].Type = AtkValueType.Int; value[0].Int = 1; shop->FireCallback(1, value);
                exportTrip = false; activeRoute = null; nextStartAttempt = DateTime.UtcNow.AddSeconds(3); status = "Export complete; resuming farming.";
            }
            return true;
        }
        if (DateTime.UtcNow - exportTripStarted > TimeSpan.FromSeconds(30)) { exportTrip = false; status = "Export trip timed out; check access to the exporter."; }
        return exportTrip;
    }

    private void Save() => pluginInterface.SavePluginConfig(config);

    private sealed class Services
    {
        [PluginService] internal ICommandManager Commands { get; private init; } = null!;
        [PluginService] internal IFramework Framework { get; private init; } = null!;
        [PluginService] internal IClientState ClientState { get; private init; } = null!;
        [PluginService] internal IGameGui GameGui { get; private init; } = null!;
    }
}
