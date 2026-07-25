using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Numerics;

namespace StockManager;

internal sealed class VislandAdapter
{
    private readonly ICallGateSubscriber<bool> isRouteRunning;
    private readonly ICallGateSubscriber<string, bool, object> startRoute;
    private readonly ICallGateSubscriber<object> stopRoute;
    private readonly ICallGateSubscriber<bool> navmeshReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> navmeshMoveTo;
    private readonly ICallGateSubscriber<bool> navmeshPathfindInProgress;
    private readonly ICallGateSubscriber<bool> navmeshPathRunning;
    private readonly ICallGateSubscriber<object> navmeshStop;
    private readonly ICallGateSubscriber<string, object> lifestreamExecute;
    private readonly ICallGateSubscriber<bool> lifestreamBusy;
    private readonly ICallGateSubscriber<object> lifestreamAbort;
    private readonly string configPath;

    public VislandAdapter(IDalamudPluginInterface pluginInterface)
    {
        isRouteRunning = pluginInterface.GetIpcSubscriber<bool>("visland.IsRouteRunning");
        startRoute = pluginInterface.GetIpcSubscriber<string, bool, object>("visland.StartRoute");
        stopRoute = pluginInterface.GetIpcSubscriber<object>("visland.StopRoute");
        navmeshReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        navmeshMoveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        navmeshPathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        navmeshPathRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        navmeshStop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        lifestreamExecute = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        lifestreamBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamAbort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
        var configRoot = pluginInterface.ConfigDirectory.Parent?.FullName
                         ?? throw new InvalidOperationException("Dalamud configuration directory is unavailable.");
        configPath = Path.Combine(configRoot, "visland.json");
    }

    public bool TryGetSnapshot(out VislandSnapshot? snapshot, out string error)
    {
        snapshot = null;
        try
        {
            if (!File.Exists(configPath))
            {
                error = "Visland configuration was not found. Install Visland and import Island routes first.";
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("Payload", out var payload)
                || !payload.TryGetProperty("visland.Gathering.GatherRouteDB", out var database)
                || !database.TryGetProperty("Routes", out var routesElement))
            {
                error = "Visland route database is missing from visland.json.";
                return false;
            }

            var routes = new List<RouteSnapshot>();
            foreach (var route in routesElement.EnumerateArray())
            {
                var name = route.GetProperty("Name").GetString() ?? "Unnamed route";
                var group = route.TryGetProperty("Group", out var groupElement) ? groupElement.GetString() ?? "" : "";
                if (!group.Equals("Island", StringComparison.OrdinalIgnoreCase))
                    continue;

                var items = new Dictionary<int, ItemSnapshot>();
                var routeNodes = new List<RouteNodeSnapshot>();
                var routeWaypoints = new List<RouteWaypointSnapshot>();
                if (route.TryGetProperty("Waypoints", out var waypoints))
                {
                    foreach (var waypoint in waypoints.EnumerateArray())
                    {
                        var movementName = waypoint.TryGetProperty("Movement", out var movement) ? movement.GetString() : null;
                        var routeMovement = ParseMovement(movementName);
                        var position = new Vector3(
                            waypoint.TryGetProperty("X", out var x) ? x.GetSingle() : 0,
                            waypoint.TryGetProperty("Y", out var y) ? y.GetSingle() : 0,
                            waypoint.TryGetProperty("Z", out var z) ? z.GetSingle() : 0);
                        var radiusValue = waypoint.TryGetProperty("Radius", out var radius) ? Math.Max(1, radius.GetSingle()) : 3;
                        var objectId = waypoint.TryGetProperty("InteractWithOID", out var oid) ? oid.GetUInt32() : 0;
                        var zoneId = waypoint.TryGetProperty("ZoneID", out var zone) ? zone.GetUInt32() : 0;
                        var nodeName = waypoint.TryGetProperty("InteractWithName", out var nodeElement) ? nodeElement.GetString()?.Trim() ?? "" : "";
                        routeWaypoints.Add(new RouteWaypointSnapshot(
                            position,
                            zoneId,
                            radiusValue,
                            routeMovement,
                            waypoint.TryGetProperty("Pathfind", out var pathfind) && pathfind.GetBoolean(),
                            objectId,
                            nodeName,
                            new Vector3(
                                waypoint.TryGetProperty("iX", out var ix) ? ix.GetSingle() : 0,
                                waypoint.TryGetProperty("iY", out var iy) ? iy.GetSingle() : 0,
                                waypoint.TryGetProperty("iZ", out var iz) ? iz.GetSingle() : 0),
                            ParseInteraction(waypoint.TryGetProperty("Interaction", out var interaction) ? interaction.GetString() : null),
                            waypoint.TryGetProperty("showInteractions", out var showInteractions) && showInteractions.GetBoolean(),
                            waypoint.TryGetProperty("showWaits", out var showWaits) && showWaits.GetBoolean(),
                            ParseCondition(waypoint.TryGetProperty("WaitForCondition", out var waitCondition) ? waitCondition.GetString() : null),
                            waypoint.TryGetProperty("WaitTimeMs", out var waitTime) ? waitTime.GetInt32() : 0,
                            ReadVector2(waypoint, "WaitTimeET"),
                            waypoint.TryGetProperty("RouteName", out var routeName) ? routeName.GetString() ?? "" : ""));

                        if (string.IsNullOrWhiteSpace(nodeName) || !IslandResources.ByNode.TryGetValue(nodeName, out var nodeItems))
                            continue;
                        foreach (var resource in nodeItems)
                        {
                            var (count, available) = GetItemState(resource.Id);
                            items[resource.Id] = items.TryGetValue(resource.Id, out var existing)
                                ? existing with { PerLoop = existing.PerLoop + 1, CurrentCount = count, IsAvailable = available }
                                : new ItemSnapshot(resource.Id, resource.Name, 1, count, available);
                        }
                        if (objectId != 0)
                            routeNodes.Add(new RouteNodeSnapshot(position, zoneId, objectId, nodeName, nodeItems.Select(x => x.Id).ToArray()));
                    }
                }

                var hasUnderwaterWaypoints = routeWaypoints.Any(x => RouteAccessibility.IsUnderwater(x.Position));
                var hasFlightMovement = routeWaypoints.Any(x => x.Movement == RouteMovement.MountFly);
                var requiresFlying = name.Contains("flying", StringComparison.OrdinalIgnoreCase)
                                     || routeWaypoints.Any(x => RouteAccessibility.IsFlightOnlyAltitude(x.Position))
                                     || (hasFlightMovement && !hasUnderwaterWaypoints);

                if (items.Count > 0 && routeWaypoints.Count > 0)
                    routes.Add(new RouteSnapshot(
                        name,
                        group,
                        requiresFlying,
                        route.TryGetProperty("Food", out var food) ? food.GetInt32() : 0,
                        route.TryGetProperty("TargetGatherItem", out var targetGatherItem) ? targetGatherItem.GetInt32() : 0,
                        items.Values.OrderBy(x => x.Name).ToList(),
                        routeNodes,
                        routeWaypoints));
            }

            var autoExport = false;
            if (payload.TryGetProperty("visland.Export.ExportConfig", out var exportConfig))
            {
                autoExport = exportConfig.TryGetProperty("AutoSell", out var autoSell) && autoSell.GetBoolean();
            }
            snapshot = new VislandSnapshot(isRouteRunning.InvokeFunc(), autoExport, GetFlightUnlocked(), routes);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not read Visland routes: {ex.GetBaseException().Message}";
            return false;
        }
    }

    public bool TryStartRoute(RouteSnapshot route, int startIndex, bool flightUnlocked, out string error)
    {
        try
        {
            startRoute.InvokeAction(SerializeForIpc(route, startIndex, flightUnlocked), true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    public bool IsNavmeshReady
    {
        get { try { return navmeshReady.HasFunction && navmeshReady.InvokeFunc(); } catch { return false; } }
    }
    public bool IsNavigationBusy
    {
        get
        {
            try
            {
                return (navmeshPathfindInProgress.HasFunction && navmeshPathfindInProgress.InvokeFunc())
                       || (navmeshPathRunning.HasFunction && navmeshPathRunning.InvokeFunc());
            }
            catch { return false; }
        }
    }

    public bool TryNavigateTo(Vector3 destination, bool fly, out string error)
    {
        try
        {
            if (!IsNavmeshReady || !navmeshMoveTo.HasFunction)
                throw new InvalidOperationException("vnavmesh is not installed or ready.");
            if (!navmeshMoveTo.InvokeFunc(destination, fly))
                throw new InvalidOperationException("vnavmesh rejected the pathfinding request.");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    public void StopNavigation()
    {
        try { if (navmeshStop.HasAction) navmeshStop.InvokeAction(); }
        catch { }
    }
    public bool IsLifestreamAvailable => lifestreamExecute.HasAction && lifestreamBusy.HasFunction;
    public bool IsLifestreamBusy
    {
        get { try { return IsLifestreamAvailable && lifestreamBusy.InvokeFunc(); } catch { return false; } }
    }

    public bool TryTravelToIsland(out string error)
    {
        try
        {
            if (!IsLifestreamAvailable) throw new InvalidOperationException("Lifestream is not installed or loaded.");
            if (IsLifestreamBusy) throw new InvalidOperationException("Lifestream is currently busy.");
            lifestreamExecute.InvokeAction("island");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    public void AbortLifestream()
    {
        try { if (lifestreamAbort.HasAction) lifestreamAbort.InvokeAction(); }
        catch { }
    }

    public RouteSnapshot CreateGeneratedRoute(IReadOnlyList<RouteNodeSnapshot> nodes, bool fly)
    {
        const string name = "Stock Manager Experimental";
        var items = nodes.SelectMany(x => x.ItemIds).GroupBy(x => x).Select(group =>
        {
            var resource = IslandResources.ById[group.Key];
            var (count, available) = GetItemState(group.Key);
            return new ItemSnapshot(group.Key, resource.Name, group.Count(), count, available);
        }).OrderBy(x => x.Name).ToList();
        var waypoints = nodes.Select((node, index) => new RouteWaypointSnapshot(
            node.Position,
            node.ZoneId,
            3,
            nodes.Count > 1 && Vector3.Distance(nodes[(index - 1 + nodes.Count) % nodes.Count].Position, node.Position) <= 18f
                ? RouteMovement.Normal
                : fly ? RouteMovement.MountFly : RouteMovement.MountNoFly,
            true,
            node.ObjectId,
            node.ObjectName,
            node.Position,
            1,
            true,
            false,
            0,
            0,
            Vector2.Zero,
            "")).ToList();
        return new RouteSnapshot(name, "Stock Manager", fly, 0, 0, items, nodes.ToList(), waypoints);
    }

    public RouteSnapshot CreateExportTripRoute()
    {
        var positions = new[]
        {
            new Vector3(-268f, 40f, 226f),
            new Vector3(-267.729f, 40.000008f, 223.35608f),
            new Vector3(-267.67017f, 41f, 220.24205f),
            new Vector3(-267.57275f, 41f, 219.37453f),
            new Vector3(-266.5052f, 41.499996f, 217.80667f),
            new Vector3(-266.4256f, 41f, 209.15497f),
        };
        var waypoints = positions.Select((position, index) => new RouteWaypointSnapshot(
            position,
            0,
            3,
            RouteMovement.Normal,
            true,
            index == positions.Length - 1 ? 1043464u : 0,
            "",
            Vector3.Zero,
            1,
            index == positions.Length - 1,
            false,
            0,
            0,
            Vector2.Zero,
            "")).ToList();
        return new RouteSnapshot("Stock Manager Export Trip", "Stock Manager", false, 0, 0, [], [], waypoints);
    }

    public void Stop()
    {
        try { if (stopRoute.HasAction) stopRoute.InvokeAction(); }
        catch { }
        StopNavigation();
    }

    public bool TryDisableBuiltInAutoExport(out string error)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "visland")
                           ?? throw new InvalidOperationException("Visland is not loaded.");
            var serviceType = assembly.GetType("visland.Service", true)!;
            var exportType = assembly.GetType("visland.Export.ExportConfig", true)!;
            var configuration = serviceType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                                ?? throw new MissingMemberException("visland.Service.Config");
            var getNode = configuration.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(x => x.Name == "Get" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0)
                .MakeGenericMethod(exportType);
            var exportConfig = getNode.Invoke(configuration, null) ?? throw new InvalidOperationException("Visland export configuration is unavailable.");
            var autoSell = exportType.GetField("AutoSell", BindingFlags.Public | BindingFlags.Instance)
                           ?? throw new MissingFieldException(exportType.FullName, "AutoSell");
            if ((bool)(autoSell.GetValue(exportConfig) ?? false))
            {
                autoSell.SetValue(exportConfig, false);
                exportType.GetMethod("NotifyModified", BindingFlags.Public | BindingFlags.Instance)?.Invoke(exportConfig, null);
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    internal static string SerializeForIpc(RouteSnapshot route, int startIndex, bool flightUnlocked = true)
    {
        if (route.Waypoints.Count == 0)
            throw new InvalidOperationException($"Route '{route.Name}' has no waypoints.");
        startIndex = Math.Clamp(startIndex, 0, route.Waypoints.Count - 1);
        var ordered = route.Waypoints.Skip(startIndex).Concat(route.Waypoints.Take(startIndex));
        var payload = new
        {
            route.Name,
            route.Group,
            route.Food,
            route.TargetGatherItem,
            Waypoints = ordered.Select(waypoint => new
            {
                Position = new { waypoint.Position.X, waypoint.Position.Y, waypoint.Position.Z },
                ZoneID = waypoint.ZoneId,
                waypoint.Radius,
                Movement = (int)GetEffectiveMovement(route, waypoint, flightUnlocked),
                waypoint.Pathfind,
                InteractWithOID = waypoint.ObjectId,
                InteractWithName = waypoint.ObjectName,
                InteractWithPosition = new
                {
                    X = waypoint.InteractionPosition.X,
                    Y = waypoint.InteractionPosition.Y,
                    Z = waypoint.InteractionPosition.Z,
                },
                showInteractions = waypoint.ShowInteractions,
                waypoint.Interaction,
                showWaits = waypoint.ShowWaits,
                waypoint.WaitForCondition,
                waypoint.WaitTimeMs,
                WaitTimeET = new { X = waypoint.WaitTimeEt.X, Y = waypoint.WaitTimeEt.Y },
                waypoint.RouteName,
            }).ToArray(),
        };
        return Compress(JsonSerializer.Serialize(payload));
    }

    private static RouteMovement GetEffectiveMovement(RouteSnapshot route, RouteWaypointSnapshot waypoint, bool flightUnlocked)
    {
        // Mixed land/underwater routes sometimes use MountFly for their surface transfer. They remain usable before
        // Island flight unlock; make those surface legs ground-mounted while retaining 3D diving movement at runtime.
        return !flightUnlocked && !route.RequiresFlying && waypoint.Movement == RouteMovement.MountFly
            ? RouteMovement.MountNoFly
            : waypoint.Movement;
    }

    private static RouteMovement ParseMovement(string? value) => value?.ToLowerInvariant() switch
    {
        "mountfly" => RouteMovement.MountFly,
        "mountnofly" => RouteMovement.MountNoFly,
        _ => RouteMovement.Normal,
    };

    private static int ParseInteraction(string? value) => value?.ToLowerInvariant() switch
    {
        "none" => 0,
        "startroute" => 9,
        "nodescan" => 12,
        _ => 1,
    };

    private static int ParseCondition(string? value) =>
        Enum.TryParse<ConditionFlag>(value, true, out var condition) ? (int)condition : 0;

    private static Vector2 ReadVector2(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            return Vector2.Zero;
        return new Vector2(
            value.TryGetProperty("X", out var x) ? x.GetSingle() : 0,
            value.TryGetProperty("Y", out var y) ? y.GetSingle() : 0);
    }

    private static string Compress(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(bytes);
        return Convert.ToBase64String(output.ToArray());
    }

    private static unsafe (int Count, bool Available) GetItemState(int itemId)
    {
        var inventory = InventoryManager.Instance();
        var manager = MJIManager.Instance();
        var count = inventory == null ? 0 : (int)inventory->GetInventoryItemCount((uint)itemId);
        return (count, manager == null || !manager->IsItemLocked((uint)itemId));
    }

    private static unsafe bool? GetFlightUnlocked()
    {
        var manager = MJIManager.Instance();
        if (manager == null || !manager->IsPlayerInSanctuary) return null;
        var playerState = PlayerState.Instance();
        return manager->IslandState.CurrentRank >= 10 && playerState != null && playerState->CanFly;
    }
}

internal static class IslandResources
{
    private static ItemResource R(int id, string name) => new(id, name);

    public static readonly Dictionary<string, ItemResource[]> ByNode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agave plant"] = [R(37558, "Islewort"), R(37569, "Hemp")],
        ["bluish rock"] = [R(37554, "Stone"), R(37564, "Copper Ore"), R(39891, "Mythril Ore")],
        ["composite rock"] = [R(37554, "Stone"), R(39887, "Coal"), R(39888, "Shale")],
        ["coral formation"] = [R(37557, "Coral"), R(37577, "Jellyfish")],
        ["cotton plant"] = [R(37558, "Islewort"), R(37568, "Cotton Boll")],
        ["crystal-banded rock"] = [R(37554, "Stone"), R(37566, "Rock Salt")],
        ["glowing fungus"] = [R(39889, "Glimshroom")],
        ["island apple tree"] = [R(37562, "Vine"), R(37552, "Apple"), R(39226, "Beehive Chip")],
        ["island crystal cluster"] = [R(41633, "Hawk's Eye Sand"), R(41634, "Crystal Formation")],
        ["large shell"] = [R(37555, "Clam"), R(37575, "Islefish")],
        ["mahogany tree"] = [R(37563, "Sap"), R(37560, "Log"), R(39227, "Wood Opal")],
        ["mound of dirt"] = [R(37559, "Sand"), R(37570, "Clay")],
        ["multicolored isleblooms"] = [R(39228, "Multicolored Isleblooms")],
        ["palm tree"] = [R(37551, "Palm Leaf"), R(37561, "Palm Log"), R(39225, "Coconut")],
        ["quartz formation"] = [R(37554, "Stone"), R(37573, "Quartz")],
        ["rough black rock"] = [R(37554, "Stone"), R(37572, "Iron Ore"), R(41630, "Durium Sand")],
        ["seaweed tangle"] = [R(37556, "Laver"), R(37576, "Squid")],
        ["smooth white rock"] = [R(37554, "Stone"), R(37565, "Limestone"), R(39890, "Marble")],
        ["speckled rock"] = [R(37554, "Stone"), R(37574, "Leucogranite")],
        ["stalagmite"] = [R(37554, "Stone"), R(39892, "Effervescent Water"), R(39893, "Spectrine")],
        ["submerged sand"] = [R(37559, "Sand"), R(37571, "Tinsand")],
        ["sugarcane"] = [R(37562, "Vine"), R(37567, "Sugarcane")],
        ["tualong tree"] = [R(37553, "Branch"), R(37560, "Log"), R(39224, "Resin")],
        ["yellowish rock"] = [R(37554, "Stone"), R(41631, "Yellow Copper Ore"), R(41632, "Gold Ore")],
    };

    public static readonly Dictionary<int, ItemResource> ById = ByNode.Values.SelectMany(x => x).GroupBy(x => x.Id)
        .ToDictionary(x => x.Key, x => x.First());
}

internal sealed record ItemResource(int Id, string Name);
