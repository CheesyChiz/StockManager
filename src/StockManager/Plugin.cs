using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;
using System.Runtime.InteropServices;

namespace StockManager;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/stockmanager";
    private const string ShortCommand = "/sm";
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
    private bool exportSubmitted;
    private string status = "Waiting for Visland...";
    private string? activeRoute;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        services = pluginInterface.Create<Services>() ?? throw new InvalidOperationException("Dalamud services are unavailable.");
        adapter = new VislandAdapter(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MigrateConfiguration();
        services.Commands.AddHandler(Command, new CommandInfo((_, _) => windowOpen = true) { HelpMessage = "Open Stock Manager" });
        services.Commands.AddHandler(ShortCommand, new CommandInfo((_, _) => windowOpen = true) { HelpMessage = "Open Stock Manager" });
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
        services.Commands.RemoveHandler(ShortCommand);
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
    }

    private void OpenMainUi() => windowOpen = true;

    private void MigrateConfiguration()
    {
        if (config.Version >= 4) return;
        if (config.CompletionAction == CompletionAction.FarmAndExport)
        {
            if (config.LegacyBulkSellLimit.HasValue) config.BulkTarget = config.LegacyBulkSellLimit.Value;
            if (config.LegacySellLimits is { Count: > 0 }) config.Targets = new Dictionary<int, int>(config.LegacySellLimits);
        }
        config.LegacyBulkSellLimit = null;
        config.LegacySellLimits = null;
        config.Version = 4;
        Save();
    }

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);
        if (!adapter.TryGetSnapshot(out snapshot, out var error)) { status = error; snapshot = null; return; }
        if (snapshot == null) { status = "Visland returned no route data."; return; }
        InitializeDefaults(snapshot);
        if (!config.Enabled) { status = "Stopped"; return; }
        if (!services.ClientState.IsLoggedIn) { status = "Log in and travel to Island Sanctuary before starting."; return; }
        if (TryGetExportValidationError(snapshot, out var validationError))
        {
            config.Enabled = false; adapter.Stop(); Save(); status = validationError; return;
        }
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
            if (snapshot.AutoExportEnabled)
            {
                if (!adapter.TryDisableBuiltInAutoExport(out error))
                {
                    status = $"Could not take over Visland Auto Export: {error}";
                    return;
                }
                status = "Stock Manager took over surplus selling.";
                return;
            }
            var cowrieRoute = SelectCowrieRoute(snapshot);
            if (cowrieRoute == null) { status = "No compatible unlocked resource routes are enabled."; return; }
            var exportDue = ManagedItems(snapshot).Where(IsExportDue).OrderByDescending(x => x.CurrentCount - config.Targets[x.Id]).FirstOrDefault();
            if (exportDue != null)
            {
                if (adapter.TryStartExportTrip(out error))
                {
                    exportTrip = true; exportSubmitted = false; exportTripStarted = DateTime.UtcNow; closeExportAfter = DateTime.MaxValue;
                    activeRoute = "Export materials";
                    status = $"{exportDue.Name} reached {exportDue.CurrentCount}; going to export configured surplus.";
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
            .Where(x => config.Targets.TryGetValue(x.Item.Id, out var target) && target is > 0 and < 999)
            .Where(x => x.Item.CurrentCount < ExportTrigger(x.Item.Id))
            .OrderBy(x => (double)x.Item.CurrentCount / Math.Max(1, ExportTrigger(x.Item.Id)))
            .ThenByDescending(x => x.Item.PerLoop).Select(x => ((RouteSnapshot, ItemSnapshot)?)x).FirstOrDefault();

    private int ExportTrigger(int itemId) => config.Targets[itemId] + config.ExportBatch;
    private bool IsExportDue(ItemSnapshot item) => item.IsAvailable && config.Targets.TryGetValue(item.Id, out var target)
        && target is > 0 and < 999 && item.CurrentCount >= ExportTrigger(item.Id);

    private bool TryGetExportValidationError(VislandSnapshot data, out string error)
    {
        error = string.Empty;
        if (config.CompletionAction != CompletionAction.FarmAndExport) return false;
        var invalid = ManagedItems(data)
            .Where(x => config.Targets.TryGetValue(x.Id, out var target) && target is > 0 and < 999)
            .Select(x => new { Item = x, Target = config.Targets[x.Id], Total = config.Targets[x.Id] + config.ExportBatch })
            .Where(x => x.Total > 999).OrderByDescending(x => x.Total).FirstOrDefault();
        if (invalid == null) return false;
        error = $"Invalid export settings: {invalid.Item.Name} uses {invalid.Target} + {config.ExportBatch} = {invalid.Total}; maximum is 999.";
        return true;
    }

    private IEnumerable<RouteSnapshot> CompatibleRoutes(VislandSnapshot data) => data.Routes
        .Where(x => !config.ExcludedRoutes.Contains(x.Name))
        .Where(x => config.MovementMode == RouteMovementMode.GroundAndFlying || !x.RequiresFlying);

    private static IEnumerable<ItemSnapshot> UniqueItems(VislandSnapshot data) =>
        data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First());

    private IEnumerable<ItemSnapshot> ManagedItems(VislandSnapshot data) =>
        CompatibleRoutes(data).SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First());

    private double RouteUtility(RouteSnapshot route) => route.Items.Sum(item =>
        item.IsAvailable && config.Targets.TryGetValue(item.Id, out var target) && target > 0 && item.CurrentCount < target
            ? ((double)(target - item.CurrentCount) / target) * item.PerLoop : 0);

    private void InitializeDefaults(VislandSnapshot data)
    {
        var changed = false;
        foreach (var item in UniqueItems(data))
        {
            if (config.Targets.TryAdd(item.Id, config.BulkTarget)) changed = true;
        }
        if (changed) Save();
    }

    private void Draw()
    {
        if (!windowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(1100, 680), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Stock Manager###StockManager", ref windowOpen)) { ImGui.End(); return; }

        ImGui.TextColored(config.Enabled ? new Vector4(.35f, .9f, .45f, 1) : new Vector4(.7f, .7f, .7f, 1), config.Enabled ? "ACTIVE" : "STOPPED");
        ImGui.SameLine(); ImGui.TextWrapped(status);
        if (ImGui.Button(config.Enabled ? "Stop automation" : "Start automation"))
        {
            var wantsEnabled = !config.Enabled;
            if (wantsEnabled && snapshot != null && TryGetExportValidationError(snapshot, out var error))
                status = error;
            else
            {
                config.Enabled = wantsEnabled; exportTrip = false; activeRoute = null; nextStartAttempt = DateTime.MinValue;
                if (!config.Enabled) adapter.Stop(); Save();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Emergency stop")) { config.Enabled = false; exportTrip = false; adapter.Stop(); Save(); }
        ImGui.Separator();

        DrawBehavior();
        ImGui.Separator();
        if (snapshot == null) ImGui.TextWrapped("Install Visland and import gathering routes into its Island group.");
        else if (ImGui.BeginTable("MainPanels", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Resources", ImGuiTableColumnFlags.WidthStretch, 1.35f);
            ImGui.TableSetupColumn("Routes", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextColumn(); ImGui.TextUnformatted("Resources"); ImGui.Separator(); DrawTargets(snapshot);
            ImGui.TableNextColumn(); ImGui.TextUnformatted("Routes"); ImGui.Separator();
            if (ImGui.BeginChild("RoutesPanel", new Vector2(0, -1), true)) DrawRoutes(snapshot);
            ImGui.EndChild(); ImGui.EndTable();
        }
        ImGui.End();
    }

    private void DrawTargets(VislandSnapshot data)
    {
        var targetLabel = config.CompletionAction == CompletionAction.Stop ? "Target stock" : "Sell above";
        var bulk = config.BulkTarget; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt($"{targetLabel} for all", ref bulk)) { config.BulkTarget = Math.Clamp(bulk, 0, 999); Save(); }
        ImGui.SameLine(); if (ImGui.Button("Apply##farm"))
        { foreach (var id in config.Targets.Keys.ToList()) config.Targets[id] = config.BulkTarget; Save(); }
        if (config.CompletionAction == CompletionAction.FarmAndExport)
        {
            ImGui.SameLine(); var batch = config.ExportBatch; ImGui.SetNextItemWidth(75);
            if (ImGui.InputInt("Export batch", ref batch)) { config.ExportBatch = Math.Clamp(batch, 1, 999); Save(); }
            ImGui.TextDisabled("Visit at Sell above + batch; export back down to Sell above.");
            if (TryGetExportValidationError(data, out var error)) ImGui.TextColored(new Vector4(1f, .3f, .3f, 1), error);
        }

        if (!ImGui.BeginTable("Targets", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, -1))) return;
        ImGui.TableSetupColumn("Resource"); ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(targetLabel, ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80); ImGui.TableHeadersRow();
        foreach (var item in UniqueItems(data).OrderBy(x => x.Name))
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (!item.IsAvailable) ImGui.TextDisabled($"{item.Name} (locked)"); else ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.CurrentCount.ToString());
            ImGui.TableNextColumn(); var target = config.Targets[item.Id]; ImGui.SetNextItemWidth(65);
            if (!item.IsAvailable) ImGui.BeginDisabled();
            if (ImGui.InputInt($"##target{item.Id}", ref target))
            { config.Targets[item.Id] = Math.Clamp(target, 0, 999); Save(); }
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
            status = "Opening Export Materials..."; return true;
        }
        var shop = (AtkUnitBase*)services.GameGui.GetAddonByName("MJIDisposeShop").Address;
        if (shop != null && shop->IsVisible)
        {
            if (!exportSubmitted)
            {
                var soldTypes = ExportConfiguredSurplus();
                if (soldTypes < 0) { status = "Waiting for exporter data..."; return true; }
                exportSubmitted = true; closeExportAfter = DateTime.UtcNow.AddSeconds(6);
                status = soldTypes == 0 ? "Nothing currently exceeds its sell limit." : $"Exporting {soldTypes} resource type(s)...";
            }
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

    private unsafe int ExportConfiguredSurplus()
    {
        var agent = AgentMJIDisposeShop.Instance();
        if (agent == null || agent->Data == null || !agent->Data->DataInitialized) return -1;
        var data = agent->Data;
        int seafarerCowries = data->CurrencyCounts[0], islanderCowries = data->CurrencyCounts[1];
        List<AtkValue> args = [new() { Type = AtkValueType.UInt }, new() { Type = AtkValueType.UInt, Int = 0 }];
        var soldTypes = 0;
        foreach (var entry in data->PerCategoryItems[0].AsSpan())
        {
            var item = entry.Value;
            if (item == null || !config.Targets.TryGetValue((int)item->ItemId, out var keep) || keep is <= 0 or >= 999) continue;
            var count = (int)InventoryManager.Instance()->GetInventoryItemCount(item->ItemId);
            var quantity = count - keep;
            if (quantity <= 0) continue;
            var value = item->CowriesPerItem * quantity;
            if (item->UseIslanderCowries)
            {
                if (islanderCowries + value > data->CurrencyStackSizes[1]) continue;
                islanderCowries += value;
            }
            else
            {
                if (seafarerCowries + value > data->CurrencyStackSizes[0]) continue;
                seafarerCowries += value;
            }
            args.Add(new() { Type = AtkValueType.UInt, UInt = item->ShopItemRowId });
            args.Add(new() { Type = AtkValueType.UInt, Int = quantity });
            soldTypes++;
        }
        if (soldTypes == 0) return 0;
        args[0] = new() { Type = AtkValueType.UInt, Int = soldTypes };
        var listener = *(AgentInterface**)((nint)agent + 0x18);
        var values = CollectionsMarshal.AsSpan(args);
        fixed (AtkValue* valuesPtr = values)
        {
            AtkValue result = new();
            listener->ReceiveEvent(&result, valuesPtr, (uint)values.Length, 0);
        }
        return soldTypes;
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
