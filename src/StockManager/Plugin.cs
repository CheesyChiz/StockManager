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
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using MountSheet = Lumina.Excel.Sheets.Mount;
using System.Numerics;
using System.Runtime.InteropServices;

namespace StockManager;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string Command = "/stockmanager";
    private const string ShortCommand = "/sm";
    private const string IslandMapTexturePath = "ui/map/h1m2/02/h1m202_m.tex";
    private const float IslandMapSizeFactor = 1f;
    private const float IslandMapOffsetX = -175f;
    private const float IslandMapOffsetZ = 138f;
    private const uint ExporterObjectId = 1043464;
    private const int InitialCaveUnlockRank = 12;
    private static readonly TimeSpan ActiveRebalanceMinimumRuntime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ActiveRebalanceStableWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StrictPriorityReviewInterval = TimeSpan.FromMinutes(10);
    private static readonly Vector3 IslandBaseExterior = new(-268f, 40f, 226f);
    private static readonly HashSet<int> InitialCaveResourceIds = [39887, 39888, 39889, 39892, 39893];
    private static readonly Vector3[] UpperCaveApproach =
    [
        new(431.19f, 119.40f, -155.07f),
        new(441.86f, 116.59f, -168.72f),
        new(446.32f, 113.38f, -180.23f),
        new(447.38f, 109.97f, -192.81f),
        new(425.58f, 98.13f, -245.72f),
    ];
    private static readonly Vector3[] LowerCaveApproach =
    [
        new(427.57f, 123.13f, -139.80f),
        new(447.67f, 114.02f, -181.11f),
        new(449.16f, 112.46f, -187.92f),
        new(446.81f, 109.99f, -192.11f),
        new(433.62f, 104.67f, -211.35f),
        new(386.96f, 99.62f, -233.22f),
        new(346.98f, 95.59f, -243.23f),
        new(329.04f, 94.80f, -250.19f),
        new(317.08f, 91.65f, -256.45f),
        new(295.24f, 75.50f, -253.14f),
    ];
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
    private bool leavingExporter;
    private bool exporterExitNavigationRequested;
    private bool experimentalTestRunning;
    private string status = "Waiting for Visland...";
    private string? activeRoute;
    private string? lastFarmRoute;
    private int? activeTargetItemId;
    private string? activeTargetItemName;
    private int? activeTargetGoal;
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
    private MapDisplayMode mapDisplayMode;
    private int? mapResourceId;
    private float mapZoom = 1f;
    private Vector2 mapPan;
    private DateTime? activeDiveStartedAt;
    private DateTime nextExporterInteractionAttempt = DateTime.MinValue;
    private DateTime nextIsleReturnAttempt = DateTime.MinValue;
    private DateTime exporterExitStartedAt = DateTime.MinValue;
    private DateTime exporterExitLastProgressAt = DateTime.MinValue;
    private float exporterExitLastDistance = float.MaxValue;
    private Vector3? activeLastPosition;
    private int activeLastInventoryTotal;
    private DateTime activeLastProgressAt = DateTime.MinValue;
    private DateTime activeRouteStartedAt = DateTime.MinValue;
    private DateTime nextStrictPriorityReviewAt = DateTime.MinValue;
    private string? activeRebalanceCandidate;
    private DateTime activeRebalanceCandidateSince = DateTime.MinValue;

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
                if (HandleActiveRouteStuck(snapshot)) return;
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
        if (leavingExporter)
        {
            HandleExporterExit(snapshot);
            return;
        }
        if (snapshot.IsRunning)
        {
            if (config.CompletionAction == CompletionAction.FarmAndExport)
            {
                var exportDue = ManagedItems(snapshot).Where(IsExportDue)
                    .OrderByDescending(x => x.CurrentCount - ExportTrigger(x.Id)).FirstOrDefault();
                if (exportDue != null)
                {
                    var interruptedRoute = activeRoute;
                    adapter.Stop();
                    activeRoute = null;
                    ClearActiveTargetTracking();
                    QueueRouteStart(adapter.CreateExportTripRoute(), exportDue, PendingRoutePurpose.Export);
                    nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                    status = $"{exportDue.Name} reached {ExportTrigger(exportDue.Id)} during {interruptedRoute}; stopping the loop to export all configured surplus.";
                    return;
                }
            }
            if (HandleActiveFarmTarget(snapshot)) return;
            if (HandleActiveRouteRebalance(snapshot)) return;
            var trackedItem = activeTargetItemId == null
                ? null
                : UniqueItems(snapshot).FirstOrDefault(x => x.Id == activeTargetItemId.Value);
            status = activeRoute == null
                ? "Visland is running a route"
                : activeTargetItemName == null
                    ? $"Running: {activeRoute}"
                    : $"Running: {activeRoute} for {activeTargetItemName}"
                      + (trackedItem != null && activeTargetGoal.HasValue ? $" ({trackedItem.CurrentCount}/{activeTargetGoal.Value})" : string.Empty);
            HandleActiveRouteWater(snapshot);
            HandleActiveRouteStuck(snapshot);
            return;
        }
        ClearActiveTargetTracking();
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
                QueueRouteStart(adapter.CreateExportTripRoute(), exportDue, PendingRoutePurpose.Export);
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
        }

        var choice = SelectNextRoute(snapshot);
        var targetGoal = choice == null ? 0 : config.Targets[choice.Value.Item.Id];
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
            targetGoal = ExportTrigger(choice.Value.Item.Id);
        }

        QueueRouteStart(choice.Value.Route, choice.Value.Item, PendingRoutePurpose.Farm, targetGoal);
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
        leavingExporter = false;
        exporterExitNavigationRequested = false;
        travelRequested = false;
        activeRoute = null;
        ClearActiveTargetTracking();
        pendingRouteStart = null;
        experimentalTestRunning = false;
        nextStartAttempt = DateTime.MinValue;
        nextStrictPriorityReviewAt = DateTime.MinValue;
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
        leavingExporter = false;
        exporterExitNavigationRequested = false;
        exportSubmitted = false;
        travelRequested = false;
        pendingRouteStart = null;
        experimentalTestRunning = false;
        activeRoute = null;
        ClearActiveTargetTracking();
        adapter.Stop();
        if (emergency || wasTravelRequested) adapter.AbortLifestream();
        EndSession();
        Save();
        status = message;
    }

    private bool QueueRouteStart(RouteSnapshot route, ItemSnapshot? item, PendingRoutePurpose purpose, int? itemGoal = null)
    {
        if (purpose == PendingRoutePurpose.Export && !adapter.TryDisableBuiltInAutoExport(out var autoExportError))
        {
            status = $"Could not disable Visland Auto Export before opening the exporter: {autoExportError}";
            return false;
        }

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
        if (IsInitialCaveRoute(route) && snapshot?.IslandRank is { } rank && rank < InitialCaveUnlockRank)
        {
            status = $"{route.Name} is inside the cave unlocked at Island rank {InitialCaveUnlockRank}.";
            if (purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return false;
        }

        var nearest = route.Waypoints.Select((waypoint, index) => (Waypoint: waypoint, Index: index,
                Distance: Vector3.Distance(player.Position, waypoint.Position)))
            .OrderBy(x => x.Distance).First();
        var startIndex = purpose == PendingRoutePurpose.Export ? 0 : nearest.Distance <= 35f ? nearest.Index : 0;
        pendingRouteStart = new PendingRouteStart(route, item?.Id, item?.Name, itemGoal, purpose, startIndex);
        var playerIsInsideCave = IsInsideInitialCave(player.Position);
        pendingRouteStart.CaveApproach = IsInitialCaveRoute(route) && !playerIsInsideCave;
        pendingRouteStart.CaveExit = !IsInitialCaveRoute(route) && playerIsInsideCave;
        if (pendingRouteStart.CaveExit)
            pendingRouteStart.CaveTransitionPath = GetCaveExitApproach(player.Position);
        navigationStartedAt = DateTime.UtcNow;
        navigationRequestedAt = DateTime.MinValue;
        nextMountAttempt = DateTime.MinValue;
        nextDiveAttempt = DateTime.MinValue;
        activeRoute = route.Name;
        var startTarget = pendingRouteStart.CaveApproach
            ? GetCaveApproach(route)[0]
            : pendingRouteStart.CaveExit
                ? pendingRouteStart.CaveTransitionPath![^1]
                : route.Waypoints[startIndex].Position;
        var startDistance = Vector3.Distance(player.Position, startTarget);
        pendingRouteStart.UseIsleReturn = purpose == PendingRoutePurpose.Export && startDistance > 35f;
        pendingRouteStart.LastProgressDistance = startDistance;
        pendingRouteStart.LastProgressAt = DateTime.UtcNow;
        status = pendingRouteStart.CaveApproach
            ? $"Preparing a guided vnavmesh approach through the cave entrance for {route.Name}..."
            : pendingRouteStart.CaveExit
                ? $"Preparing a guided flight out of the cave before starting {route.Name}..."
                : purpose != PendingRoutePurpose.Export && nearest.Distance <= 35f
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

        if (pending.UseIsleReturn)
        {
            var baseDistance = Vector3.Distance(player.Position, IslandBaseExterior);
            if (baseDistance <= 12f)
            {
                pending.UseIsleReturn = false;
                pending.CaveExit = false;
                pending.CaveNavigationInitialized = false;
                pending.NavigationRequested = false;
                pending.LastProgressDistance = float.MaxValue;
                pending.LastProgressAt = DateTime.UtcNow;
            }
            else if (pending.IsleReturnStartedAt == null
                     || DateTime.UtcNow - pending.IsleReturnStartedAt.Value <= TimeSpan.FromSeconds(15))
            {
                pending.IsleReturnStartedAt ??= DateTime.UtcNow;
                if (!services.Condition[ConditionFlag.Casting]
                    && !services.Condition[ConditionFlag.OccupiedInEvent]
                    && DateTime.UtcNow >= nextIsleReturnAttempt)
                {
                    TryUseIsleReturn();
                    nextIsleReturnAttempt = DateTime.UtcNow.AddSeconds(2);
                }
                status = pending.Purpose == PendingRoutePurpose.Export
                    ? "Using Isle Return before visiting the Island exporter..."
                    : $"The guided cave exit stalled; using Isle Return before starting {pending.Route.Name}...";
                return;
            }
            else
            {
                pending.UseIsleReturn = false;
                pending.LastProgressAt = DateTime.UtcNow;
                pending.LastProgressDistance = baseDistance;
                pending.CaveNavigationInitialized = false;
                pending.NavigationRequested = false;
                status = pending.CaveExit
                    ? "Isle Return was unavailable; retrying the guided cave exit."
                    : "Isle Return was unavailable; falling back to vnavmesh.";
            }
        }

        var waypoint = pending.Route.Waypoints[pending.StartIndex];
        var distance = Vector3.Distance(player.Position, waypoint.Position);
        var horizontalDistance = Vector2.Distance(
            new Vector2(player.Position.X, player.Position.Z),
            new Vector2(waypoint.Position.X, waypoint.Position.Z));
        var swimming = services.Condition[ConditionFlag.Swimming];
        var diving = services.Condition[ConditionFlag.Diving];
        var inWater = swimming || diving;
        var underwaterDestination = RouteAccessibility.IsUnderwater(waypoint.Position);
        var progressDistance = underwaterDestination && swimming && !diving ? horizontalDistance : distance;
        var arrivalRadius = Math.Max(5f, waypoint.Radius + 2f);
        if (distance <= arrivalRadius && (!underwaterDestination || diving))
        {
            StartPreparedRoute(pending);
            return;
        }

        if (underwaterDestination && swimming && !diving)
        {
            pending.DiveStartedAt ??= DateTime.UtcNow;
            status = $"Swimming to a diveable point and diving for {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            TryDive();
        }
        else if (!diving) pending.DiveStartedAt = null;

        if (diving && pending.NavigationRequested && !pending.NavigationWasThreeDimensional)
        {
            adapter.StopNavigation();
            pending.NavigationRequested = false;
            pending.LastProgressAt = DateTime.UtcNow;
            pending.LastProgressDistance = distance;
            status = $"Rebuilding a three-dimensional underwater path to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return;
        }

        if (pending.ExitingCabin)
        {
            var exitDistance = Vector3.Distance(player.Position, IslandBaseExterior);
            if (exitDistance <= 6f)
            {
                adapter.StopNavigation();
                pending.ExitingCabin = false;
                pending.NavigationRequested = false;
                pending.LastProgressAt = DateTime.UtcNow;
                pending.LastProgressDistance = distance;
                status = $"Outside the cabin; preparing to mount for {pending.Route.Name}...";
                return;
            }
            if (!pending.NavigationRequested || !adapter.IsNavigationBusy)
            {
                if (!adapter.TryNavigateTo(IslandBaseExterior, false, out var exitError))
                {
                    HandleStuckRoute(pending, $"the cabin exit could not be reached: {exitError}");
                    return;
                }
                pending.NavigationRequested = true;
            }
            if (exitDistance + 1f < pending.LastProgressDistance)
            {
                pending.LastProgressDistance = exitDistance;
                pending.LastProgressAt = DateTime.UtcNow;
            }
            else if (config.SkipStuckRoutes
                     && DateTime.UtcNow - pending.LastProgressAt > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
            {
                TryUseIsleReturn();
                pending.LastProgressAt = DateTime.UtcNow;
            }
            status = $"Mounts are unavailable in the cabin; walking outside first... ({exitDistance:F0} yalms)";
            return;
        }

        var requiresMount = pending.CaveApproach || pending.CaveExit || diving || distance > 12f || waypoint.Movement != RouteMovement.Normal;
        if (requiresMount && !services.Condition[ConditionFlag.Mounted] && (!inWater || diving))
        {
            pending.NavigationRequested = false;
            pending.MountStartedAt ??= DateTime.UtcNow;
            adapter.StopNavigation();
            status = $"Mounting before travelling directly to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            if (!diving
                && pending.MountStartedAt.HasValue
                && DateTime.UtcNow - pending.MountStartedAt.Value > TimeSpan.FromSeconds(3)
                && Vector3.Distance(player.Position, IslandBaseExterior) < 35f)
            {
                pending.ExitingCabin = true;
                pending.MountStartedAt = null;
                pending.NavigationRequested = false;
                pending.LastProgressDistance = Vector3.Distance(player.Position, IslandBaseExterior);
                pending.LastProgressAt = DateTime.UtcNow;
                status = "Mount unavailable near the cabin; walking to the exterior first...";
                return;
            }
            if (config.SkipStuckRoutes
                && DateTime.UtcNow - pending.MountStartedAt.Value > TimeSpan.FromSeconds(Math.Max(20, Math.Clamp(config.StuckTimeoutSeconds, 8, 60))))
            {
                HandleStuckRoute(pending, diving ? "the character could not mount underwater" : "the character could not mount");
                return;
            }
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
        if (pending.MountStartedAt != null)
        {
            pending.LastProgressAt = DateTime.UtcNow;
            pending.LastProgressDistance = progressDistance;
        }
        pending.MountStartedAt = null;

        if (HandleInitialCaveExit(data, pending, player.Position)) return;
        if (HandleInitialCaveApproach(data, pending, player.Position)) return;

        if (!pending.NavigationRequested)
        {
            var threeDimensional = diving
                                   || (waypoint.Movement == RouteMovement.MountFly
                                       && data.FlightUnlocked == true
                                       && services.Condition[ConditionFlag.Mounted]);
            var navigationTarget = underwaterDestination && swimming && !diving
                ? new Vector3(waypoint.Position.X, player.Position.Y, waypoint.Position.Z)
                : waypoint.Position;
            if (!adapter.TryNavigateTo(navigationTarget, threeDimensional, out var error))
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
        }

        if (progressDistance + 1.5f < pending.LastProgressDistance)
        {
            pending.LastProgressDistance = progressDistance;
            pending.LastProgressAt = DateTime.UtcNow;
        }
        else if (config.SkipStuckRoutes
                 && DateTime.UtcNow - pending.LastProgressAt > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
        {
            HandleStuckRoute(pending);
            return;
        }

        status = underwaterDestination && swimming && !diving
            ? $"Swimming and looking for a dive point for {pending.Route.Name}... ({horizontalDistance:F0} yalms)"
            : $"Navigating directly with vnavmesh to {pending.Route.Name}... ({distance:F0} yalms)";
        if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
        if (DateTime.UtcNow - navigationRequestedAt < TimeSpan.FromSeconds(2) || adapter.IsNavigationBusy) return;

        if (inWater)
        {
            pending.NavigationRequested = false;
            status = $"Repathing through water to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return;
        }

        HandleStuckRoute(pending, "vnavmesh stopped before reaching the route");
    }

    private bool HandleInitialCaveApproach(VislandSnapshot data, PendingRouteStart pending, Vector3 playerPosition)
    {
        if (!pending.CaveApproach) return false;
        if (data.FlightUnlocked != true)
        {
            HandleStuckRoute(pending, "the cave approach requires Island flight");
            return true;
        }

        var path = GetCaveApproach(pending.Route);
        var target = pending.CaveApproachStage == 0 ? path[0] : path[^1];
        var distance = Vector3.Distance(playerPosition, target);
        if (!pending.CaveNavigationInitialized)
        {
            adapter.StopNavigation();
            pending.NavigationRequested = false;
            pending.CaveNavigationInitialized = true;
            pending.LastProgressDistance = distance;
            pending.LastProgressAt = DateTime.UtcNow;
        }

        var arrivalRadius = pending.CaveApproachStage == 0 ? 10f : 8f;
        if (distance <= arrivalRadius)
        {
            adapter.StopNavigation();
            pending.NavigationRequested = false;
            pending.LastProgressAt = DateTime.UtcNow;
            if (pending.CaveApproachStage == 0)
            {
                pending.CaveApproachStage = 1;
                pending.CaveNavigationInitialized = false;
                status = $"Reached the cave entrance; following the guided corridor to {pending.Route.Name}...";
                if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
                return true;
            }

            pending.CaveApproach = false;
            StartPreparedRoute(pending);
            return true;
        }

        if (!pending.NavigationRequested)
        {
            var started = pending.CaveApproachStage == 0
                ? adapter.TryNavigateTo(target, true, out var error)
                : adapter.TryFollowPath(path.Skip(1), true, out error);
            if (!started)
            {
                HandleStuckRoute(pending, $"the guided cave approach could not start: {error}");
                return true;
            }
            pending.NavigationRequested = true;
            pending.NavigationWasThreeDimensional = true;
            navigationRequestedAt = DateTime.UtcNow;
        }

        if (distance + 1.5f < pending.LastProgressDistance)
        {
            pending.LastProgressDistance = distance;
            pending.LastProgressAt = DateTime.UtcNow;
        }
        else if (config.SkipStuckRoutes
                 && DateTime.UtcNow - pending.LastProgressAt > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
        {
            HandleStuckRoute(pending,
                "the cave entrance made no progress; make sure the rank 12 cave expansion and Mammet-sized Spelunking Tools are complete");
            return true;
        }

        status = pending.CaveApproachStage == 0
            ? $"Flying to the cave entrance for {pending.Route.Name}... ({distance:F0} yalms)"
            : $"Following the guided cave corridor to {pending.Route.Name}... ({distance:F0} yalms)";
        if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
        if (DateTime.UtcNow - navigationRequestedAt >= TimeSpan.FromSeconds(2) && !adapter.IsNavigationBusy)
            pending.NavigationRequested = false;
        return true;
    }

    private bool HandleInitialCaveExit(VislandSnapshot data, PendingRouteStart pending, Vector3 playerPosition)
    {
        if (!pending.CaveExit) return false;
        if (data.FlightUnlocked != true)
        {
            HandleStuckRoute(pending, "the cave exit requires Island flight");
            return true;
        }

        var path = pending.CaveTransitionPath ?? GetCaveExitApproach(playerPosition);
        pending.CaveTransitionPath = path;
        var target = pending.CaveApproachStage == 0 ? path[^1] : path[0];
        var distance = Vector3.Distance(playerPosition, target);
        if (!pending.CaveNavigationInitialized)
        {
            adapter.StopNavigation();
            pending.NavigationRequested = false;
            pending.CaveNavigationInitialized = true;
            pending.LastProgressDistance = distance;
            pending.LastProgressAt = DateTime.UtcNow;
        }

        var arrivalRadius = pending.CaveApproachStage == 0 ? 8f : 10f;
        if (distance <= arrivalRadius)
        {
            adapter.StopNavigation();
            pending.NavigationRequested = false;
            pending.LastProgressAt = DateTime.UtcNow;
            if (pending.CaveApproachStage == 0)
            {
                pending.CaveApproachStage = 1;
                pending.CaveNavigationInitialized = false;
                status = $"Reached the inner cave corridor; following the guided exit before {pending.Route.Name}...";
                if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
                return true;
            }

            pending.CaveExit = false;
            pending.CaveNavigationInitialized = false;
            pending.LastProgressDistance = Vector3.Distance(playerPosition, pending.Route.Waypoints[pending.StartIndex].Position);
            pending.LastProgressAt = DateTime.UtcNow;
            status = $"Exited the cave; preparing the outdoor path to {pending.Route.Name}...";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            return true;
        }

        if (!pending.NavigationRequested)
        {
            var started = pending.CaveApproachStage == 0
                ? adapter.TryNavigateTo(target, true, out var error)
                : adapter.TryFollowPath(path.Reverse().Skip(1), true, out error);
            if (!started)
            {
                BeginCaveExitIsleReturnFallback(pending, $"the guided cave exit could not start: {error}");
                return true;
            }
            pending.NavigationRequested = true;
            pending.NavigationWasThreeDimensional = true;
            navigationRequestedAt = DateTime.UtcNow;
        }

        if (distance + 1.5f < pending.LastProgressDistance)
        {
            pending.LastProgressDistance = distance;
            pending.LastProgressAt = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - pending.LastProgressAt > TimeSpan.FromSeconds(
                     Math.Max(20, Math.Clamp(config.StuckTimeoutSeconds, 8, 60))))
        {
            BeginCaveExitIsleReturnFallback(pending, "the guided cave exit made no progress");
            return true;
        }

        status = pending.CaveApproachStage == 0
            ? $"Reaching the inner cave corridor before exiting for {pending.Route.Name}... ({distance:F0} yalms)"
            : $"Following the guided flight out of the cave for {pending.Route.Name}... ({distance:F0} yalms)";
        if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
        if (DateTime.UtcNow - navigationRequestedAt >= TimeSpan.FromSeconds(2) && !adapter.IsNavigationBusy)
            pending.NavigationRequested = false;
        return true;
    }

    private void BeginCaveExitIsleReturnFallback(PendingRouteStart pending, string reason)
    {
        adapter.StopNavigation();
        pending.NavigationRequested = false;
        pending.CaveNavigationInitialized = false;
        pending.UseIsleReturn = true;
        pending.IsleReturnStartedAt = null;
        pending.LastProgressAt = DateTime.UtcNow;
        status = $"Could not leave the cave normally ({reason}); falling back to Isle Return.";
        if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
    }

    private void StartPreparedRoute(PendingRouteStart pending)
    {
        adapter.StopNavigation();
        pendingRouteStart = null;
        if (!adapter.TryStartRoute(pending.Route, pending.StartIndex, snapshot?.FlightUnlocked == true, out var error))
        {
            activeRoute = null;
            ClearActiveTargetTracking();
            status = $"Visland rejected start: {error}";
            if (pending.Purpose == PendingRoutePurpose.Experimental) experimentalStatus = status;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        switch (pending.Purpose)
        {
            case PendingRoutePurpose.Export:
                ClearActiveTargetTracking();
                exportTrip = true;
                exportSubmitted = false;
                exportTripStarted = DateTime.UtcNow;
                nextExporterInteractionAttempt = DateTime.MinValue;
                closeExportAfter = DateTime.MaxValue;
                status = $"{pending.ItemName} reached its export threshold; going to export configured surplus.";
                break;
            case PendingRoutePurpose.Experimental:
                ClearActiveTargetTracking();
                experimentalTestRunning = true;
                activeLastPosition = services.Objects.LocalPlayer?.Position;
                activeLastInventoryTotal = snapshot == null ? 0 : UniqueItems(snapshot).Sum(x => x.CurrentCount);
                activeLastProgressAt = DateTime.UtcNow;
                status = "Experimental test loop started in Visland. Use Stop test loop or Emergency stop if needed.";
                experimentalStatus = status;
                break;
            default:
                activeTargetItemId = pending.ItemId;
                activeTargetItemName = pending.ItemName;
                activeTargetGoal = pending.ItemGoal;
                lastFarmRoute = pending.Route.Name;
                activeRouteStartedAt = DateTime.UtcNow;
                nextStrictPriorityReviewAt = DateTime.UtcNow + StrictPriorityReviewInterval;
                activeRebalanceCandidate = null;
                activeRebalanceCandidateSince = DateTime.MinValue;
                activeLastPosition = services.Objects.LocalPlayer?.Position;
                activeLastInventoryTotal = snapshot == null ? 0 : UniqueItems(snapshot).Sum(x => x.CurrentCount);
                activeLastProgressAt = DateTime.UtcNow;
                status = $"Starting {pending.Route.Name} for {pending.ItemName}"
                         + (pending.ItemGoal.HasValue ? $" ({pending.ItemGoal.Value} target)." : ".");
                break;
        }
    }

    private bool HandleActiveFarmTarget(VislandSnapshot data)
    {
        if (activeTargetItemId == null || activeTargetGoal == null) return false;
        var item = UniqueItems(data).FirstOrDefault(x => x.Id == activeTargetItemId.Value);
        if (item == null) return false;
        if (item.CurrentCount < activeTargetGoal.Value)
        {
            status = $"Running: {activeRoute} for {item.Name} ({item.CurrentCount}/{activeTargetGoal.Value})";
            return false;
        }

        var routeName = activeRoute;
        var reachedGoal = activeTargetGoal.Value;
        adapter.Stop();
        activeRoute = null;
        ClearActiveTargetTracking();
        nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
        status = $"{item.Name} reached {reachedGoal}; stopped {routeName} and recalculating.";
        return true;
    }

    private bool HandleActiveRouteRebalance(VislandSnapshot data)
    {
        if (activeRoute == null
            || exportTrip
            || activeDiveStartedAt != null
            || activeRouteStartedAt == DateTime.MinValue)
        {
            ResetActiveRebalanceCandidate();
            return false;
        }

        if (config.ResourcePriority != ResourcePriority.FastestRoute)
        {
            if (DateTime.UtcNow < nextStrictPriorityReviewAt) return false;
            nextStrictPriorityReviewAt = DateTime.UtcNow + StrictPriorityReviewInterval;
            var strictRecommendation = SelectPhaseRoute(data);
            if (strictRecommendation == null)
            {
                var previous = activeRoute;
                adapter.Stop();
                activeRoute = null;
                ClearActiveTargetTracking();
                nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
                status = $"Stopped {previous}: the 10-minute strict-priority review found no unfinished compatible target.";
                return true;
            }

            if (string.Equals(strictRecommendation.Value.Route.Name, activeRoute, StringComparison.OrdinalIgnoreCase))
            {
                if (activeTargetItemId != strictRecommendation.Value.Item.Id)
                {
                    activeTargetItemId = strictRecommendation.Value.Item.Id;
                    activeTargetItemName = strictRecommendation.Value.Item.Name;
                    activeTargetGoal = SelectionGoal(data, strictRecommendation.Value.Item);
                    status = $"10-minute priority review kept {activeRoute} and now tracks {activeTargetItemName}.";
                }
                return false;
            }

            var strictPreviousRoute = activeRoute;
            adapter.Stop();
            activeRoute = null;
            ClearActiveTargetTracking();
            nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
            status = $"10-minute priority review changed {strictPreviousRoute} to {strictRecommendation.Value.Route.Name} for {strictRecommendation.Value.Item.Name}.";
            return true;
        }

        if (DateTime.UtcNow - activeRouteStartedAt < ActiveRebalanceMinimumRuntime)
        {
            ResetActiveRebalanceCandidate();
            return false;
        }

        // Compare pure route utility here. Respawn detours belong between completed route selections and would
        // otherwise make a short route look stale while the character is still part-way through its first loop.
        var recommendation = SelectPhaseRoute(data, false);
        if (recommendation == null
            || string.Equals(recommendation.Value.Route.Name, activeRoute, StringComparison.OrdinalIgnoreCase))
        {
            ResetActiveRebalanceCandidate();
            return false;
        }

        if (!string.Equals(activeRebalanceCandidate, recommendation.Value.Route.Name, StringComparison.OrdinalIgnoreCase))
        {
            activeRebalanceCandidate = recommendation.Value.Route.Name;
            activeRebalanceCandidateSince = DateTime.UtcNow;
            return false;
        }
        if (DateTime.UtcNow - activeRebalanceCandidateSince < ActiveRebalanceStableWindow) return false;

        var previousRoute = activeRoute;
        var nextRoute = recommendation.Value.Route.Name;
        adapter.Stop();
        activeRoute = null;
        ClearActiveTargetTracking();
        nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
        status = $"Stopped {previousRoute}: {nextRoute} remained substantially better; recalculating.";
        return true;
    }

    private int SelectionGoal(VislandSnapshot data, ItemSnapshot item)
    {
        var target = config.Targets.GetValueOrDefault(item.Id, 1);
        var unfinishedTargetsRemain = ManagedItems(data).Any(x => x.IsAvailable && x.CurrentCount < config.Targets[x.Id]);
        return config.CompletionAction == CompletionAction.FarmAndExport && !unfinishedTargetsRemain
            ? ExportTrigger(item.Id)
            : target;
    }

    private void RequestRouteRecalculation()
    {
        nextStrictPriorityReviewAt = DateTime.MinValue;
        ResetActiveRebalanceCandidate();
    }

    private void ResetActiveRebalanceCandidate()
    {
        activeRebalanceCandidate = null;
        activeRebalanceCandidateSince = DateTime.MinValue;
    }

    private bool HandleActiveRouteStuck(VislandSnapshot data)
    {
        if (!config.SkipStuckRoutes || activeRoute == null || exportTrip || activeDiveStartedAt != null) return false;
        var player = services.Objects.LocalPlayer;
        if (player == null) return false;
        var inventoryTotal = UniqueItems(data).Sum(x => x.CurrentCount);
        if (activeLastPosition == null
            || Vector3.Distance(activeLastPosition.Value, player.Position) >= 1.5f
            || inventoryTotal != activeLastInventoryTotal)
        {
            activeLastPosition = player.Position;
            activeLastInventoryTotal = inventoryTotal;
            activeLastProgressAt = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow - activeLastProgressAt <= TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60))) return false;

        var routeName = activeRoute;
        adapter.Stop();
        activeRoute = null;
        ClearActiveTargetTracking();
        if (experimentalTestRunning)
        {
            experimentalTestRunning = false;
            status = $"Stopped experimental test {routeName}: no movement or gathering progress was detected.";
            experimentalStatus = status;
        }
        else
        {
            blockedRoutes[routeName] = DateTime.UtcNow.AddMinutes(5);
            nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
            status = $"Skipped {routeName}: no movement or gathering progress was detected. Cooling it down for 5 minutes.";
        }
        return true;
    }

    private void ClearActiveTargetTracking()
    {
        activeTargetItemId = null;
        activeTargetItemName = null;
        activeTargetGoal = null;
        activeLastPosition = null;
        activeLastInventoryTotal = 0;
        activeLastProgressAt = DateTime.MinValue;
        activeRouteStartedAt = DateTime.MinValue;
        ResetActiveRebalanceCandidate();
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

    private static unsafe void TryUseIsleReturn()
    {
        var actions = ActionManager.Instance();
        if (actions != null) actions->UseAction(ActionType.GeneralAction, 27);
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
        if ((!services.Condition[ConditionFlag.Swimming] && !services.Condition[ConditionFlag.Mounted])
            || services.Condition[ConditionFlag.Diving]
            || DateTime.UtcNow < nextDiveAttempt) return;
        nextDiveAttempt = DateTime.UtcNow.AddSeconds(2);
        try
        {
            dive ??= Marshal.GetDelegateForFunctionPointer<DiveDelegate>(services.SigScanner.ScanText(
                "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 1D ?? ?? ?? ?? 48 8D 54 24"));
            dive(Control.Instance());
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
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
            activeDiveStartedAt = null;
            return;
        }
        if (services.Condition[ConditionFlag.Diving])
        {
            activeDiveStartedAt = null;
            return;
        }
        if (!services.Condition[ConditionFlag.Swimming])
        {
            activeDiveStartedAt = null;
            return;
        }
        activeDiveStartedAt ??= DateTime.UtcNow;
        if (config.SkipStuckRoutes
            && DateTime.UtcNow - activeDiveStartedAt.Value > TimeSpan.FromSeconds(Math.Max(30, Math.Clamp(config.StuckTimeoutSeconds, 8, 60))))
        {
            adapter.Stop();
            blockedRoutes[route.Name] = DateTime.UtcNow.AddMinutes(5);
            activeRoute = null;
            ClearActiveTargetTracking();
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
        ClearActiveTargetTracking();
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
        ClearActiveTargetTracking();
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
        if (config.Version < 8)
        {
            config.UserRoutes ??= [];
            config.Version = 8;
            changed = true;
        }
        if (config.Version < 9)
        {
            config.GeneratorItems = config.EnabledItems.ToHashSet();
            config.Version = 9;
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

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectNextRoute(VislandSnapshot data, bool preferRespawnDetour = true)
    {
        var routes = SelectableRoutes(data);
        var candidates = ManagedItems(data).Where(x => x.IsAvailable)
            .Where(x => config.Targets[x.Id] > x.CurrentCount).ToList();
        if (config.ResourcePriority == ResourcePriority.FastestRoute)
        {
            if (preferRespawnDetour) routes = PreferRespawnDetour(routes, candidates);
            return SelectBestProgressRoute(routes, candidates, x => config.Targets[x.Id]);
        }

        // Strict priorities decide which resource wins before respawn protection is considered. Applying the
        // detour globally here could remove every route for the lowest-stock item and silently fall through to a
        // different resource. A detour may only choose between routes that still gather the selected item.
        var items = OrderItems(data, candidates, x => config.Targets[x.Id], routes);
        foreach (var item in items)
        {
            var itemRoutes = routes.Where(x => x.Items.Any(y => y.Id == item.Id && y.PerLoop > 0)).ToList();
            if (preferRespawnDetour) itemRoutes = PreferRespawnDetour(itemRoutes, [item]);
            var route = itemRoutes
                .Select(x => (Route: x, Item: x.Items.FirstOrDefault(y => y.Id == item.Id)))
                .Where(x => x.Item is { PerLoop: > 0 }).OrderByDescending(x => x.Item!.PerLoop)
                .ThenByDescending(x => RouteUtility(x.Route)).FirstOrDefault();
            if (route.Item != null) return (route.Route, item);
        }
        return null;
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectCowrieRoute(VislandSnapshot data, bool preferRespawnDetour = true)
    {
        var routes = SelectableRoutes(data);
        var items = ManagedItems(data).Where(x => x.IsAvailable)
            .Where(x => config.Targets.TryGetValue(x.Id, out var target) && target is > 0 and < 999)
            .Where(x => x.CurrentCount < ExportTrigger(x.Id)).ToList();
        if (preferRespawnDetour) routes = PreferRespawnDetour(routes, items);
        // Once every enabled resource has reached its retained stock, the user-selected balancing priority no
        // longer applies. Choose the best overall surplus route so cowrie farming favors useful yield and short
        // travel instead of repeatedly filling the same low-stock material.
        return SelectBestProgressRoute(routes, items, x => ExportTrigger(x.Id));
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectPhaseRoute(VislandSnapshot data, bool preferRespawnDetour = true)
    {
        var choice = SelectNextRoute(data, preferRespawnDetour);
        if (choice != null || config.CompletionAction != CompletionAction.FarmAndExport) return choice;
        return SelectCowrieRoute(data, preferRespawnDetour);
    }

    private (RouteSnapshot Route, ItemSnapshot Item)? SelectBestProgressRoute(
        IReadOnlyCollection<RouteSnapshot> routes, IReadOnlyCollection<ItemSnapshot> items, Func<ItemSnapshot, int> goal)
    {
        if (routes.Count == 0 || items.Count == 0) return null;
        var byId = items.ToDictionary(x => x.Id);
        var player = services.Objects.LocalPlayer;
        var ranked = routes.Select(route =>
        {
            var useful = route.Items
                .Where(x => byId.ContainsKey(x.Id))
                .Select(x =>
                {
                    var item = byId[x.Id];
                    var remaining = Math.Max(0, goal(item) - item.CurrentCount);
                    return (Item: item, RouteItem: x, Remaining: remaining,
                        UsefulNodes: Math.Min(x.PerLoop, remaining));
                })
                .Where(x => x.UsefulNodes > 0).ToList();
            var usefulNodes = useful.Sum(x => x.UsefulNodes);
            var coveredTypes = useful.Count;
            var weightedNeed = useful.Sum(x => x.UsefulNodes * (double)x.Remaining / Math.Max(1, goal(x.Item)));
            var physicalNodes = route.Nodes.GroupBy(NodeKey).Select(x => x.First()).ToList();
            var usefulPhysicalNodes = physicalNodes.Count(node => node.ItemIds.Any(id => byId.TryGetValue(id, out var item)
                && item.CurrentCount < goal(item)));
            var wastedNodes = Math.Max(0, physicalNodes.Count - usefulPhysicalNodes);
            var cycleDistance = RouteCycleDistance(route);
            var approachDistance = player == null || route.Waypoints.Count == 0
                ? 0
                : route.Waypoints.Min(x => Vector3.Distance(player.Position, x.Position));
            var travelCost = Math.Max(25, cycleDistance + approachDistance * 2 + route.Waypoints.Count * 6 + wastedNodes * 8);
            var progress = usefulNodes + weightedNeed + Math.Max(0, coveredTypes - 1);
            var score = progress / travelCost;
            var finishItem = useful.OrderBy(x => (double)x.Remaining / Math.Max(1, x.RouteItem.PerLoop))
                .ThenByDescending(x => x.UsefulNodes).Select(x => x.Item).FirstOrDefault();
            return (Route: route, Item: finishItem, Score: score, UsefulNodes: usefulNodes,
                CoveredTypes: coveredTypes, WastedNodes: wastedNodes);
        }).Where(x => x.Item != null && x.UsefulNodes > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.UsefulNodes)
            .ThenByDescending(x => x.CoveredTypes)
            .ThenBy(x => x.WastedNodes)
            .ToList();
        if (ranked.Count == 0) return null;
        var best = ranked[0];
        var currentArea = ranked.FirstOrDefault(x => string.Equals(x.Route.Name, lastFarmRoute, StringComparison.OrdinalIgnoreCase));
        if (currentArea.Item != null && currentArea.Score >= best.Score * .8) best = currentArea;
        return (best.Route, best.Item!);
    }

    private List<RouteSnapshot> PreferRespawnDetour(List<RouteSnapshot> routes, IReadOnlyCollection<ItemSnapshot> candidates)
    {
        if (lastFarmRoute == null || candidates.Count == 0) return routes;
        var previous = routes.FirstOrDefault(x => string.Equals(x.Name, lastFarmRoute, StringComparison.OrdinalIgnoreCase));
        if (previous == null) return routes;
        var previousNodes = previous.Nodes.GroupBy(NodeKey).Select(x => x.Key).ToHashSet();
        // Eleven physical nodes is the mathematical minimum, but a single failed interaction makes an immediate
        // repeat too short. Routes with only eleven nodes therefore take a useful detour when one is available.
        if (previousNodes.Count >= 12) return routes;

        var candidateIds = candidates.Select(x => x.Id).ToHashSet();
        var detours = routes.Where(x => !string.Equals(x.Name, previous.Name, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Items.Any(item => candidateIds.Contains(item.Id)))
            .Where(x => previousNodes.Concat(x.Nodes.Select(NodeKey)).Distinct().Count() >= 12)
            .ToList();
        return detours.Count > 0 ? detours : routes;
    }

    private static double RouteCycleDistance(RouteSnapshot route)
    {
        if (route.Waypoints.Count < 2) return 0;
        double result = 0;
        for (var i = 0; i < route.Waypoints.Count; i++)
            result += Vector3.Distance(route.Waypoints[i].Position, route.Waypoints[(i + 1) % route.Waypoints.Count].Position);
        return result;
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
        .Concat(GetUserRouteSnapshots(data, true))
        .Where(x => data.FlightUnlocked == true || !x.RequiresFlying)
        .Where(x => !IsInitialCaveRoute(x)
                    || data.FlightUnlocked == true && (data.IslandRank == null || data.IslandRank >= InitialCaveUnlockRank));

    private static bool IsInitialCaveRoute(RouteSnapshot route) =>
        route.Items.Any(x => InitialCaveResourceIds.Contains(x.Id));

    private static bool IsLowerInitialCaveRoute(RouteSnapshot route) =>
        route.Items.Any(x => x.Id is 39892 or 39893)
        || route.Nodes.Any(x => x.ObjectName.Equals("stalagmite", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<Vector3> GetCaveApproach(RouteSnapshot route) =>
        IsLowerInitialCaveRoute(route) ? LowerCaveApproach : UpperCaveApproach;

    private IReadOnlyList<Vector3> GetCaveExitApproach(Vector3 playerPosition)
    {
        var previousRoute = snapshot?.Routes
            .FirstOrDefault(x => string.Equals(x.Name, lastFarmRoute, StringComparison.OrdinalIgnoreCase));
        if (previousRoute != null && IsInitialCaveRoute(previousRoute)) return GetCaveApproach(previousRoute);

        return Vector3.DistanceSquared(playerPosition, LowerCaveApproach[^1])
               < Vector3.DistanceSquared(playerPosition, UpperCaveApproach[^1])
            ? LowerCaveApproach
            : UpperCaveApproach;
    }

    private static bool IsInsideInitialCave(Vector3 position) =>
        position.X is >= 270f and <= 510f && position.Z <= -225f && position.Y <= 112f;

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
            if (ImGui.BeginTabItem("Routes"))
            {
                DrawRouteWorkbench();
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

        var routes = snapshot.Routes.Concat(GetUserRouteSnapshots(snapshot, false)).ToList();
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
        var allItems = UniqueItems(snapshot).OrderBy(x => x.Name).ToList();
        if (mapResourceId == null || allItems.All(x => x.Id != mapResourceId)) mapResourceId = allItems.FirstOrDefault()?.Id;

        var displayMode = (int)mapDisplayMode;
        ImGui.SetNextItemWidth(230);
        if (ImGui.Combo("View", ref displayMode, "Selected route\0All registered nodes\0Specific resource\0"))
            mapDisplayMode = (MapDisplayMode)displayMode;
        ImGui.SameLine();
        if (ImGui.Button("Reset view")) { mapZoom = 1; mapPan = Vector2.Zero; }
        ImGui.SameLine(); ImGui.TextDisabled($"{mapZoom:F1}x");

        if (mapDisplayMode == MapDisplayMode.SelectedRoute)
        {
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
        }
        else if (mapDisplayMode == MapDisplayMode.Resource && allItems.Count > 0)
        {
            var selected = allItems.First(x => x.Id == mapResourceId);
            ImGui.SetNextItemWidth(Math.Min(420, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("Resource", selected.Name, ImGuiComboFlags.HeightLarge))
            {
                foreach (var item in allItems)
                {
                    if (!ImGui.Selectable(item.Name, item.Id == mapResourceId)) continue;
                    mapResourceId = item.Id;
                    selected = item;
                }
                ImGui.EndCombo();
            }
        }

        var allNodes = GetKnownNodes(snapshot);
        var visibleNodes = mapDisplayMode switch
        {
            MapDisplayMode.AllNodes => allNodes,
            MapDisplayMode.Resource when mapResourceId.HasValue => allNodes.Where(x => x.ItemIds.Contains(mapResourceId.Value)).ToList(),
            _ => route.Nodes.GroupBy(NodeKey).Select(x => x.First()).ToList(),
        };
        if (mapDisplayMode != MapDisplayMode.SelectedRoute)
            ImGui.TextWrapped($"Showing {visibleNodes.Count} registered gathering node(s). Routes are hidden in this view.");
        ImGui.TextDisabled("Mouse wheel: zoom around cursor. Left-drag: pan. Hover a gathering point for resources and coordinates.");

        var available = ImGui.GetContentRegionAvail();
        var side = Math.Clamp(Math.Min(available.X, available.Y), 240f, 900f);
        var canvasSize = new Vector2(side, side);
        if (available.X > side) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (available.X - side) * .5f);
        ImGui.InvisibleButton("##RouteMapCanvas", canvasSize);
        var topLeft = ImGui.GetItemRectMin();
        var bottomRight = ImGui.GetItemRectMax();
        var mouse = ImGui.GetMousePos();
        var mapHovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        if (mapHovered && Math.Abs(io.MouseWheel) > .01f)
        {
            var oldZoom = mapZoom;
            mapZoom = Math.Clamp(mapZoom * MathF.Pow(1.15f, io.MouseWheel), .75f, 5f);
            var baseCenter = topLeft + canvasSize * .5f;
            var relative = mouse - baseCenter - mapPan;
            mapPan += relative * (1 - mapZoom / oldZoom);
        }
        if (mapHovered && ImGui.IsMouseDown(ImGuiMouseButton.Left)) mapPan += io.MouseDelta;
        var center = topLeft + canvasSize * .5f + mapPan;
        var draw = ImGui.GetWindowDrawList();
        var background = ImGui.ColorConvertFloat4ToU32(new Vector4(.035f, .055f, .06f, .96f));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(.65f, .7f, .7f, 1));
        var grid = ImGui.ColorConvertFloat4ToU32(new Vector4(.75f, .82f, .8f, .22f));
        draw.AddRectFilled(topLeft, bottomRight, background);
        draw.PushClipRect(topLeft, bottomRight, true);
        var mapTexture = services.Textures.GetFromGame(IslandMapTexturePath).GetWrapOrEmpty();
        if (mapTexture != null)
        {
            var halfTexture = canvasSize * .5f * mapZoom;
            draw.AddImage(mapTexture.Handle, center - halfTexture, center + halfTexture);
        }

        // FFXIV map rows are centered on pixel 1024 of a 2048px texture. Island Sanctuary is map h1m2/02,
        // SizeFactor 100 with offsets (-175, 138), so route positions can be placed without hand-tuned bounds.
        Vector2 ToCanvas(Vector3 world) => new(
            center.X + (world.X + IslandMapOffsetX) * IslandMapSizeFactor * canvasSize.X / 2048f * mapZoom,
            center.Y + (world.Z + IslandMapOffsetZ) * IslandMapSizeFactor * canvasSize.Y / 2048f * mapZoom);
        for (var x = -800; x <= 800; x += 200)
        {
            var a = ToCanvas(new Vector3(x, 0, -1100));
            var b = ToCanvas(new Vector3(x, 0, 1100));
            draw.AddLine(a, b, grid);
        }
        for (var z = -800; z <= 800; z += 200)
        {
            var a = ToCanvas(new Vector3(-1100, 0, z));
            var b = ToCanvas(new Vector3(1100, 0, z));
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

        if (mapDisplayMode == MapDisplayMode.SelectedRoute)
        {
            for (var index = 0; index < route.Waypoints.Count; index++)
            {
                var from = ToCanvas(route.Waypoints[index].Position);
                var next = route.Waypoints[(index + 1) % route.Waypoints.Count];
                draw.AddLine(from, ToCanvas(next.Position), MovementColor(next), 2f);
            }
        }

        RouteWaypointSnapshot? hovered = null;
        RouteNodeSnapshot? hoveredNode = null;
        var hoveredIndex = -1;
        if (mapDisplayMode == MapDisplayMode.SelectedRoute)
        {
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
                    hoveredNode = visibleNodes.FirstOrDefault(x => x.ObjectId == point.ObjectId && point.ObjectId != 0)
                                  ?? visibleNodes.FirstOrDefault(x => Vector3.Distance(x.Position, point.Position) < 1f);
                }
            }
        }
        else
        {
            var nodeColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.35f, .95f, .55f, 1));
            foreach (var node in visibleNodes)
            {
                var screen = ToCanvas(node.Position);
                draw.AddCircleFilled(screen, 5f, RouteAccessibility.IsUnderwater(node.Position) ? underwaterColor : nodeColor);
                draw.AddCircle(screen, 6.5f, 0xC0000000, 0, 1.5f);
                if (Vector2.Distance(mouse, screen) <= 10) hoveredNode = node;
            }
        }

        var player = services.Objects.LocalPlayer;
        if (player != null)
        {
            var playerColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .95f, .2f, 1));
            draw.AddCircleFilled(ToCanvas(player.Position), 6f, playerColor);
            draw.AddCircle(ToCanvas(player.Position), 8f, 0xFF000000, 0, 2f);
        }
        draw.AddText(topLeft + new Vector2(8, 7), 0xFFFFFFFF, "N");
        draw.PopClipRect();
        draw.AddRect(topLeft, bottomRight, border);

        if (mapHovered)
        {
            var worldAtMouse = new Vector2(
                (mouse.X - center.X) * 2048f / canvasSize.X / IslandMapSizeFactor / mapZoom - IslandMapOffsetX,
                (mouse.Y - center.Y) * 2048f / canvasSize.Y / IslandMapSizeFactor / mapZoom - IslandMapOffsetZ);
            ImGui.BeginTooltip();
            if (hovered != null)
            {
                ImGui.TextUnformatted($"Waypoint #{hoveredIndex + 1}");
                ImGui.TextUnformatted($"X {hovered.Position.X:F1}  Y {hovered.Position.Y:F1}  Z {hovered.Position.Z:F1}");
                ImGui.TextUnformatted(RouteAccessibility.IsUnderwater(hovered.Position) ? "Underwater" : hovered.Movement.ToString());
                if (!string.IsNullOrWhiteSpace(hovered.ObjectName)) ImGui.TextUnformatted(hovered.ObjectName);
            }
            if (hoveredNode != null)
            {
                if (hovered == null) ImGui.TextUnformatted($"X {hoveredNode.Position.X:F1}  Y {hoveredNode.Position.Y:F1}  Z {hoveredNode.Position.Z:F1}");
                var names = hoveredNode.ItemIds.Select(id => allItems.FirstOrDefault(x => x.Id == id)?.Name ?? id.ToString()).Distinct();
                ImGui.TextWrapped($"Resources: {string.Join(", ", names)}");
                if (!string.IsNullOrWhiteSpace(hoveredNode.ObjectName)) ImGui.TextDisabled(hoveredNode.ObjectName);
            }
            else if (hovered == null) ImGui.TextUnformatted($"X {worldAtMouse.X:F1}  Z {worldAtMouse.Y:F1}");
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
                "Largest relative deficit (strict)\0Lowest current stock (strict)\0Highest current stock (strict)\0Best overall route progress (recommended)\0"))
        {
            config.ResourcePriority = (ResourcePriority)priority;
            RequestRouteRecalculation();
            Save();
        }
        ImGui.TextDisabled(config.ResourcePriority switch
        {
            ResourcePriority.LowestStock => "Farms the enabled resource with the smallest raw inventory count first.",
            ResourcePriority.HighestStock => "Farms the enabled unfinished resource with the largest raw inventory count first.",
            ResourcePriority.FastestRoute => "Balances useful unfinished resources per loop against route length, approach distance, and already-complete nodes.",
            _ => "Strictly farms the largest current/target deficit first, even when its compatible route has a low yield.",
        });
        ImGui.TextDisabled(config.ResourcePriority == ResourcePriority.FastestRoute
            ? "Best overall is reviewed continuously after the route has run for 45 seconds; a better choice must remain stable before switching."
            : "Strict modes keep the chosen target, then review priorities every 10 minutes, when it reaches its target, or immediately after settings change.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Stuck recovery");
        var skipStuck = config.SkipStuckRoutes;
        if (ImGui.Checkbox("Temporarily skip a route when navigation stops making progress", ref skipStuck))
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
        ImGui.TextDisabled("Allowed range: 8-60 seconds. Shorter timeouts can trigger during normal pathfinding, mounting, or gathering.");
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
            RequestRouteRecalculation();
            Save();
        }
        var maximumTarget = config.CompletionAction == CompletionAction.FarmAndExport ? Math.Max(1, 999 - config.ExportBatch) : 999;
        var bulk = config.BulkTarget; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt($"{targetLabel} for all", ref bulk)) { config.BulkTarget = Math.Clamp(bulk, 1, maximumTarget); Save(); }
        ImGui.SameLine(); if (ImGui.Button("Apply##farm"))
        {
            foreach (var id in config.Targets.Keys.ToList()) config.Targets[id] = Math.Min(config.BulkTarget, maximumTarget);
            RequestRouteRecalculation();
            Save();
        }
        if (config.CompletionAction == CompletionAction.FarmAndExport)
        {
            ImGui.SameLine(); var batch = config.ExportBatch; ImGui.SetNextItemWidth(75);
            var highestTarget = config.EnabledItems.Select(id => config.Targets.GetValueOrDefault(id, 1)).DefaultIfEmpty(1).Max();
            if (ImGui.InputInt("Export batch", ref batch))
            {
                config.ExportBatch = Math.Clamp(batch, 1, Math.Max(1, 999 - highestTarget));
                RequestRouteRecalculation();
                Save();
            }
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
                RequestRouteRecalculation();
                Save();
            }
            if (!canFarm) ImGui.EndDisabled();
            ImGui.SameLine();
            if (!item.IsAvailable) ImGui.TextDisabled($"{item.Name} (tool locked)");
            else if (InitialCaveResourceIds.Contains(item.Id) && data.IslandRank is < InitialCaveUnlockRank)
                ImGui.TextDisabled($"{item.Name} (cave unlocks at rank {InitialCaveUnlockRank})");
            else if (!hasCompatibleRoute) ImGui.TextDisabled($"{item.Name} (access unavailable)");
            else ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.CurrentCount.ToString());
            ImGui.TableNextColumn(); var target = config.Targets[item.Id]; ImGui.SetNextItemWidth(65);
            if (!item.IsAvailable) ImGui.BeginDisabled();
            if (ImGui.InputInt($"##target{item.Id}", ref target))
            {
                config.Targets[item.Id] = Math.Clamp(target, 1, maximumTarget);
                RequestRouteRecalculation();
                Save();
            }
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
            RequestRouteRecalculation();
            Save();
        }
        var autoTravel = config.AutoTravelToIsland;
        if (!adapter.IsLifestreamAvailable) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Travel to Island with Lifestream when starting", ref autoTravel)) { config.AutoTravelToIsland = autoTravel; Save(); }
        if (!adapter.IsLifestreamAvailable) ImGui.EndDisabled();
        if (!adapter.IsLifestreamAvailable) ImGui.TextDisabled("Optional: install Lifestream to enable Island travel.");
        if (config.CompletionAction != CompletionAction.FarmAndExport) return;
        if (snapshot?.AutoExportEnabled == true)
            ImGui.TextColored(new Vector4(1f, .7f, .25f, 1), "Visland Auto Export is on; Stock Manager will disable it before opening the exporter.");
        else ImGui.TextDisabled("Visland Auto Export is off; Stock Manager uses the individual Sell above values below.");
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
        if (data.IslandRank is < InitialCaveUnlockRank)
            ImGui.TextDisabled($"Rank {InitialCaveUnlockRank} cave routes and their exclusive resources are ignored until the cave expansion is available.");
        else if (data.IslandRank >= InitialCaveUnlockRank)
            ImGui.TextDisabled("Cave routes use a guided flight through the entrance before Visland starts the gathering loop.");
        ImGui.TextDisabled($"Considering {compatible.Count} of {data.Routes.Count} imported Island routes.");
        if (config.ResourcePriority == ResourcePriority.FastestRoute)
        {
            var next = SelectPhaseRoute(data, false);
            if (data.IsRunning && activeRoute != null)
            {
                var activeItem = activeTargetItemId.HasValue
                    ? UniqueItems(data).FirstOrDefault(x => x.Id == activeTargetItemId.Value)
                    : null;
                var trigger = activeItem == null || !activeTargetGoal.HasValue
                    ? ""
                    : $" | tracked target: {activeItem.Name} {activeItem.CurrentCount}/{activeTargetGoal.Value}";
                ImGui.TextColored(new Vector4(.45f, .85f, 1f, 1), $"Active overall route: {activeRoute}{trigger}");
                if (next != null && !string.Equals(next.Value.Route.Name, activeRoute, StringComparison.OrdinalIgnoreCase))
                    ImGui.TextColored(new Vector4(1f, .75f, .3f, 1),
                        $"Next recommendation: {next.Value.Route.Name} for {next.Value.Item.Name} (auto-switches if it remains substantially better)");
                else ImGui.TextDisabled("The active route remains the best overall choice.");
            }
            else if (next != null)
                ImGui.TextColored(new Vector4(.45f, .85f, 1f, 1),
                    $"Best overall now: {next.Value.Route.Name} | tracked target: {next.Value.Item.Name}");
            ImGui.TextDisabled("This mode scores all unfinished enabled resources in each route and includes loop length, approach distance, and wasted completed nodes.");
        }
        else
        {
            var next = SelectPhaseRoute(data);
            if (next != null)
                ImGui.TextColored(new Vector4(.45f, .85f, 1f, 1),
                    $"Strict-priority choice now: {next.Value.Item.Name} via {next.Value.Route.Name}");
            if (data.IsRunning && activeRoute != null && activeTargetItemName != null)
            {
                var reviewIn = nextStrictPriorityReviewAt <= DateTime.UtcNow
                    ? "now"
                    : $"{Math.Max(1, (int)Math.Ceiling((nextStrictPriorityReviewAt - DateTime.UtcNow).TotalMinutes))} min";
                ImGui.TextDisabled($"Active strict target: {activeTargetItemName} via {activeRoute}; next scheduled review: {reviewIn}.");
            }
            ImGui.TextDisabled("Strict modes choose the resource first. Respawn detours may change its route, but never replace it with another resource.");
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Highest direct yield by resource (reference only):");
        ImGui.TextDisabled("This is not the Best overall decision. Matching nodes per loop are compared first; ties prefer routes that also advance other unfinished resources.");
        foreach (var item in UniqueItems(data).Where(x => IsEffectivelyEnabled(x.Id)).OrderBy(x => x.Name))
        {
            var candidates = compatible.Select(route => (Route: route, Item: route.Items.FirstOrDefault(x => x.Id == item.Id)))
                .Where(x => x.Item != null).OrderByDescending(x => x.Item!.PerLoop).ThenByDescending(x => RouteUtility(x.Route)).ToList();
            if (candidates.Count == 0) ImGui.TextColored(new Vector4(1f, .45f, .3f, 1), $"{item.Name}: no compatible route");
            else
            {
                var best = candidates[0];
                ImGui.TextUnformatted($"{item.Name}: {best.Route.Name}");
                ImGui.TextDisabled($"  ~{best.Item!.PerLoop} matching node(s) per loop; {candidates.Count} compatible route(s) contain this resource");
            }
        }

    }

    private void DrawExperimentalRouteGenerator(VislandSnapshot data)
    {
        if (!ImGui.CollapsingHeader("Experimental route generator")) return;
        ImGui.TextWrapped("Builds a compact temporary route from gathering nodes already present in imported Visland routes. Nearby target nodes are added before distant detours.");
        ImGui.TextColored(new Vector4(1f, .75f, .25f, 1), "Experimental: inspect and test the result before relying on it.");
        DrawGeneratorResourceSelector(data);
        var limit = experimentalNodeLimit; ImGui.SetNextItemWidth(75);
        if (ImGui.InputInt("Maximum nodes", ref limit)) experimentalNodeLimit = Math.Clamp(limit, 12, 30);
        ImGui.TextDisabled("At least 12 unique nodes provide a one-node respawn safety margin; short nearby hops stay on foot.");

        var canGenerate = data.FlightUnlocked != null && !config.Enabled && !data.IsRunning
                          && pendingRouteStart == null && !experimentalTestRunning;
        if (!canGenerate) ImGui.BeginDisabled();
        if (ImGui.Button("Generate preview")) GenerateExperimentalRoute(data);
        if (!canGenerate) ImGui.EndDisabled();
        ImGui.SameLine();
        var canRun = canGenerate && experimentalRoute != null && adapter.IsNavmeshReady
                     && (data.FlightUnlocked == true || !experimentalRoute.RequiresFlying);
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
        else
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1),
                "Generated legs are experimental: vnavmesh may select inaccessible or progression-gated geometry. Supervise tests and use Stop test loop if movement looks wrong.");
        if (experimentalRoute != null && ImGui.Button("Save preview as editable route"))
        {
            var saved = CreateUserRoute(experimentalRoute, "Generated route");
            config.UserRoutes.Add(saved);
            routeWorkbenchUserRouteId = saved.Id;
            Save();
            experimentalStatus = $"Saved {saved.Name} in the Routes editor.";
        }
    }

    private void DrawGeneratorResourceSelector(VislandSnapshot data)
    {
        var compatible = CompatibleRoutes(data).ToList();
        var items = compatible.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First())
            .Where(x => x.IsAvailable).OrderBy(x => x.Name).ToList();
        var selected = items.Where(x => config.GeneratorItems.Contains(x.Id)).ToList();
        var preview = selected.Count switch
        {
            0 => "Select resources...",
            <= 3 => string.Join(", ", selected.Select(x => x.Name)),
            _ => $"{selected.Count} resources selected",
        };

        ImGui.SetNextItemWidth(Math.Min(520, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Generator resources", preview, ImGuiComboFlags.HeightLarge))
        {
            foreach (var item in items)
            {
                var enabled = config.GeneratorItems.Contains(item.Id);
                if (!ImGui.Checkbox($"{item.Name}##generator{item.Id}", ref enabled)) continue;
                if (enabled) config.GeneratorItems.Add(item.Id);
                else config.GeneratorItems.Remove(item.Id);
                Save();
            }
            ImGui.EndCombo();
        }

        if (ImGui.SmallButton("Use Automation selection"))
        {
            config.GeneratorItems = items.Where(x => config.EnabledItems.Contains(x.Id)).Select(x => x.Id).ToHashSet();
            Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Select all available"))
        {
            config.GeneratorItems = items.Select(x => x.Id).ToHashSet();
            Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            config.GeneratorItems.Clear();
            Save();
        }
        ImGui.TextDisabled(selected.Count == 0
            ? "Choose one or more resources specifically for this generated route."
            : $"The preview will cover: {string.Join(", ", selected.Select(x => x.Name))}.");
    }

    private void GenerateExperimentalRoute(VislandSnapshot data)
    {
        experimentalRoute = null;
        var compatible = CompatibleRoutes(data).ToList();
        var activeItems = compatible.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First())
            .Where(x => x.IsAvailable && config.GeneratorItems.Contains(x.Id))
            .OrderBy(x => x.Name).ToList();
        if (activeItems.Count == 0)
        {
            experimentalStatus = "Select at least one available generator resource with a compatible route.";
            return;
        }

        var availableIds = compatible.SelectMany(x => x.Items).Where(x => x.IsAvailable).Select(x => x.Id).ToHashSet();
        var allNodes = compatible.SelectMany(x => x.Nodes)
            .Where(x => x.ItemIds.Any(availableIds.Contains))
            .GroupBy(NodeKey).Select(x => x.First()).ToList();
        var activeIds = activeItems.Select(x => x.Id).ToHashSet();
        var targetNodes = allNodes.Where(x => x.ItemIds.Any(activeIds.Contains)).ToList();
        if (targetNodes.Count == 0)
        {
            experimentalStatus = "No usable gathering nodes were found for the selected generator resources.";
            return;
        }

        var selected = SelectClusteredNodes(targetNodes, activeItems, experimentalNodeLimit);
        if (selected.Count == 0)
        {
            experimentalStatus = $"The selected resources cannot all fit within {experimentalNodeLimit} nodes. Increase Maximum nodes or select fewer resources.";
            return;
        }
        while (selected.Count < 12)
        {
            var support = allNodes.Where(x => !selected.Contains(x))
                .OrderBy(x => InsertionCost(selected, x)).FirstOrDefault();
            if (support == null) break;
            selected.Add(support);
        }
        if (selected.Count < 12)
        {
            experimentalStatus = $"Only {selected.Count} unique nodes are available; at least 12 are required for a resilient loop.";
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
        if (data.AutoExportEnabled)
        {
            if (!adapter.TryDisableBuiltInAutoExport(out var autoExportError))
            {
                status = $"Waiting to take over Visland Auto Export: {autoExportError}";
                return true;
            }
            status = "Visland Auto Export disabled; continuing to the exporter.";
            return true;
        }
        var shop = (AtkUnitBase*)services.GameGui.GetAddonByName("MJIDisposeShop").Address;
        if (shop != null && shop->IsVisible)
        {
            if (data.IsRunning) adapter.Stop();
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
                exportTrip = false;
                activeRoute = null;
                leavingExporter = true;
                exporterExitNavigationRequested = false;
                exporterExitStartedAt = DateTime.UtcNow;
                exporterExitLastProgressAt = DateTime.UtcNow;
                exporterExitLastDistance = float.MaxValue;
                nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
                status = "Export complete; walking outside before resuming farming.";
            }
            return true;
        }
        var select = (AtkUnitBase*)services.GameGui.GetAddonByName("SelectString").Address;
        if (select != null && select->IsVisible && select->IsReady)
        {
            if (data.IsRunning) adapter.Stop();
            var value = stackalloc AtkValue[1]; value[0].Type = AtkValueType.Int; value[0].Int = 0; select->FireCallback(1, value);
            status = "Opening Export Materials..."; return true;
        }

        var player = services.Objects.LocalPlayer;
        var exporter = services.Objects.FirstOrDefault(x => x.BaseId == ExporterObjectId && x.IsTargetable);
        if (player != null && exporter != null && Vector3.Distance(player.Position, exporter.Position) <= 7f)
        {
            if (data.IsRunning)
            {
                adapter.Stop();
                status = "Reached the Island exporter; stopping the travel route before interaction...";
                return true;
            }
            if (DateTime.UtcNow >= nextExporterInteractionAttempt)
            {
                nextExporterInteractionAttempt = DateTime.UtcNow.AddSeconds(2);
                var targetSystem = TargetSystem.Instance();
                if (targetSystem != null)
                    targetSystem->InteractWithObject((GameObject*)exporter.Address, false);
            }
            status = "Talking to the Island exporter...";
            return true;
        }

        if (data.IsRunning) { status = "Going to the Island exporter..."; return true; }
        if (DateTime.UtcNow - exportTripStarted > TimeSpan.FromSeconds(60))
        {
            exportTrip = false;
            activeRoute = null;
            status = "Export trip timed out; the exporter could not be reached or opened.";
        }
        return exportTrip;
    }

    private void HandleExporterExit(VislandSnapshot data)
    {
        if (data.IsRunning) adapter.Stop();
        var player = services.Objects.LocalPlayer;
        if (player == null)
        {
            status = "Waiting for the character before leaving the exporter.";
            return;
        }

        var distance = Vector3.Distance(player.Position, IslandBaseExterior);
        if (distance <= 6f)
        {
            adapter.StopNavigation();
            leavingExporter = false;
            exporterExitNavigationRequested = false;
            nextStartAttempt = DateTime.UtcNow.AddSeconds(1);
            status = "Outside the exporter; recalculating the next route.";
            return;
        }

        if (distance + 1f < exporterExitLastDistance)
        {
            exporterExitLastDistance = distance;
            exporterExitLastProgressAt = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - exporterExitLastProgressAt
                 > TimeSpan.FromSeconds(Math.Clamp(config.StuckTimeoutSeconds, 8, 60)))
        {
            adapter.StopNavigation();
            exporterExitNavigationRequested = false;
            TryUseIsleReturn();
            nextIsleReturnAttempt = DateTime.UtcNow.AddSeconds(2);
            exporterExitLastProgressAt = DateTime.UtcNow;
            status = "The cabin exit was blocked; using Isle Return to reach the base exterior...";
            return;
        }

        if (exporterExitNavigationRequested && !adapter.IsNavigationBusy)
            exporterExitNavigationRequested = false;
        if (!exporterExitNavigationRequested)
        {
            if (!adapter.TryNavigateTo(IslandBaseExterior, false, out var error))
            {
                if (DateTime.UtcNow - exporterExitStartedAt > TimeSpan.FromSeconds(10)
                    && DateTime.UtcNow >= nextIsleReturnAttempt)
                {
                    TryUseIsleReturn();
                    nextIsleReturnAttempt = DateTime.UtcNow.AddSeconds(2);
                    status = "Could not path out of the cabin; using Isle Return...";
                }
                else status = $"Walking out of the exporter failed: {error}";
                return;
            }
            exporterExitNavigationRequested = true;
        }
        status = $"Walking out of the exporter before mounting... ({distance:F0} yalms)";
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

    private enum MapDisplayMode
    {
        SelectedRoute,
        AllNodes,
        Resource,
    }

    private sealed class PendingRouteStart(RouteSnapshot route, int? itemId, string? itemName, int? itemGoal,
        PendingRoutePurpose purpose, int startIndex)
    {
        public RouteSnapshot Route { get; } = route;
        public int? ItemId { get; } = itemId;
        public string? ItemName { get; } = itemName;
        public int? ItemGoal { get; } = itemGoal;
        public PendingRoutePurpose Purpose { get; } = purpose;
        public int StartIndex { get; } = startIndex;
        public bool NavigationRequested { get; set; }
        public bool NavigationWasThreeDimensional { get; set; }
        public float LastProgressDistance { get; set; } = float.MaxValue;
        public DateTime LastProgressAt { get; set; } = DateTime.UtcNow;
        public DateTime? DiveStartedAt { get; set; }
        public DateTime? MountStartedAt { get; set; }
        public bool UseIsleReturn { get; set; }
        public DateTime? IsleReturnStartedAt { get; set; }
        public bool ExitingCabin { get; set; }
        public bool CaveApproach { get; set; }
        public bool CaveExit { get; set; }
        public int CaveApproachStage { get; set; }
        public bool CaveNavigationInitialized { get; set; }
        public IReadOnlyList<Vector3>? CaveTransitionPath { get; set; }
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
        [PluginService] internal ITextureProvider Textures { get; private init; } = null!;
        [PluginService] internal IObjectTable Objects { get; private init; } = null!;
        [PluginService] internal IGameGui GameGui { get; private init; } = null!;
        [PluginService] internal IChatGui ChatGui { get; private init; } = null!;
        [PluginService] internal ISigScanner SigScanner { get; private init; } = null!;
        [PluginService] internal IGameInteropProvider GameInterop { get; private init; } = null!;
        [PluginService] internal IPluginLog Log { get; private init; } = null!;
    }
}
