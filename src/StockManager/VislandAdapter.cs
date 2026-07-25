using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
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
                var requiresFlying = false;
                RouteStartSnapshot? start = null;
                if (route.TryGetProperty("Waypoints", out var waypoints))
                {
                    foreach (var waypoint in waypoints.EnumerateArray())
                    {
                        var movementName = waypoint.TryGetProperty("Movement", out var movement) ? movement.GetString() : null;
                        if (movementName?.Equals("MountFly", StringComparison.OrdinalIgnoreCase) == true)
                            requiresFlying = true;

                        var position = new Vector3(
                            waypoint.TryGetProperty("X", out var x) ? x.GetSingle() : 0,
                            waypoint.TryGetProperty("Y", out var y) ? y.GetSingle() : 0,
                            waypoint.TryGetProperty("Z", out var z) ? z.GetSingle() : 0);
                        start ??= new RouteStartSnapshot(
                            position,
                            waypoint.TryGetProperty("Radius", out var radius) ? Math.Max(1, radius.GetSingle()) : 3,
                            movementName?.Equals("MountFly", StringComparison.OrdinalIgnoreCase) == true);

                        if (!waypoint.TryGetProperty("InteractWithName", out var nodeElement))
                            continue;
                        var nodeName = nodeElement.GetString();
                        if (string.IsNullOrWhiteSpace(nodeName) || !IslandResources.ByNode.TryGetValue(nodeName.Trim(), out var nodeItems))
                            continue;
                        foreach (var resource in nodeItems)
                        {
                            var (count, available) = GetItemState(resource.Id);
                            items[resource.Id] = items.TryGetValue(resource.Id, out var existing)
                                ? existing with { PerLoop = existing.PerLoop + 1, CurrentCount = count, IsAvailable = available }
                                : new ItemSnapshot(resource.Id, resource.Name, 1, count, available);
                        }
                        var objectId = waypoint.TryGetProperty("InteractWithOID", out var oid) ? oid.GetUInt32() : 0;
                        var zoneId = waypoint.TryGetProperty("ZoneID", out var zone) ? zone.GetUInt32() : 0;
                        if (objectId != 0)
                            routeNodes.Add(new RouteNodeSnapshot(position, zoneId, objectId, nodeName.Trim(), nodeItems.Select(x => x.Id).ToArray()));
                    }
                }

                if (items.Count > 0 && start != null)
                    routes.Add(new RouteSnapshot(name, group, requiresFlying, Compress(route.GetRawText()), items.Values.OrderBy(x => x.Name).ToList(), routeNodes, start));
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

    public bool TryStartRoute(RouteSnapshot route, out string error)
    {
        try
        {
            startRoute.InvokeAction(route.SerializedRoute, true);
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
    public bool TryNavigateToStart(RouteSnapshot route, Vector3 currentPosition, out string error)
    {
        try
        {
            if (!IsNavmeshReady)
                throw new InvalidOperationException("vnavmesh is not installed or ready.");
            var approach = new
            {
                Name = $"Stock Manager Approach: {route.Name}",
                Group = "Stock Manager",
                Food = 0,
                TargetGatherItem = 0,
                Waypoints = new[]
                {
                    new
                    {
                        X = currentPosition.X,
                        Y = currentPosition.Y,
                        Z = currentPosition.Z,
                        Radius = 3f,
                        Movement = "Normal",
                        Pathfind = false
                    },
                    new
                    {
                        X = route.Start.Position.X,
                        Y = route.Start.Position.Y,
                        Z = route.Start.Position.Z,
                        Radius = route.Start.Radius,
                        Movement = route.Start.Fly ? "MountFly" : "MountNoFly",
                        Pathfind = true
                    }
                }
            };
            startRoute.InvokeAction(Compress(JsonSerializer.Serialize(approach)), true);
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
        try { if (stopRoute.HasAction) stopRoute.InvokeAction(); }
        catch { }
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
        var route = new
        {
            Name = name,
            Group = "Stock Manager",
            Food = 0,
            TargetGatherItem = 0,
            Waypoints = nodes.Select(node => new
            {
                X = node.Position.X,
                Y = node.Position.Y,
                Z = node.Position.Z,
                ZoneID = node.ZoneId,
                Radius = 3,
                Movement = fly ? "MountFly" : "MountNoFly",
                Pathfind = true,
                InteractWithOID = node.ObjectId,
                InteractWithName = node.ObjectName,
                iX = node.Position.X,
                iY = node.Position.Y,
                iZ = node.Position.Z,
                Interaction = "Standard"
            }).ToArray()
        };
        var items = nodes.SelectMany(x => x.ItemIds).GroupBy(x => x).Select(group =>
        {
            var resource = IslandResources.ById[group.Key];
            var (count, available) = GetItemState(group.Key);
            return new ItemSnapshot(group.Key, resource.Name, group.Count(), count, available);
        }).OrderBy(x => x.Name).ToList();
        return new RouteSnapshot(name, "Stock Manager", fly, Compress(JsonSerializer.Serialize(route)), items, nodes.ToList(),
            new RouteStartSnapshot(nodes[0].Position, 3, fly));
    }

    public RouteSnapshot CreateExportTripRoute()
    {
        const string routeJson = "{\"Name\":\"Stock Manager Export Trip\",\"Group\":\"Stock Manager\",\"Food\":0,\"TargetGatherItem\":0,\"Waypoints\":[{\"X\":-267.729,\"Y\":40.000008,\"Z\":223.35608,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-267.67017,\"Y\":41.0,\"Z\":220.24205,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-267.57275,\"Y\":41.0,\"Z\":219.37453,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-266.5052,\"Y\":41.499996,\"Z\":217.80667,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-266.4256,\"Y\":41.0,\"Z\":209.15497,\"Movement\":\"Normal\",\"Pathfind\":true,\"InteractWithOID\":1043464,\"Interaction\":\"Standard\"}]}";
        return new RouteSnapshot("Stock Manager Export Trip", "Stock Manager", false, Compress(routeJson), [], [],
            new RouteStartSnapshot(new Vector3(-267.729f, 40.000008f, 223.35608f), 3, false));
    }

    public void Stop()
    {
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
        return playerState != null && playerState->CanFly;
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
