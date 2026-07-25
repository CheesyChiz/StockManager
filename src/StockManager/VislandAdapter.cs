using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace StockManager;

internal sealed class VislandAdapter
{
    private readonly ICallGateSubscriber<bool> isRouteRunning;
    private readonly ICallGateSubscriber<string, bool, object> startRoute;
    private readonly ICallGateSubscriber<object> stopRoute;
    private readonly string configPath;

    public VislandAdapter(IDalamudPluginInterface pluginInterface)
    {
        isRouteRunning = pluginInterface.GetIpcSubscriber<bool>("visland.IsRouteRunning");
        startRoute = pluginInterface.GetIpcSubscriber<string, bool, object>("visland.StartRoute");
        stopRoute = pluginInterface.GetIpcSubscriber<object>("visland.StopRoute");
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
                var requiresFlying = false;
                if (route.TryGetProperty("Waypoints", out var waypoints))
                {
                    foreach (var waypoint in waypoints.EnumerateArray())
                    {
                        if (waypoint.TryGetProperty("Movement", out var movement)
                            && movement.GetString()?.Equals("MountFly", StringComparison.OrdinalIgnoreCase) == true)
                            requiresFlying = true;

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
                    }
                }

                if (items.Count > 0)
                    routes.Add(new RouteSnapshot(name, group, requiresFlying, Compress(route.GetRawText()), items.Values.OrderBy(x => x.Name).ToList()));
            }

            var autoExport = false;
            var exportLimit = 999;
            if (payload.TryGetProperty("visland.Export.ExportConfig", out var exportConfig))
            {
                autoExport = exportConfig.TryGetProperty("AutoSell", out var autoSell) && autoSell.GetBoolean();
                exportLimit = exportConfig.TryGetProperty("NormalLimit", out var normalLimit) ? normalLimit.GetInt32() : 999;
            }
            snapshot = new VislandSnapshot(isRouteRunning.InvokeFunc(), autoExport, exportLimit, routes);
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

    public bool TryStartExportTrip(out string error)
    {
        const string routeJson = "{\"Name\":\"Stock Manager Export Trip\",\"Group\":\"Stock Manager\",\"Food\":0,\"TargetGatherItem\":0,\"Waypoints\":[{\"X\":-267.729,\"Y\":40.000008,\"Z\":223.35608,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-267.67017,\"Y\":41.0,\"Z\":220.24205,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-267.57275,\"Y\":41.0,\"Z\":219.37453,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-266.5052,\"Y\":41.499996,\"Z\":217.80667,\"Movement\":\"Normal\",\"Pathfind\":true},{\"X\":-266.4256,\"Y\":41.0,\"Z\":209.15497,\"Movement\":\"Normal\",\"Pathfind\":true,\"InteractWithOID\":1043464,\"Interaction\":\"Standard\"}]}";
        try
        {
            startRoute.InvokeAction(Compress(routeJson), true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    public void Stop()
    {
        try { stopRoute.InvokeAction(); }
        catch { }
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
}

internal sealed record ItemResource(int Id, string Name);
