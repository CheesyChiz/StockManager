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
    private DateTime nextTravelAttempt = DateTime.MinValue;
    private bool windowOpen;
    private bool exportTrip;
    private bool exportSubmitted;
    private bool travelRequested;
    private bool experimentalTestRunning;
    private string status = "Waiting for Visland...";
    private string? activeRoute;
    private PendingRouteStart? pendingRouteStart;
    private DateTime navigationStartedAt;
    private RouteSnapshot? experimentalRoute;
    private string experimentalStatus = "Uses enabled resources and nodes found in imported Visland routes.";
    private int experimentalNodeLimit = 18;
    private readonly Dictionary<int, int> sessionLastCounts = new();
    private readonly Dictionary<int, int> sessionCollected = new();
    private DateTime? sessionStartedAt;
    private DateTime? sessionEndedAt;
    private bool sessionTracking;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        services = pluginInterface.Create<Services>() ?? throw new InvalidOperationException("Dalamud services are unavailable.");
        adapter = new VislandAdapter(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MigrateConfiguration();
        services.Commands.AddHandler(Command, new CommandInfo(HandleCommand) { HelpMessage = "Open or control Stock Manager. Use /sm help for commands." });
        services.Commands.AddHandler(ShortCommand, new CommandInfo(HandleCommand) { HelpMessage = "Open or control Stock Manager. Use /sm help for commands." });
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

    private void HandleCommand(string _, string arguments)
    {
        var verb = arguments.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        switch (verb)
        {
            case "":
            case "open":
                windowOpen = true;
                break;
            case "start":
                TryStartAutomation();
                services.ChatGui.Print($"[Stock Manager] {status}");
                break;
            case "stop":
                StopAutomation(false, "Stopped by command.");
                services.ChatGui.Print("[Stock Manager] Automation stopped.");
                break;
            case "emergency":
                StopAutomation(true, "Emergency stop requested.");
                services.ChatGui.Print("[Stock Manager] Emergency stop completed.");
                break;
            case "travel":
                if (adapter.TryTravelToIsland(out var error))
                {
                    travelRequested = true;
                    status = "Lifestream is taking you to your Island Sanctuary...";
                }
                else status = $"Could not travel with Lifestream: {error}";
                services.ChatGui.Print($"[Stock Manager] {status}");
                break;
            case "status":
                PrintSessionStatus();
                break;
            case "help":
                services.ChatGui.Print("[Stock Manager] /sm [open|start|stop|status|travel|emergency|help]");
                break;
            default:
                services.ChatGui.Print($"[Stock Manager] Unknown command '{verb}'. Use /sm help.");
                break;
        }
    }

    private void MigrateConfiguration()
    {
        if (config.Version < 4)
        {
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
    }

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);
        if (!adapter.TryGetSnapshot(out snapshot, out var error)) { status = error; snapshot = null; return; }
        if (snapshot == null) { status = "Visland returned no route data."; return; }
        InitializeDefaults(snapshot);
        MigrateRouteSelections(snapshot);
        if (config.Enabled && sessionStartedAt == null) BeginSession(snapshot);
        UpdateSessionStats(snapshot);
        if (pendingRouteStart != null)
        {
            if (!services.ClientState.IsLoggedIn || snapshot.FlightUnlocked == null)
            {
                adapter.StopNavigation();
                pendingRouteStart = null;
                activeRoute = null;
                status = "Route start cancelled because the Island is no longer available.";
            }
            else HandlePendingRouteStart(snapshot);
            return;
        }
        if (experimentalTestRunning)
        {
            if (snapshot.IsRunning) status = $"Running experimental test: {activeRoute}";
            else
            {
                experimentalTestRunning = false;
                activeRoute = null;
                status = "Experimental test loop complete.";
                experimentalStatus = status;
            }
            return;
        }
        if (!config.Enabled) { status = "Stopped"; return; }
        if (!services.ClientState.IsLoggedIn) { status = "Log in and travel to Island Sanctuary before starting."; return; }
        if (snapshot.FlightUnlocked == null)
        {
            if (config.AutoTravelToIsland && !travelRequested && DateTime.UtcNow >= nextTravelAttempt)
            {
                if (adapter.TryTravelToIsland(out error))
                {
                    travelRequested = true;
                    status = "Lifestream is taking you to your Island Sanctuary...";
                }
                else
                {
                    status = $"Could not travel with Lifestream: {error}";
                    nextTravelAttempt = DateTime.UtcNow.AddSeconds(5);
                }
            }
            else if (travelRequested)
                status = adapter.IsLifestreamBusy ? "Lifestream is taking you to your Island Sanctuary..." : "Waiting to arrive on your Island Sanctuary...";
            else status = "Travel to your Island Sanctuary before starting.";
            return;
        }
        travelRequested = false;
        if (TryGetStartValidationError(snapshot, out var validationError))
        {
            config.Enabled = false; adapter.Stop(); EndSession(); Save(); status = validationError; return;
        }
        if (exportTrip && HandleExportTrip(snapshot)) return;
        if (snapshot.IsRunning) { status = activeRoute == null ? "Visland is running a route" : $"Running: {activeRoute}"; return; }
        if (DateTime.UtcNow < nextStartAttempt) return;

        var choice = SelectNextRoute(snapshot);
        if (choice == null)
        {
            if (config.CompletionAction == CompletionAction.Stop)
            {
                config.Enabled = false; activeRoute = null; EndSession(); Save();
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
                QueueRouteStart(adapter.CreateExportTripRoute(), exportDue.Name, PendingRoutePurpose.Export);
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
            choice = cowrieRoute;
        }

        QueueRouteStart(choice.Value.Route, choice.Value.Item.Name, PendingRoutePurpose.Farm);
        nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
    }

    private void TryStartAutomation()
    {
        if (config.Enabled)
        {
            status = "Automation is already active.";
            return;
        }
        if (snapshot == null)
        {
            status = "Waiting for Visland route data.";
            return;
        }
        if (TryGetStartValidationError(snapshot, out var error))
        {
            status = error;
            return;
        }

        config.Enabled = true;
        exportTrip = false;
        travelRequested = false;
        activeRoute = null;
        pendingRouteStart = null;
        experimentalTestRunning = false;
        nextStartAttempt = DateTime.MinValue;
        BeginSession(snapshot);
        Save();
        status = snapshot.FlightUnlocked == null && config.AutoTravelToIsland
            ? "Starting automation and preparing Island travel..."
            : "Automation started.";
    }

    private void StopAutomation(bool emergency, string message)
    {
        var wasTravelRequested = travelRequested;
        config.Enabled = false;
        exportTrip = false;
        exportSubmitted = false;
        travelRequested = false;
        pendingRouteStart = null;
        experimentalTestRunning = false;
        activeRoute = null;
        adapter.Stop();
        if (emergency || wasTravelRequested) adapter.AbortLifestream();
        EndSession();
        Save();
        status = message;
    }

    private bool QueueRouteStart(RouteSnapshot route, string? itemName, PendingRoutePurpose purpose)
    {
        var player = services.Objects.LocalPlayer;
        if (player == null)
        {
            status = "The local player is unavailable.";
            if (purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return false;
        }
        if (!adapter.TryNavigateToStart(route, player.Position, out var error))
        {
            status = $"Could not navigate to the start of {route.Name}: {error}";
            if (purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return false;
        }

        pendingRouteStart = new PendingRouteStart(route, itemName, purpose);
        navigationStartedAt = DateTime.UtcNow;
        activeRoute = route.Name;
        status = $"Navigating with vnavmesh to the start of {route.Name}...";
        if (purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
        return true;
    }

    private void HandlePendingRouteStart(VislandSnapshot data)
    {
        var pending = pendingRouteStart;
        if (pending == null) return;
        if (DateTime.UtcNow - navigationStartedAt > TimeSpan.FromMinutes(2))
        {
            adapter.StopNavigation();
            pendingRouteStart = null;
            activeRoute = null;
            status = $"Timed out navigating to the start of {pending.Route.Name}.";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        status = $"Navigating with vnavmesh to the start of {pending.Route.Name}...";
        if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
        if (DateTime.UtcNow - navigationStartedAt < TimeSpan.FromSeconds(1) || data.IsRunning) return;

        var player = services.Objects.LocalPlayer;
        var arrivalRadius = Math.Max(5f, pending.Route.Start.Radius + 2f);
        if (player == null || Vector3.Distance(player.Position, pending.Route.Start.Position) > arrivalRadius)
        {
            pendingRouteStart = null;
            activeRoute = null;
            status = $"vnavmesh stopped before reaching the start of {pending.Route.Name}.";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        pendingRouteStart = null;
        if (!adapter.TryStartRoute(pending.Route, out var error))
        {
            activeRoute = null;
            status = $"Visland rejected start: {error}";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        switch (pending.Purpose)
        {
            case PendingRoutePurpose.Export:
                exportTrip = true;
                exportSubmitted = false;
                exportTripStarted = DateTime.UtcNow;
                closeExportAfter = DateTime.MaxValue;
                status = $"{pending.ItemName} reached its export threshold; going to export configured surplus.";
                break;
            case PendingRoutePurpose.Experimental:
                experimentalTestRunning = true;
                status = "Experimental test loop started in Visland. Use Emergency stop if needed.";
                experimentalStatus = status;
                break;
            default:
                status = $"Starting {pending.Route.Name} for {pending.ItemName}.";
                break;
        }
    }

    private void BeginSession(VislandSnapshot data)
    {
        sessionLastCounts.Clear();
        sessionCollected.Clear();
        foreach (var item in UniqueItems(data)) sessionLastCounts[item.Id] = item.CurrentCount;
        sessionStartedAt = DateTime.UtcNow;
        sessionEndedAt = null;
        sessionTracking = true;
    }

    private void UpdateSessionStats(VislandSnapshot data)
    {
        if (!sessionTracking) return;
        foreach (var item in UniqueItems(data))
        {
            if (sessionLastCounts.TryGetValue(item.Id, out var previous) && item.CurrentCount > previous)
                sessionCollected[item.Id] = sessionCollected.GetValueOrDefault(item.Id) + item.CurrentCount - previous;
            sessionLastCounts[item.Id] = item.CurrentCount;
        }
    }

    private void EndSession()
    {
        if (sessionTracking) sessionEndedAt = DateTime.UtcNow;
        sessionTracking = false;
    }

    private void PrintSessionStatus()
    {
        services.ChatGui.Print($"[Stock Manager] {status}" + (activeRoute == null ? string.Empty : $" Route: {activeRoute}."));
        if (sessionStartedAt == null)
        {
            services.ChatGui.Print("[Stock Manager] No collection session has been started yet.");
            return;
        }

        var end = sessionEndedAt ?? DateTime.UtcNow;
        var duration = end - sessionStartedAt.Value;
        var names = snapshot == null
            ? new Dictionary<int, string>()
            : UniqueItems(snapshot).ToDictionary(x => x.Id, x => x.Name);
        var collected = sessionCollected.Where(x => x.Value > 0).OrderByDescending(x => x.Value)
            .Select(x => $"{names.GetValueOrDefault(x.Key, x.Key.ToString())} +{x.Value}").ToList();
        var total = sessionCollected.Values.Sum();
        var elapsed = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        if (collected.Count == 0)
        {
            services.ChatGui.Print($"[Stock Manager] This run: {elapsed}; nothing collected yet.");
            return;
        }
        services.ChatGui.Print($"[Stock Manager] This run: {elapsed}, {total} total.");
        foreach (var group in collected.Chunk(6))
            services.ChatGui.Print($"[Stock Manager] {string.Join(", ", group)}.");
    }

    private void MigrateRouteSelections(VislandSnapshot data)
    {
        if (config.Version >= 6) return;
        var oldRoutes = data.Routes.Where(x => config.LegacyExcludedRoutes?.Contains(x.Name) != true);
        if (config.LegacyMovementMode != 1) oldRoutes = oldRoutes.Where(x => !x.RequiresFlying);
        config.EnabledItems = oldRoutes.SelectMany(x => x.Items).Select(x => x.Id).Distinct()
            .Where(x => config.Targets.TryGetValue(x, out var target) && target > 0).ToHashSet();
        config.LegacyExcludedRoutes = null;
        config.LegacyMovementMode = null;
        config.Version = 6;
        Save();
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectNextRoute(VislandSnapshot data)
    {
        var items = ManagedItems(data).Where(x => x.IsAvailable)
            .Where(x => config.Targets[x.Id] > x.CurrentCount)
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
            .Where(x => config.EnabledItems.Contains(x.Item.Id) && config.Targets.TryGetValue(x.Item.Id, out var target) && target is > 0 and < 999)
            .Where(x => x.Item.CurrentCount < ExportTrigger(x.Item.Id))
            .OrderBy(x => (double)x.Item.CurrentCount / Math.Max(1, ExportTrigger(x.Item.Id)))
            .ThenByDescending(x => x.Item.PerLoop).Select(x => ((RouteSnapshot, ItemSnapshot)?)x).FirstOrDefault();

    private int ExportTrigger(int itemId) => config.Targets[itemId] + config.ExportBatch;
    private bool IsExportDue(ItemSnapshot item) => item.IsAvailable && config.EnabledItems.Contains(item.Id) && config.Targets.TryGetValue(item.Id, out var target)
        && target is > 0 and < 999 && item.CurrentCount >= ExportTrigger(item.Id);

    private bool TryGetExportValidationError(VislandSnapshot data, out string error)
    {
        error = string.Empty;
        if (config.CompletionAction != CompletionAction.FarmAndExport) return false;
        var invalid = ManagedItems(data)
            .Where(x => x.IsAvailable && config.Targets.TryGetValue(x.Id, out var target) && target > 0)
            .Select(x => new { Item = x, Target = config.Targets[x.Id], Total = config.Targets[x.Id] + config.ExportBatch })
            .Where(x => x.Total > 999).OrderByDescending(x => x.Total).FirstOrDefault();
        if (invalid == null) return false;
        error = $"Invalid export settings: {invalid.Item.Name} uses {invalid.Target} + {config.ExportBatch} = {invalid.Total}; maximum is 999.";
        return true;
    }

    private bool TryGetStartValidationError(VislandSnapshot data, out string error)
    {
        if (TryGetExportValidationError(data, out error)) return true;
        if (config.EnabledItems.Count == 0)
        {
            error = "Enable at least one resource before starting.";
            return true;
        }
        if (data.FlightUnlocked != null && !ManagedItems(data).Any(x => x.IsAvailable))
        {
            error = "No enabled resource is currently unlocked and served by a compatible imported route.";
            return true;
        }
        error = string.Empty;
        return false;
    }

    private IEnumerable<RouteSnapshot> CompatibleRoutes(VislandSnapshot data) => data.Routes
        .Where(x => data.FlightUnlocked == true || !x.RequiresFlying);

    private static IEnumerable<ItemSnapshot> UniqueItems(VislandSnapshot data) =>
        data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First());

    private IEnumerable<ItemSnapshot> ManagedItems(VislandSnapshot data) => CompatibleRoutes(data)
        .SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First())
        .Where(x => config.EnabledItems.Contains(x.Id) && config.Targets.TryGetValue(x.Id, out var target) && target > 0);

    private double RouteUtility(RouteSnapshot route) => route.Items.Sum(item =>
        item.IsAvailable && config.EnabledItems.Contains(item.Id) && config.Targets.TryGetValue(item.Id, out var target) && target > 0 && item.CurrentCount < target
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
            if (config.Enabled) StopAutomation(false, "Stopped");
            else TryStartAutomation();
        }
        ImGui.SameLine();
        if (ImGui.Button("Emergency stop")) StopAutomation(true, "Emergency stop requested.");
        ImGui.SameLine();
        var canTravel = snapshot?.FlightUnlocked == null && adapter.IsLifestreamAvailable && !adapter.IsLifestreamBusy;
        if (!canTravel) ImGui.BeginDisabled();
        if (ImGui.Button("Travel to Island"))
        {
            if (adapter.TryTravelToIsland(out var error)) { travelRequested = true; status = "Lifestream is taking you to your Island Sanctuary..."; }
            else status = $"Could not travel with Lifestream: {error}";
        }
        if (!canTravel) ImGui.EndDisabled();
        ImGui.Separator();

        DrawBehavior();
        ImGui.Separator();
        if (snapshot == null) ImGui.TextWrapped("Install Visland and import gathering routes into its Island group.");
        else if (ImGui.BeginTable("MainPanels", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Resources", ImGuiTableColumnFlags.WidthStretch, 1.35f);
            ImGui.TableSetupColumn("Automatic routes", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextColumn(); ImGui.TextUnformatted("Resources"); ImGui.Separator(); DrawTargets(snapshot);
            ImGui.TableNextColumn(); ImGui.TextUnformatted("Automatic routes"); ImGui.Separator();
            if (ImGui.BeginChild("RoutesPanel", new Vector2(0, -1), true)) DrawRoutes(snapshot);
            ImGui.EndChild(); ImGui.EndTable();
        }
        ImGui.End();
    }

    private void DrawTargets(VislandSnapshot data)
    {
        var targetLabel = config.CompletionAction == CompletionAction.Stop ? "Target stock" : "Sell above";
        var compatibleRoutes = CompatibleRoutes(data).ToList();
        var allItemIds = UniqueItems(data).Select(x => x.Id).ToList();
        var selectableIds = UniqueItems(data).Where(x => x.IsAvailable && compatibleRoutes.Any(route => route.Items.Any(y => y.Id == x.Id)))
            .Select(x => x.Id).ToList();
        var allEnabled = selectableIds.Count > 0 && selectableIds.All(config.EnabledItems.Contains);
        if (ImGui.Checkbox("Enable all available resources", ref allEnabled))
        {
            foreach (var id in allEnabled ? selectableIds : allItemIds)
            {
                if (allEnabled) config.EnabledItems.Add(id); else config.EnabledItems.Remove(id);
            }
            Save();
        }
        var bulk = config.BulkTarget; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt($"{targetLabel} for all", ref bulk)) { config.BulkTarget = Math.Clamp(bulk, 1, 999); Save(); }
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
            var enabled = config.EnabledItems.Contains(item.Id);
            var hasCompatibleRoute = compatibleRoutes.Any(route => route.Items.Any(x => x.Id == item.Id));
            var canFarm = item.IsAvailable && hasCompatibleRoute;
            if (!canFarm) ImGui.BeginDisabled();
            if (ImGui.Checkbox($"##enabled{item.Id}", ref enabled))
            {
                if (enabled) config.EnabledItems.Add(item.Id); else config.EnabledItems.Remove(item.Id);
                Save();
            }
            if (!canFarm) ImGui.EndDisabled();
            ImGui.SameLine();
            if (!item.IsAvailable) ImGui.TextDisabled($"{item.Name} (tool locked)");
            else if (!hasCompatibleRoute) ImGui.TextDisabled($"{item.Name} (flight unavailable)");
            else ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.CurrentCount.ToString());
            ImGui.TableNextColumn(); var target = config.Targets[item.Id]; ImGui.SetNextItemWidth(65);
            if (!item.IsAvailable) ImGui.BeginDisabled();
            if (ImGui.InputInt($"##target{item.Id}", ref target))
            { config.Targets[item.Id] = Math.Clamp(target, 1, 999); Save(); }
            if (!item.IsAvailable) ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(!canFarm ? "ignored" : !enabled ? "off" : item.CurrentCount >= target ? "done" : $"{item.CurrentCount * 100 / target}%");
        }
        ImGui.EndTable();
    }

    private void DrawBehavior()
    {
        var completion = (int)config.CompletionAction; ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("When targets are complete", ref completion, "Stop\0Farm and export for cowries\0")) { config.CompletionAction = (CompletionAction)completion; Save(); }
        var autoTravel = config.AutoTravelToIsland;
        if (!adapter.IsLifestreamAvailable) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Travel to Island with Lifestream when starting", ref autoTravel)) { config.AutoTravelToIsland = autoTravel; Save(); }
        if (!adapter.IsLifestreamAvailable) ImGui.EndDisabled();
        if (!adapter.IsLifestreamAvailable) ImGui.TextDisabled("Optional: install Lifestream to enable Island travel.");
        if (config.CompletionAction != CompletionAction.FarmAndExport) return;
    }

    private void DrawRoutes(VislandSnapshot data)
    {
        var compatible = CompatibleRoutes(data).ToList();
        ImGui.TextWrapped("Routes are selected automatically for enabled resources.");
        if (data.FlightUnlocked == true) ImGui.TextDisabled("Island flight is unlocked; ground and flying routes are available.");
        else if (data.FlightUnlocked == false) ImGui.TextDisabled("Island flight is locked; flying routes and their exclusive resources are ignored.");
        else ImGui.TextDisabled("Travel to your Island to detect flight access; only ground routes are available until then.");
        ImGui.TextDisabled($"Considering {compatible.Count} of {data.Routes.Count} imported Island routes.");
        ImGui.Spacing();
        foreach (var item in UniqueItems(data).Where(x => config.EnabledItems.Contains(x.Id)).OrderBy(x => x.Name))
        {
            var candidates = compatible.Select(route => (Route: route, Item: route.Items.FirstOrDefault(x => x.Id == item.Id)))
                .Where(x => x.Item != null).OrderByDescending(x => x.Item!.PerLoop).ThenByDescending(x => RouteUtility(x.Route)).ToList();
            if (candidates.Count == 0) ImGui.TextColored(new Vector4(1f, .45f, .3f, 1), $"{item.Name}: no compatible route");
            else
            {
                var best = candidates[0];
                ImGui.TextUnformatted($"{item.Name}: {best.Route.Name}");
                ImGui.TextDisabled($"  best of {candidates.Count}; about {best.Item!.PerLoop} node(s) per loop");
            }
        }

        ImGui.Spacing(); ImGui.Separator();
        DrawExperimentalRouteGenerator(data);
    }

    private void DrawExperimentalRouteGenerator(VislandSnapshot data)
    {
        if (!ImGui.CollapsingHeader("Experimental route generator")) return;
        ImGui.TextWrapped("Builds a temporary mixed-resource route from gathering nodes already present in imported Visland routes.");
        ImGui.TextColored(new Vector4(1f, .75f, .25f, 1), "Experimental: inspect and test the result before relying on it.");
        var limit = experimentalNodeLimit; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt("Maximum nodes", ref limit)) experimentalNodeLimit = Math.Clamp(limit, 11, 30);
        ImGui.TextDisabled("At least 11 unique nodes are used to support Island node respawns.");

        var canGenerate = data.FlightUnlocked != null && !config.Enabled && !data.IsRunning
                          && pendingRouteStart == null && !experimentalTestRunning;
        if (!canGenerate) ImGui.BeginDisabled();
        if (ImGui.Button("Generate preview")) GenerateExperimentalRoute(data);
        if (!canGenerate) ImGui.EndDisabled();
        ImGui.SameLine();
        var canRun = canGenerate && experimentalRoute != null && adapter.IsNavmeshReady;
        if (!canRun) ImGui.BeginDisabled();
        if (ImGui.Button("Run one test loop") && experimentalRoute != null)
        {
            QueueRouteStart(experimentalRoute, null, PendingRoutePurpose.Experimental);
        }
        if (!canRun) ImGui.EndDisabled();
        ImGui.TextWrapped(experimentalStatus);
        if (!adapter.IsNavmeshReady) ImGui.TextDisabled("vnavmesh must be installed and ready to test a generated route.");
    }

    private void GenerateExperimentalRoute(VislandSnapshot data)
    {
        experimentalRoute = null;
        var compatible = CompatibleRoutes(data).ToList();
        var activeItems = UniqueItems(data)
            .Where(x => x.IsAvailable && config.EnabledItems.Contains(x.Id) && config.Targets.TryGetValue(x.Id, out var target) && target > 0)
            .Where(x => compatible.Any(route => route.Items.Any(item => item.Id == x.Id)))
            .OrderBy(x => (double)x.CurrentCount / Math.Max(1, config.Targets[x.Id])).ToList();
        if (activeItems.Count == 0)
        {
            experimentalStatus = "Enable at least one unlocked resource with a compatible route.";
            return;
        }

        var availableIds = UniqueItems(data).Where(x => x.IsAvailable).Select(x => x.Id).ToHashSet();
        var allNodes = compatible.SelectMany(x => x.Nodes).Where(x => x.ItemIds.Any(availableIds.Contains))
            .GroupBy(NodeKey).Select(x => x.First()).ToList();
        var activeIds = activeItems.Select(x => x.Id).ToHashSet();
        var targetNodes = allNodes.Where(x => x.ItemIds.Any(activeIds.Contains)).ToList();
        if (targetNodes.Count == 0)
        {
            experimentalStatus = "No usable gathering nodes were found for the enabled resources.";
            return;
        }

        var selected = SelectBalancedNodes(targetNodes, activeItems, experimentalNodeLimit);
        while (selected.Count < 11)
        {
            var support = allNodes.Where(x => !selected.Contains(x))
                .OrderBy(x => selected.Min(y => Vector3.Distance(x.Position, y.Position))).FirstOrDefault();
            if (support == null) break;
            selected.Add(support);
        }
        if (selected.Count < 11)
        {
            experimentalStatus = $"Only {selected.Count} unique nodes are available; at least 11 are required for a stable loop.";
            return;
        }

        var ordered = OptimizeCycle(selected);
        experimentalRoute = adapter.CreateGeneratedRoute(ordered, data.FlightUnlocked == true);
        var wantedNodes = ordered.Count(x => x.ItemIds.Any(activeIds.Contains));
        experimentalStatus = $"Preview ready: {ordered.Count} nodes ({wantedNodes} target, {ordered.Count - wantedNodes} respawn support), "
                             + $"~{CycleLength(ordered):F0} yalms straight-line cycle. This preview is not saved to Visland.";
    }

    private static List<RouteNodeSnapshot> SelectBalancedNodes(List<RouteNodeSnapshot> candidates, List<ItemSnapshot> activeItems, int limit)
    {
        if (candidates.Count <= limit) return candidates.ToList();
        var selected = new List<RouteNodeSnapshot>();
        while (selected.Count < limit)
        {
            var added = false;
            foreach (var item in activeItems)
            {
                var next = candidates.Where(x => !selected.Contains(x) && x.ItemIds.Contains(item.Id))
                    .OrderBy(x => selected.Count == 0 ? 0 : selected.Min(y => Vector3.Distance(x.Position, y.Position))).FirstOrDefault();
                if (next == null) continue;
                selected.Add(next); added = true;
                if (selected.Count == limit) break;
            }
            if (!added) break;
        }
        return selected;
    }

    private static List<RouteNodeSnapshot> OptimizeCycle(List<RouteNodeSnapshot> nodes)
    {
        List<RouteNodeSnapshot>? best = null;
        var bestLength = float.MaxValue;
        foreach (var start in nodes)
        {
            var remaining = nodes.Where(x => x != start).ToList();
            var route = new List<RouteNodeSnapshot> { start };
            while (remaining.Count > 0)
            {
                var next = remaining.OrderBy(x => Vector3.Distance(route[^1].Position, x.Position)).First();
                route.Add(next); remaining.Remove(next);
            }
            ImproveCycle(route);
            var length = CycleLength(route);
            if (length < bestLength) { best = route; bestLength = length; }
        }
        return best ?? nodes;
    }

    private static void ImproveCycle(List<RouteNodeSnapshot> route)
    {
        var improved = true;
        while (improved)
        {
            improved = false;
            for (var i = 1; i < route.Count - 1; i++)
            for (var k = i + 1; k < route.Count; k++)
            {
                var a = route[(i - 1 + route.Count) % route.Count]; var b = route[i];
                var c = route[k]; var d = route[(k + 1) % route.Count];
                var current = Vector3.Distance(a.Position, b.Position) + Vector3.Distance(c.Position, d.Position);
                var swapped = Vector3.Distance(a.Position, c.Position) + Vector3.Distance(b.Position, d.Position);
                if (swapped + .1f >= current) continue;
                route.Reverse(i, k - i + 1); improved = true;
            }
        }
    }

    private static float CycleLength(IReadOnlyList<RouteNodeSnapshot> route)
    {
        if (route.Count < 2) return 0;
        var result = 0f;
        for (var i = 0; i < route.Count; i++)
            result += Vector3.Distance(route[i].Position, route[(i + 1) % route.Count].Position);
        return result;
    }

    private static (uint ObjectId, int X, int Y, int Z) NodeKey(RouteNodeSnapshot node) =>
        (node.ObjectId, (int)MathF.Round(node.Position.X * 2), (int)MathF.Round(node.Position.Y * 2), (int)MathF.Round(node.Position.Z * 2));

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
            if (item == null || !config.EnabledItems.Contains((int)item->ItemId)
                || !config.Targets.TryGetValue((int)item->ItemId, out var keep) || keep is <= 0 or >= 999) continue;
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

    private enum PendingRoutePurpose
    {
        Farm,
        Export,
        Experimental,
    }

    private sealed record PendingRouteStart(RouteSnapshot Route, string? ItemName, PendingRoutePurpose Purpose);

    private sealed class Services
    {
        [PluginService] internal ICommandManager Commands { get; private init; } = null!;
        [PluginService] internal IFramework Framework { get; private init; } = null!;
        [PluginService] internal IClientState ClientState { get; private init; } = null!;
        [PluginService] internal IObjectTable Objects { get; private init; } = null!;
        [PluginService] internal IGameGui GameGui { get; private init; } = null!;
        [PluginService] internal IChatGui ChatGui { get; private init; } = null!;
    }
}
