using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MountSheet = Lumina.Excel.Sheets.Mount;
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
    private bool settingsRequested;
    private bool exportTrip;
    private bool exportSubmitted;
    private bool travelRequested;
    private bool experimentalTestRunning;
    private string status = "Waiting for Visland...";
    private string? activeRoute;
    private PendingRouteStart? pendingRouteStart;
    private DateTime navigationStartedAt;
    private DateTime navigationRequestedAt;
    private DateTime nextMountAttempt;
    private DateTime nextDiveAttempt;
    private DiveDelegate? dive;
    private Hook<ActionManager.Delegates.UseAction>? useActionHook;
    private RouteSnapshot? experimentalRoute;
    private string experimentalStatus = "Uses enabled resources and nodes found in imported Visland routes.";
    private int experimentalNodeLimit = 24;
    private readonly Dictionary<int, int> sessionLastCounts = new();
    private readonly Dictionary<int, int> sessionCollected = new();
    private readonly HashSet<int> completedStopItems = new();
    private DateTime? sessionStartedAt;
    private DateTime? sessionEndedAt;
    private bool sessionTracking;
    private readonly List<MountOption> availableMounts = new();
    private DateTime nextMountRefresh = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> blockedRoutes = new(StringComparer.OrdinalIgnoreCase);
    private string? mapRouteName;
    private bool activeDivePathReset;
    private DateTime? activeDiveStartedAt;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        services = pluginInterface.Create<Services>() ?? throw new InvalidOperationException("Dalamud services are unavailable.");
        adapter = new VislandAdapter(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MigrateConfiguration();
        InitializeMountOverride();
        services.Commands.AddHandler(Command, new CommandInfo(HandleCommand)
        {
            HelpMessage = "Open Stock Manager. Subcommands: start, stop, status, travel, emergency, help.",
        });
        services.Commands.AddHandler(ShortCommand, new CommandInfo(HandleCommand)
        {
            HelpMessage = "Alias for /stockmanager.",
        });
        services.Framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettingsUi;
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
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettingsUi;
        useActionHook?.Dispose();
    }

    private void OpenMainUi()
    {
        settingsRequested = false;
        windowOpen = true;
    }

    private void OpenSettingsUi()
    {
        settingsRequested = true;
        windowOpen = true;
    }

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
            if (snapshot.IsRunning)
            {
                status = $"Running experimental test: {activeRoute}";
                HandleActiveRouteWater(snapshot);
            }
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
        UpdateCompletedStopTargets(snapshot);
        if (TryGetStartValidationError(snapshot, out var validationError))
        {
            config.Enabled = false; adapter.Stop(); EndSession(); Save(); status = validationError; return;
        }
        if (exportTrip && HandleExportTrip(snapshot)) return;
        if (snapshot.IsRunning)
        {
            status = activeRoute == null ? "Visland is running a route" : $"Running: {activeRoute}";
            HandleActiveRouteWater(snapshot);
            return;
        }
        if (DateTime.UtcNow < nextStartAttempt) return;

        if (config.CompletionAction == CompletionAction.FarmAndExport)
        {
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
            var exportDue = ManagedItems(snapshot).Where(IsExportDue)
                .OrderByDescending(x => x.CurrentCount - ExportTrigger(x.Id)).FirstOrDefault();
            if (exportDue != null)
            {
                QueueRouteStart(adapter.CreateExportTripRoute(), exportDue.Name, PendingRoutePurpose.Export);
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
        }

        var choice = SelectNextRoute(snapshot);
        if (choice == null)
        {
            if (ManagedItems(snapshot).Any(x => x.IsAvailable && x.CurrentCount < config.Targets[x.Id]))
            {
                status = "Compatible routes are cooling down after navigation stalls; retrying shortly.";
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
            if (config.CompletionAction == CompletionAction.Stop)
            {
                config.Enabled = false; activeRoute = null; EndSession(); Save();
                status = "All available targets have been reached.";
                return;
            }
            var cowrieRoute = SelectCowrieRoute(snapshot);
            if (cowrieRoute == null) { status = "No compatible unlocked resource routes are enabled."; return; }
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
        if (player == null || route.Waypoints.Count == 0)
        {
            status = player == null ? "The local player is unavailable." : $"Route {route.Name} has no waypoints.";
            if (purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return false;
        }
        if (!adapter.IsNavmeshReady)
        {
            status = "vnavmesh is not installed or ready.";
            if (purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return false;
        }

        var nearest = route.Waypoints.Select((waypoint, index) => (Waypoint: waypoint, Index: index,
                Distance: Vector3.Distance(player.Position, waypoint.Position)))
            .OrderBy(x => x.Distance).First();
        var startIndex = nearest.Distance <= 35f ? nearest.Index : 0;
        pendingRouteStart = new PendingRouteStart(route, itemName, purpose, startIndex);
        navigationStartedAt = DateTime.UtcNow;
        navigationRequestedAt = DateTime.MinValue;
        nextMountAttempt = DateTime.MinValue;
        nextDiveAttempt = DateTime.MinValue;
        activeRoute = route.Name;
        var startDistance = Vector3.Distance(player.Position, route.Waypoints[startIndex].Position);
        pendingRouteStart.LastProgressDistance = startDistance;
        pendingRouteStart.LastProgressAt = DateTime.UtcNow;
        status = nearest.Distance <= 35f
            ? $"Starting {route.Name} from nearby waypoint #{startIndex + 1}..."
            : $"Preparing a direct vnavmesh route to {route.Name}...";
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

        var player = services.Objects.LocalPlayer;
        if (player == null)
        {
            pendingRouteStart = null;
            activeRoute = null;
            status = "The local player is unavailable.";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        if (data.IsRunning)
        {
            status = "Waiting for the current Visland route to finish before navigating...";
            return;
        }

        var waypoint = pending.Route.Waypoints[pending.StartIndex];
        var distance = Vector3.Distance(player.Position, waypoint.Position);
        var swimming = services.Condition[ConditionFlag.Swimming];
        var diving = services.Condition[ConditionFlag.Diving];
        var inWater = swimming || diving;
        var underwaterDestination = RouteAccessibility.IsUnderwater(waypoint.Position);
        var arrivalRadius = Math.Max(5f, waypoint.Radius + 2f);
        if (distance <= arrivalRadius && (!underwaterDestination || !swimming || diving))
        {
            StartPreparedRoute(pending);
            return;
        }

        if (underwaterDestination && swimming && !diving)
        {
            pending.NavigationRequested = false;
            pending.LastProgressDistance = distance;
            pending.DiveStartedAt ??= DateTime.UtcNow;
            adapter.StopNavigation();
            status = $"Diving before continuing underwater to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            if (config.SkipStuckRoutes
                && DateTime.UtcNow - pending.DiveStartedAt.Value > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
            {
                HandleStuckRoute(pending, "the character could not dive");
                return;
            }
            TryDive();
            return;
        }
        pending.DiveStartedAt = null;

        if (diving && pending.NavigationRequested && !pending.NavigationWasThreeDimensional)
        {
            adapter.StopNavigation();
            pending.NavigationRequested = false;
            pending.LastProgressAt = DateTime.UtcNow;
            status = $"Rebuilding a three-dimensional underwater path to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return;
        }

        var requiresMount = distance > 12f || waypoint.Movement != RouteMovement.Normal;
        if (requiresMount && !services.Condition[ConditionFlag.Mounted] && (!inWater || diving))
        {
            pending.NavigationRequested = false;
            pending.LastProgressAt = DateTime.UtcNow;
            pending.LastProgressDistance = distance;
            adapter.StopNavigation();
            status = $"Mounting before travelling directly to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            if (!services.Condition[ConditionFlag.Casting]
                && !services.Condition[ConditionFlag.Mounting]
                && !services.Condition[ConditionFlag.MountOrOrnamentTransition]
                && DateTime.UtcNow >= nextMountAttempt)
            {
                TryUseConfiguredMount();
                nextMountAttempt = DateTime.UtcNow.AddSeconds(1);
            }
            return;
        }

        if (!pending.NavigationRequested)
        {
            var threeDimensional = diving
                                   || (waypoint.Movement == RouteMovement.MountFly
                                       && data.FlightUnlocked == true
                                       && services.Condition[ConditionFlag.Mounted]);
            if (!adapter.TryNavigateTo(waypoint.Position, threeDimensional, out var error))
            {
                pendingRouteStart = null;
                activeRoute = null;
                status = $"Could not navigate to {pending.Route.Name}: {error}";
                if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
            pending.NavigationRequested = true;
            pending.NavigationWasThreeDimensional = threeDimensional;
            navigationRequestedAt = DateTime.UtcNow;
            pending.LastProgressAt = DateTime.UtcNow;
            pending.LastProgressDistance = distance;
        }

        if (distance + 1.5f < pending.LastProgressDistance)
        {
            pending.LastProgressDistance = distance;
            pending.LastProgressAt = DateTime.UtcNow;
        }
        else if (config.SkipStuckRoutes
                 && DateTime.UtcNow - pending.LastProgressAt > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
        {
            HandleStuckRoute(pending);
            return;
        }

        status = $"Navigating directly with vnavmesh to {pending.Route.Name}... ({distance:F0} yalms)";
        if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
        if (DateTime.UtcNow - navigationRequestedAt < TimeSpan.FromSeconds(2) || adapter.IsNavigationBusy) return;

        if (inWater)
        {
            pending.NavigationRequested = false;
            pending.LastProgressAt = DateTime.UtcNow;
            status = $"Repathing through water to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return;
        }

        HandleStuckRoute(pending, "vnavmesh stopped before reaching the route");
    }

    private void StartPreparedRoute(PendingRouteStart pending)
    {
        adapter.StopNavigation();
        pendingRouteStart = null;
        if (!adapter.TryStartRoute(pending.Route, pending.StartIndex, snapshot?.FlightUnlocked == true, out var error))
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

    private unsafe void TryUseConfiguredMount()
    {
        var actions = ActionManager.Instance();
        if (actions == null) return;
        var playerState = PlayerState.Instance();
        if (config.MountId != 0 && playerState != null && playerState->IsMountUnlocked(config.MountId))
        {
            if (actions->UseAction(ActionType.Mount, config.MountId)) return;
        }
        actions->UseAction(ActionType.GeneralAction, 24);
    }

    private unsafe void InitializeMountOverride()
    {
        try
        {
            var address = (nint)(void*)ActionManager.MemberFunctionPointers.UseAction;
            if (address == 0) return;
            useActionHook = services.GameInterop.HookFromAddress<ActionManager.Delegates.UseAction>(address, UseActionDetour);
            useActionHook.Enable();
        }
        catch (Exception exception)
        {
            services.Log.Warning(exception, "Could not initialize the Visland mount-roulette override.");
        }
    }

    private unsafe bool UseActionDetour(ActionManager* self, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (actionType == ActionType.GeneralAction && actionId == 24 && ShouldOverrideMount())
        {
            var playerState = PlayerState.Instance();
            if (playerState != null && playerState->IsMountUnlocked(config.MountId))
                return useActionHook!.Original(self, ActionType.Mount, config.MountId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }
        return useActionHook!.Original(self, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    private bool ShouldOverrideMount() => config.MountId != 0
        && (config.Enabled || pendingRouteStart != null || experimentalTestRunning || activeRoute != null);

    private unsafe void TryDive()
    {
        if (!services.Condition[ConditionFlag.Swimming] || services.Condition[ConditionFlag.Diving]
            || DateTime.UtcNow < nextDiveAttempt) return;
        nextDiveAttempt = DateTime.UtcNow.AddSeconds(1.5);
        try
        {
            dive ??= Marshal.GetDelegateForFunctionPointer<DiveDelegate>(services.SigScanner.ScanText(
                "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 1D ?? ?? ?? ?? 48 8D 54 24"));
            dive(Control.Instance());
        }
        catch (Exception exception)
        {
            services.Log.Warning(exception, "Could not dive while approaching an underwater route.");
        }
    }

    private void HandleActiveRouteWater(VislandSnapshot data)
    {
        var route = data.Routes.FirstOrDefault(x => string.Equals(x.Name, activeRoute, StringComparison.OrdinalIgnoreCase))
                    ?? (string.Equals(experimentalRoute?.Name, activeRoute, StringComparison.OrdinalIgnoreCase) ? experimentalRoute : null);
        if (route == null || !route.Waypoints.Any(x => RouteAccessibility.IsUnderwater(x.Position)))
        {
            activeDivePathReset = false;
            activeDiveStartedAt = null;
            return;
        }
        if (services.Condition[ConditionFlag.Diving])
        {
            activeDiveStartedAt = null;
            if (activeDivePathReset) return;
            adapter.StopNavigation();
            activeDivePathReset = true;
            status = $"Rebuilding a three-dimensional underwater path for {route.Name}...";
            return;
        }
        activeDivePathReset = false;
        if (!services.Condition[ConditionFlag.Swimming])
        {
            activeDiveStartedAt = null;
            return;
        }
        activeDiveStartedAt ??= DateTime.UtcNow;
        if (config.SkipStuckRoutes
            && DateTime.UtcNow - activeDiveStartedAt.Value > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
        {
            adapter.Stop();
            blockedRoutes[route.Name] = DateTime.UtcNow.AddMinutes(5);
            activeRoute = null;
            activeDiveStartedAt = null;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
            status = $"Skipping {route.Name}: the character could not dive. Trying another route.";
            return;
        }
        status = $"Diving for underwater section of {route.Name}...";
        TryDive();
    }

    private void HandleStuckRoute(PendingRouteStart pending, string reason = "no approach progress was detected")
    {
        adapter.StopNavigation();
        pendingRouteStart = null;
        activeRoute = null;
        if (pending.Purpose == PendingRoutePurpose.Farm)
        {
            blockedRoutes[pending.Route.Name] = DateTime.UtcNow.AddMinutes(5);
            status = $"Skipping {pending.Route.Name}: {reason}. Trying another route.";
            nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        if (pending.Purpose == PendingRoutePurpose.Experimental)
        {
            experimentalStatus = $"Test stopped: {reason}.";
            status = experimentalStatus;
            return;
        }
        config.Enabled = false;
        EndSession();
        Save();
        status = $"Export trip stopped: {reason}.";
    }

    private void StopExperimentalTest()
    {
        adapter.Stop();
        pendingRouteStart = null;
        experimentalTestRunning = false;
        activeRoute = null;
        experimentalStatus = "Experimental test loop stopped.";
        status = experimentalStatus;
    }

    private unsafe void RefreshAvailableMounts()
    {
        if (DateTime.UtcNow < nextMountRefresh) return;
        nextMountRefresh = DateTime.UtcNow.AddSeconds(5);
        availableMounts.Clear();

        if (!services.ClientState.IsLoggedIn) return;
        var playerState = PlayerState.Instance();
        if (playerState == null) return;

        foreach (var mount in services.Data.GetExcelSheet<MountSheet>())
        {
            if (mount.RowId == 0 || !playerState->IsMountUnlocked(mount.RowId)) continue;
            var name = mount.Singular.ToString().Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            availableMounts.Add(new MountOption(mount.RowId, name));
        }

        availableMounts.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
    }

    private void BeginSession(VislandSnapshot data)
    {
        sessionLastCounts.Clear();
        sessionCollected.Clear();
        completedStopItems.Clear();
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
        var changed = false;
        if (config.Version < 6)
        {
            var oldRoutes = data.Routes.Where(x => config.LegacyExcludedRoutes?.Contains(x.Name) != true);
            if (config.LegacyMovementMode != 1) oldRoutes = oldRoutes.Where(x => !x.RequiresFlying);
            config.EnabledItems = oldRoutes.SelectMany(x => x.Items).Select(x => x.Id).Distinct()
                .Where(x => config.Targets.TryGetValue(x, out var target) && target > 0).ToHashSet();
            config.LegacyExcludedRoutes = null;
            config.LegacyMovementMode = null;
            changed = true;
        }
        if (config.Version < 7)
        {
            config.Version = 7;
            changed = true;
        }
        if (changed) Save();
    }

    private void UpdateCompletedStopTargets(VislandSnapshot data)
    {
        if (!config.Enabled || config.CompletionAction != CompletionAction.Stop)
        {
            completedStopItems.Clear();
            return;
        }
        foreach (var item in ConfiguredManagedItems(data))
        {
            if (item.IsAvailable && item.CurrentCount >= config.Targets[item.Id]) completedStopItems.Add(item.Id);
            else completedStopItems.Remove(item.Id);
        }
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectNextRoute(VislandSnapshot data)
    {
        var routes = SelectableRoutes(data);
        var items = OrderItems(data, ManagedItems(data).Where(x => x.IsAvailable)
            .Where(x => config.Targets[x.Id] > x.CurrentCount), x => config.Targets[x.Id], routes);
        foreach (var item in items)
        {
            var route = routes
                .Select(x => (Route: x, Item: x.Items.FirstOrDefault(y => y.Id == item.Id)))
                .Where(x => x.Item is { PerLoop: > 0 }).OrderByDescending(x => x.Item!.PerLoop)
                .ThenByDescending(x => RouteUtility(x.Route)).FirstOrDefault();
            if (route.Item != null) return (route.Route, item);
        }
        return null;
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectCowrieRoute(VislandSnapshot data)
    {
        var routes = SelectableRoutes(data);
        var items = ManagedItems(data).Where(x => x.IsAvailable)
            .Where(x => config.Targets.TryGetValue(x.Id, out var target) && target is > 0 and < 999)
            .Where(x => x.CurrentCount < ExportTrigger(x.Id));
        foreach (var item in OrderItems(data, items, x => ExportTrigger(x.Id), routes))
        {
            var route = routes.Select(x => (Route: x, Item: x.Items.FirstOrDefault(y => y.Id == item.Id)))
                .Where(x => x.Item is { PerLoop: > 0 }).OrderByDescending(x => x.Item!.PerLoop)
                .ThenByDescending(x => RouteUtility(x.Route)).FirstOrDefault();
            if (route.Item != null) return (route.Route, item);
        }
        return null;
    }

    private IEnumerable<ItemSnapshot> OrderItems(VislandSnapshot data, IEnumerable<ItemSnapshot> source,
        Func<ItemSnapshot, int> goal, IReadOnlyCollection<RouteSnapshot>? routes = null)
    {
        var candidates = source.ToList();
        routes ??= SelectableRoutes(data);
        return config.ResourcePriority switch
        {
            ResourcePriority.LowestStock => candidates.OrderBy(x => x.CurrentCount).ThenBy(x => (double)x.CurrentCount / Math.Max(1, goal(x))),
            ResourcePriority.HighestStock => candidates.OrderByDescending(x => x.CurrentCount).ThenBy(x => (double)x.CurrentCount / Math.Max(1, goal(x))),
            ResourcePriority.FastestRoute => candidates.OrderByDescending(item => routes
                    .Select(route => route.Items.FirstOrDefault(x => x.Id == item.Id)?.PerLoop ?? 0).DefaultIfEmpty().Max())
                .ThenBy(x => (double)x.CurrentCount / Math.Max(1, goal(x))),
            _ => candidates.OrderBy(x => (double)x.CurrentCount / Math.Max(1, goal(x))).ThenBy(x => x.CurrentCount),
        };
    }

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
        if (data.FlightUnlocked != null && !ConfiguredManagedItems(data).Any(x => x.IsAvailable))
        {
            error = "No enabled resource is currently unlocked and served by a compatible imported route.";
            return true;
        }
        error = string.Empty;
        return false;
    }

    private IEnumerable<RouteSnapshot> CompatibleRoutes(VislandSnapshot data) => data.Routes
        .Where(x => data.FlightUnlocked == true || !x.RequiresFlying);

    private List<RouteSnapshot> SelectableRoutes(VislandSnapshot data)
    {
        var now = DateTime.UtcNow;
        foreach (var route in blockedRoutes.Where(x => x.Value <= now).Select(x => x.Key).ToList())
            blockedRoutes.Remove(route);
        return CompatibleRoutes(data).Where(x => !blockedRoutes.ContainsKey(x.Name)).ToList();
    }

    private static IEnumerable<ItemSnapshot> UniqueItems(VislandSnapshot data) =>
        data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First());

    private IEnumerable<ItemSnapshot> ManagedItems(VislandSnapshot data) => CompatibleRoutes(data)
        .SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First())
        .Where(x => IsEffectivelyEnabled(x.Id) && config.Targets.TryGetValue(x.Id, out var target) && target > 0);

    private IEnumerable<ItemSnapshot> ConfiguredManagedItems(VislandSnapshot data) => CompatibleRoutes(data)
        .SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First())
        .Where(x => config.EnabledItems.Contains(x.Id) && config.Targets.TryGetValue(x.Id, out var target) && target > 0);

    private bool IsEffectivelyEnabled(int itemId) => config.EnabledItems.Contains(itemId)
        && !(config.Enabled && config.CompletionAction == CompletionAction.Stop && completedStopItems.Contains(itemId));

    private double RouteUtility(RouteSnapshot route) => route.Items.Sum(item =>
        item.IsAvailable && IsEffectivelyEnabled(item.Id) && config.Targets.TryGetValue(item.Id, out var target) && target > 0 && item.CurrentCount < target
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

        if (ImGui.BeginTabBar("StockManagerTabs"))
        {
            if (ImGui.BeginTabItem("Automation"))
            {
                DrawAutomation();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Map"))
            {
                DrawMap();
                ImGui.EndTabItem();
            }
            var settingsFlags = settingsRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Settings", settingsFlags))
            {
                settingsRequested = false;
                DrawSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawMap()
    {
        if (snapshot == null)
        {
            ImGui.TextWrapped("Travel to the Island and load Visland routes to display the route map.");
            return;
        }

        var routes = snapshot.Routes.ToList();
        if (experimentalRoute != null)
        {
            routes.RemoveAll(x => string.Equals(x.Name, experimentalRoute.Name, StringComparison.OrdinalIgnoreCase));
            routes.Insert(0, experimentalRoute);
        }
        if (routes.Count == 0)
        {
            ImGui.TextWrapped("No imported Island routes are available.");
            return;
        }

        if (mapRouteName == null || routes.All(x => !string.Equals(x.Name, mapRouteName, StringComparison.OrdinalIgnoreCase)))
            mapRouteName = routes.Any(x => string.Equals(x.Name, activeRoute, StringComparison.OrdinalIgnoreCase)) ? activeRoute : routes[0].Name;
        var route = routes.First(x => string.Equals(x.Name, mapRouteName, StringComparison.OrdinalIgnoreCase));

        ImGui.SetNextItemWidth(Math.Min(520, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Route", route.Name, ImGuiComboFlags.HeightLarge))
        {
            foreach (var candidate in routes.OrderBy(x => x.Name))
            {
                if (!ImGui.Selectable(candidate.Name, candidate == route)) continue;
                mapRouteName = candidate.Name;
                route = candidate;
            }
            ImGui.EndCombo();
        }
        if (activeRoute != null && routes.Any(x => string.Equals(x.Name, activeRoute, StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.SameLine();
            if (ImGui.Button("Show active route"))
            {
                mapRouteName = activeRoute;
                route = routes.First(x => string.Equals(x.Name, activeRoute, StringComparison.OrdinalIgnoreCase));
            }
        }
        var resources = string.Join(", ", route.Items.Select(x => x.Name));
        ImGui.TextWrapped($"{route.Waypoints.Count} waypoints  |  {(route.RequiresFlying ? "flight required" : "ground/underwater compatible")}"
                          + (string.IsNullOrWhiteSpace(resources) ? string.Empty : $"  |  {resources}"));
        ImGui.TextDisabled("Initial Island-coordinate preview. Hover a point for coordinates and movement details.");

        var available = ImGui.GetContentRegionAvail();
        var canvasSize = new Vector2(Math.Max(320, available.X), Math.Max(360, available.Y));
        ImGui.InvisibleButton("##RouteMapCanvas", canvasSize);
        var topLeft = ImGui.GetItemRectMin();
        var bottomRight = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var background = ImGui.ColorConvertFloat4ToU32(new Vector4(.035f, .055f, .06f, .96f));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(.32f, .38f, .4f, 1));
        var grid = ImGui.ColorConvertFloat4ToU32(new Vector4(.2f, .28f, .29f, .55f));
        draw.AddRectFilled(topLeft, bottomRight, background);
        draw.AddRect(topLeft, bottomRight, border);

        const float minX = -900f, maxX = 900f, minZ = -700f, maxZ = 700f;
        Vector2 ToCanvas(Vector3 world) => new(
            topLeft.X + (world.X - minX) / (maxX - minX) * canvasSize.X,
            topLeft.Y + (world.Z - minZ) / (maxZ - minZ) * canvasSize.Y);
        for (var x = -800; x <= 800; x += 200)
        {
            var a = ToCanvas(new Vector3(x, 0, minZ));
            var b = ToCanvas(new Vector3(x, 0, maxZ));
            draw.AddLine(a, b, grid);
        }
        for (var z = -600; z <= 600; z += 200)
        {
            var a = ToCanvas(new Vector3(minX, 0, z));
            var b = ToCanvas(new Vector3(maxX, 0, z));
            draw.AddLine(a, b, grid);
        }

        var normalColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.4f, .85f, .5f, 1));
        var mountColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .65f, .2f, 1));
        var flyingColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.45f, .65f, 1f, 1));
        var underwaterColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.2f, .85f, 1f, 1));
        uint MovementColor(RouteWaypointSnapshot point) => RouteAccessibility.IsUnderwater(point.Position)
            ? underwaterColor
            : point.Movement == RouteMovement.MountFly ? flyingColor
            : point.Movement == RouteMovement.MountNoFly ? mountColor : normalColor;

        for (var index = 0; index < route.Waypoints.Count; index++)
        {
            var from = ToCanvas(route.Waypoints[index].Position);
            var next = route.Waypoints[(index + 1) % route.Waypoints.Count];
            draw.AddLine(from, ToCanvas(next.Position), MovementColor(next), 2f);
        }

        var mouse = ImGui.GetMousePos();
        RouteWaypointSnapshot? hovered = null;
        var hoveredIndex = -1;
        for (var index = 0; index < route.Waypoints.Count; index++)
        {
            var point = route.Waypoints[index];
            var screen = ToCanvas(point.Position);
            var radius = point.ObjectId != 0 ? 5f : 3.5f;
            draw.AddCircleFilled(screen, radius, MovementColor(point));
            if (Vector2.Distance(mouse, screen) <= radius + 5)
            {
                hovered = point;
                hoveredIndex = index;
            }
        }

        var player = services.Objects.LocalPlayer;
        if (player != null)
        {
            var playerColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .95f, .2f, 1));
            draw.AddCircleFilled(ToCanvas(player.Position), 6f, playerColor);
        }
        draw.AddText(topLeft + new Vector2(8, 7), border, "N");

        if (hovered != null && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Waypoint #{hoveredIndex + 1}");
            ImGui.TextUnformatted($"X {hovered.Position.X:F1}  Y {hovered.Position.Y:F1}  Z {hovered.Position.Z:F1}");
            ImGui.TextUnformatted(RouteAccessibility.IsUnderwater(hovered.Position) ? "Underwater" : hovered.Movement.ToString());
            if (!string.IsNullOrWhiteSpace(hovered.ObjectName)) ImGui.TextUnformatted(hovered.ObjectName);
            ImGui.EndTooltip();
        }
    }

    private void DrawAutomation()
    {
        ImGui.TextColored(config.Enabled ? new Vector4(.35f, .9f, .45f, 1) : new Vector4(.7f, .7f, .7f, 1), config.Enabled ? "ACTIVE" : "STOPPED");
        ImGui.SameLine(); ImGui.TextWrapped(status);
        if (sessionStartedAt != null)
        {
            var duration = (sessionEndedAt ?? DateTime.UtcNow) - sessionStartedAt.Value;
            ImGui.TextDisabled($"This run: {duration:h\\:mm\\:ss}  |  {sessionCollected.Values.Sum()} gathered");
        }
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
    }

    private unsafe void DrawSettings()
    {
        RefreshAvailableMounts();
        ImGui.TextUnformatted("Navigation");
        ImGui.Separator();
        ImGui.TextWrapped("Stock Manager travels directly from the character's current position. It does not return to the Island base before starting a route.");
        ImGui.TextDisabled("If you are already near the selected route, the closest waypoint is used as its starting point.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Travel mount");
        var selected = availableMounts.FirstOrDefault(x => x.Id == config.MountId);
        var preview = config.MountId == 0 ? "Mount roulette" : selected?.Name ?? $"Unavailable mount #{config.MountId}";
        ImGui.SetNextItemWidth(Math.Min(420, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##TravelMount", preview, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable("Mount roulette", config.MountId == 0))
            {
                config.MountId = 0;
                Save();
            }
            foreach (var mount in availableMounts)
            {
                if (!ImGui.Selectable($"{mount.Name}##mount{mount.Id}", mount.Id == config.MountId)) continue;
                config.MountId = mount.Id;
                Save();
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled("Mount roulette is the first option. A specific choice also replaces Visland's roulette while Stock Manager routes are active.");
        ImGui.TextDisabled($"{availableMounts.Count} unlocked mounts found on this character.");
        if (config.MountId != 0 && selected == null)
            ImGui.TextColored(new Vector4(1f, .45f, .3f, 1), "The selected mount is unavailable; mount roulette will be used as a fallback.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Farming priority");
        var priority = (int)config.ResourcePriority;
        ImGui.SetNextItemWidth(Math.Min(420, ImGui.GetContentRegionAvail().X));
        if (ImGui.Combo("##ResourcePriority", ref priority,
                "Largest relative deficit (recommended)\0Lowest current stock\0Highest current stock\0Fastest best route\0"))
        {
            config.ResourcePriority = (ResourcePriority)priority;
            Save();
        }
        ImGui.TextDisabled(config.ResourcePriority switch
        {
            ResourcePriority.LowestStock => "Farms the enabled resource with the smallest raw inventory count first.",
            ResourcePriority.HighestStock => "Farms the enabled unfinished resource with the largest raw inventory count first.",
            ResourcePriority.FastestRoute => "Prefers the resource whose best compatible route contains the most matching nodes.",
            _ => "Compares current stock to each target, so resources with different targets stay balanced.",
        });

        ImGui.Spacing();
        ImGui.TextUnformatted("Stuck recovery");
        var skipStuck = config.SkipStuckRoutes;
        if (ImGui.Checkbox("Temporarily skip a route when its approach is stuck", ref skipStuck))
        {
            config.SkipStuckRoutes = skipStuck;
            Save();
        }
        if (!config.SkipStuckRoutes) ImGui.BeginDisabled();
        var timeout = config.StuckTimeoutSeconds;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("No-progress timeout (seconds)", ref timeout))
        {
            config.StuckTimeoutSeconds = Math.Clamp(timeout, 8, 60);
            Save();
        }
        if (!config.SkipStuckRoutes) ImGui.EndDisabled();
        ImGui.TextDisabled("A skipped farming route cools down for 5 minutes while another route or resource is selected.");
    }

    private unsafe void DrawTargets(VislandSnapshot data)
    {
        var targetLabel = config.CompletionAction == CompletionAction.Stop ? "Target stock" : "Sell above";
        var compatibleRoutes = CompatibleRoutes(data).ToList();
        var allItemIds = UniqueItems(data).Select(x => x.Id).ToList();
        var selectableIds = UniqueItems(data).Where(x => x.IsAvailable && compatibleRoutes.Any(route => route.Items.Any(y => y.Id == x.Id)))
            .Select(x => x.Id).ToList();
        var allEnabled = selectableIds.Count > 0 && selectableIds.All(IsEffectivelyEnabled);
        if (ImGui.Checkbox("Enable all available resources", ref allEnabled))
        {
            foreach (var id in allEnabled ? selectableIds : allItemIds)
            {
                if (allEnabled)
                {
                    config.EnabledItems.Add(id);
                    completedStopItems.Remove(id);
                }
                else
                {
                    config.EnabledItems.Remove(id);
                    completedStopItems.Remove(id);
                }
            }
            Save();
        }
        var maximumTarget = config.CompletionAction == CompletionAction.FarmAndExport ? Math.Max(1, 999 - config.ExportBatch) : 999;
        var bulk = config.BulkTarget; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt($"{targetLabel} for all", ref bulk)) { config.BulkTarget = Math.Clamp(bulk, 1, maximumTarget); Save(); }
        ImGui.SameLine(); if (ImGui.Button("Apply##farm"))
        { foreach (var id in config.Targets.Keys.ToList()) config.Targets[id] = Math.Min(config.BulkTarget, maximumTarget); Save(); }
        if (config.CompletionAction == CompletionAction.FarmAndExport)
        {
            ImGui.SameLine(); var batch = config.ExportBatch; ImGui.SetNextItemWidth(75);
            var highestTarget = config.EnabledItems.Select(id => config.Targets.GetValueOrDefault(id, 1)).DefaultIfEmpty(1).Max();
            if (ImGui.InputInt("Export batch", ref batch)) { config.ExportBatch = Math.Clamp(batch, 1, Math.Max(1, 999 - highestTarget)); Save(); }
            ImGui.TextDisabled("Visit at Sell above + batch; export back down to Sell above.");
            if (TryGetExportValidationError(data, out var error)) ImGui.TextColored(new Vector4(1f, .3f, .3f, 1), error);
        }

        var tableFlags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                         | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate;
        if (!ImGui.BeginTable("Targets", 4, tableFlags, new Vector2(0, -1))) return;
        ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.DefaultSort); ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(targetLabel, ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80); ImGui.TableHeadersRow();

        var items = UniqueItems(data).ToList();
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (!sortSpecs.IsNull && sortSpecs.SpecsCount > 0)
        {
            var spec = sortSpecs.Specs[0];
            Func<ItemSnapshot, object> key = spec.ColumnIndex switch
            {
                1 => item => item.CurrentCount,
                2 => item => config.Targets.GetValueOrDefault(item.Id, 0),
                3 => item => TargetStatusSortValue(item, compatibleRoutes),
                _ => item => item.Name,
            };
            items = (spec.SortDirection == ImGuiSortDirection.Descending
                    ? items.OrderByDescending(key).ThenBy(x => x.Name)
                    : items.OrderBy(key).ThenBy(x => x.Name)).ToList();
            sortSpecs.SpecsDirty = false;
        }
        else items = items.OrderBy(x => x.Name).ToList();

        foreach (var item in items)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            var completedForRun = config.Enabled && config.CompletionAction == CompletionAction.Stop && completedStopItems.Contains(item.Id);
            var enabled = IsEffectivelyEnabled(item.Id);
            var hasCompatibleRoute = compatibleRoutes.Any(route => route.Items.Any(x => x.Id == item.Id));
            var canFarm = item.IsAvailable && hasCompatibleRoute;
            if (!canFarm) ImGui.BeginDisabled();
            if (ImGui.Checkbox($"##enabled{item.Id}", ref enabled))
            {
                if (enabled)
                {
                    config.EnabledItems.Add(item.Id);
                    completedStopItems.Remove(item.Id);
                }
                else
                {
                    config.EnabledItems.Remove(item.Id);
                    completedStopItems.Remove(item.Id);
                }
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
            { config.Targets[item.Id] = Math.Clamp(target, 1, maximumTarget); Save(); }
            if (!item.IsAvailable) ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(!canFarm ? "ignored" : completedForRun ? "done" : !enabled ? "off" : item.CurrentCount >= target ? "done" : $"{item.CurrentCount * 100 / target}%");
        }
        ImGui.EndTable();
    }

    private double TargetStatusSortValue(ItemSnapshot item, IReadOnlyCollection<RouteSnapshot> compatibleRoutes)
    {
        if (!item.IsAvailable || !compatibleRoutes.Any(route => route.Items.Any(x => x.Id == item.Id))) return -2;
        if (!IsEffectivelyEnabled(item.Id)) return completedStopItems.Contains(item.Id) ? 2 : -1;
        var target = Math.Max(1, config.Targets.GetValueOrDefault(item.Id, 1));
        return (double)item.CurrentCount / target;
    }

    private void DrawBehavior()
    {
        var completion = (int)config.CompletionAction; ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("When targets are complete", ref completion, "Stop\0Farm and export for cowries\0"))
        {
            config.CompletionAction = (CompletionAction)completion;
            if (config.CompletionAction == CompletionAction.FarmAndExport) NormalizeExportLimits();
            Save();
        }
        var autoTravel = config.AutoTravelToIsland;
        if (!adapter.IsLifestreamAvailable) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Travel to Island with Lifestream when starting", ref autoTravel)) { config.AutoTravelToIsland = autoTravel; Save(); }
        if (!adapter.IsLifestreamAvailable) ImGui.EndDisabled();
        if (!adapter.IsLifestreamAvailable) ImGui.TextDisabled("Optional: install Lifestream to enable Island travel.");
        if (config.CompletionAction != CompletionAction.FarmAndExport) return;
    }

    private void NormalizeExportLimits()
    {
        config.ExportBatch = Math.Clamp(config.ExportBatch, 1, 998);
        var maximumTarget = 999 - config.ExportBatch;
        config.BulkTarget = Math.Clamp(config.BulkTarget, 1, maximumTarget);
        foreach (var id in config.Targets.Keys.ToList())
            config.Targets[id] = Math.Clamp(config.Targets[id], 1, maximumTarget);
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
        foreach (var item in UniqueItems(data).Where(x => IsEffectivelyEnabled(x.Id)).OrderBy(x => x.Name))
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
        ImGui.TextWrapped("Builds a compact temporary route from gathering nodes already present in imported Visland routes. Nearby target nodes are added before distant detours.");
        ImGui.TextColored(new Vector4(1f, .75f, .25f, 1), "Experimental: inspect and test the result before relying on it.");
        var limit = experimentalNodeLimit; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt("Maximum nodes", ref limit)) experimentalNodeLimit = Math.Clamp(limit, 11, 30);
        ImGui.TextDisabled("At least 11 unique nodes are used for respawns; short hops between nearby nodes stay on foot.");

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
        var testActive = experimentalTestRunning || pendingRouteStart?.Purpose == PendingRoutePurpose.Experimental;
        ImGui.SameLine();
        if (!testActive) ImGui.BeginDisabled();
        if (ImGui.Button("Stop test loop")) StopExperimentalTest();
        if (!testActive) ImGui.EndDisabled();
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
        var allNodes = compatible.SelectMany(x => x.Nodes)
            .Where(x => data.FlightUnlocked == true || !RouteAccessibility.IsFlightOnlyAltitude(x.Position))
            .Where(x => x.ItemIds.Any(availableIds.Contains))
            .GroupBy(NodeKey).Select(x => x.First()).ToList();
        var activeIds = activeItems.Select(x => x.Id).ToHashSet();
        var targetNodes = allNodes.Where(x => x.ItemIds.Any(activeIds.Contains)).ToList();
        if (targetNodes.Count == 0)
        {
            experimentalStatus = "No usable gathering nodes were found for the enabled resources.";
            return;
        }

        var selected = SelectClusteredNodes(targetNodes, activeItems, experimentalNodeLimit);
        if (selected.Count == 0)
        {
            experimentalStatus = $"The selected resources cannot all fit within {experimentalNodeLimit} nodes. Increase Maximum nodes or enable fewer resources.";
            return;
        }
        while (selected.Count < 11)
        {
            var support = allNodes.Where(x => !selected.Contains(x))
                .OrderBy(x => InsertionCost(selected, x)).FirstOrDefault();
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

    private static List<RouteNodeSnapshot> SelectClusteredNodes(List<RouteNodeSnapshot> candidates, List<ItemSnapshot> activeItems, int limit)
    {
        if (candidates.Count <= limit) return candidates.ToList();
        List<RouteNodeSnapshot>? best = null;
        var bestLength = float.MaxValue;
        var activeIds = activeItems.Select(x => x.Id).ToHashSet();
        foreach (var seed in candidates)
        {
            var selected = new List<RouteNodeSnapshot> { seed };
            var covered = seed.ItemIds.Where(activeIds.Contains).ToHashSet();
            while (covered.Count < activeIds.Count && selected.Count < limit)
            {
                var next = candidates.Where(x => !selected.Contains(x) && x.ItemIds.Any(id => activeIds.Contains(id) && !covered.Contains(id)))
                    .OrderByDescending(x => x.ItemIds.Count(id => activeIds.Contains(id) && !covered.Contains(id)))
                    .ThenBy(x => InsertionCost(selected, x)).FirstOrDefault();
                if (next == null) break;
                selected.Add(next);
                covered.UnionWith(next.ItemIds.Where(activeIds.Contains));
            }
            if (covered.Count < activeIds.Count) continue;
            while (selected.Count < limit)
            {
                var next = candidates.Where(x => !selected.Contains(x)).OrderBy(x => InsertionCost(selected, x)).FirstOrDefault();
                if (next == null) break;
                selected.Add(next);
            }
            var ordered = OptimizeCycle(selected);
            var length = CycleLength(ordered);
            if (length >= bestLength) continue;
            best = ordered;
            bestLength = length;
        }
        return best ?? [];
    }

    private static float InsertionCost(IReadOnlyList<RouteNodeSnapshot> route, RouteNodeSnapshot candidate)
    {
        if (route.Count == 0) return 0;
        if (route.Count == 1) return Vector3.Distance(route[0].Position, candidate.Position) * 2;
        var best = float.MaxValue;
        for (var index = 0; index < route.Count; index++)
        {
            var a = route[index].Position;
            var b = route[(index + 1) % route.Count].Position;
            best = Math.Min(best, Vector3.Distance(a, candidate.Position) + Vector3.Distance(candidate.Position, b) - Vector3.Distance(a, b));
        }
        return best;
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

    private enum PendingRoutePurpose
    {
        Farm,
        Export,
        Experimental,
    }

    private sealed class PendingRouteStart(RouteSnapshot route, string? itemName, PendingRoutePurpose purpose, int startIndex)
    {
        public RouteSnapshot Route { get; } = route;
        public string? ItemName { get; } = itemName;
        public PendingRoutePurpose Purpose { get; } = purpose;
        public int StartIndex { get; } = startIndex;
        public bool NavigationRequested { get; set; }
        public bool NavigationWasThreeDimensional { get; set; }
        public float LastProgressDistance { get; set; } = float.MaxValue;
        public DateTime LastProgressAt { get; set; } = DateTime.UtcNow;
        public DateTime? DiveStartedAt { get; set; }
    }

    private sealed record MountOption(uint Id, string Name);

    private unsafe delegate byte DiveDelegate(void* control);

    private sealed class Services
    {
        [PluginService] internal ICommandManager Commands { get; private init; } = null!;
        [PluginService] internal IFramework Framework { get; private init; } = null!;
        [PluginService] internal IClientState ClientState { get; private init; } = null!;
        [PluginService] internal ICondition Condition { get; private init; } = null!;
        [PluginService] internal IDataManager Data { get; private init; } = null!;
        [PluginService] internal IObjectTable Objects { get; private init; } = null!;
        [PluginService] internal IGameGui GameGui { get; private init; } = null!;
        [PluginService] internal IChatGui ChatGui { get; private init; } = null!;
        [PluginService] internal ISigScanner SigScanner { get; private init; } = null!;
        [PluginService] internal IGameInteropProvider GameInterop { get; private init; } = null!;
        [PluginService] internal IPluginLog Log { get; private init; } = null!;
    }
}
