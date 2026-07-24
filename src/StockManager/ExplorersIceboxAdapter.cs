using System.Collections;
using System.Reflection;

namespace StockManager;

internal sealed class ExplorersIceboxAdapter : IDisposable
{
    private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private Assembly? assembly;
    private Type? pluginType;
    private Type? schedulerType;
    private Type? islandHelperType;
    private Type? itemDataType;
    private Type? playerHelperType;
    private bool startedByManager;
    private object? savedConfig;
    private bool savedRunMaxLoops;
    private bool savedRunMultiple;
    private readonly Dictionary<string, int> savedAmounts = new();

    public bool TryGetSnapshot(out IceboxSnapshot? snapshot, out string error)
    {
        snapshot = null;
        if (!TryBind(out error))
            return false;

        try
        {
            var state = GetStaticMember(schedulerType!, "State")?.ToString() ?? "Unknown";
            var running = !state.Equals("Idle", StringComparison.OrdinalIgnoreCase);
            if (!running && startedByManager)
                RestoreConfig();

            var routes = BuildRoutes();
            snapshot = new IceboxSnapshot(1, running, state, routes);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"ExplorersIcebox {assembly?.GetName().Version} is incompatible: {ex.GetBaseException().Message}";
            return false;
        }
    }

    public bool TryStartRoute(RouteSnapshot routeSnapshot, int loops, out string error)
    {
        if (!TryBind(out error))
            return false;

        try
        {
            var state = GetStaticMember(schedulerType!, "State")?.ToString();
            if (!string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase))
            {
                error = $"scheduler state is {state}";
                return false;
            }

            var routes = GetRoutesDictionary();
            if (!routes.Contains(routeSnapshot.Name))
            {
                error = "route no longer exists";
                return false;
            }

            SaveAndApplyConfig(routeSnapshot, loops);
            var currentRouteField = islandHelperType!.GetField("CurrentRoute", AllStatic)
                                    ?? throw new MissingFieldException(islandHelperType.FullName, "CurrentRoute");
            var currentRoute = Activator.CreateInstance(currentRouteField.FieldType, routeSnapshot.Name, routes[routeSnapshot.Name]);
            currentRouteField.SetValue(null, currentRoute);
            SetStaticField(islandHelperType, "GoalLoopAmount", loops);
            SetStaticField(islandHelperType, "MaxRouteLoops", 999);

            var enable = schedulerType!.GetMethod("EnablePlugin", AllStatic)
                         ?? throw new MissingMethodException(schedulerType.FullName, "EnablePlugin");
            var accepted = enable.Invoke(null, null) as bool? ?? false;
            if (!accepted)
            {
                RestoreConfig();
                error = "EnablePlugin returned false";
                return false;
            }

            startedByManager = true;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            RestoreConfig();
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (TryBind(out _))
                schedulerType?.GetMethod("DisablePlugin", AllStatic)?.Invoke(null, null);
        }
        finally
        {
            RestoreConfig();
        }
    }

    public void Dispose() => RestoreConfig();

    private bool TryBind(out string error)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => candidate.GetName().Name == "ExplorersIcebox");
        if (loaded == null)
        {
            error = "ExplorersIcebox is not loaded. Install and enable it from Ice's repository.";
            return false;
        }

        if (ReferenceEquals(loaded, assembly))
        {
            error = string.Empty;
            return true;
        }

        assembly = loaded;
        pluginType = RequireType("ExplorersIcebox.ExplorersIcebox");
        schedulerType = RequireType("ExplorersIcebox.Scheduler.SchedulerMain");
        islandHelperType = RequireType("ExplorersIcebox.Util.IslandHelper");
        itemDataType = RequireType("ExplorersIcebox.Util.ItemData");
        playerHelperType = RequireType("ExplorersIcebox.Util.PlayerHelper");
        error = string.Empty;
        return true;
    }

    private Type RequireType(string name) => assembly?.GetType(name, throwOnError: true)
                                             ?? throw new TypeLoadException(name);

    private List<RouteSnapshot> BuildRoutes()
    {
        var result = new List<RouteSnapshot>();
        foreach (DictionaryEntry route in GetRoutesDictionary())
        {
            var items = BuildRouteItems(route.Value!);
            result.Add(new RouteSnapshot((string)route.Key, items.Values.OrderBy(item => item.Name).ToList()));
        }
        return result;
    }

    private IDictionary GetRoutesDictionary()
    {
        var embedRoutes = GetStaticMember(pluginType!, "EmbedRoutes")
                          ?? throw new MissingMemberException(pluginType!.FullName, "EmbedRoutes");
        return GetInstanceMember(embedRoutes, "Routes") as IDictionary
               ?? throw new InvalidCastException("EmbedRoutes.Routes is not a dictionary");
    }

    private Dictionary<int, ItemSnapshot> BuildRouteItems(object route)
    {
        var result = new Dictionary<int, ItemSnapshot>();
        var waypoints = GetInstanceMember(route, "RouteWaypoints") as IEnumerable
                        ?? throw new InvalidCastException("RouteWaypoints is not enumerable");
        var nodes = GetStaticMember(itemDataType!, "IslandNodeInfo") as IEnumerable
                    ?? throw new InvalidCastException("IslandNodeInfo is not enumerable");
        var itemInfo = GetStaticMember(itemDataType!, "IslandItems") as IDictionary
                       ?? throw new InvalidCastException("IslandItems is not a dictionary");

        foreach (var waypoint in waypoints)
        {
            var targetId = Convert.ToUInt64(GetInstanceMember(waypoint!, "TargetId"));
            if (targetId == 0)
                continue;

            object? matchedNode = null;
            foreach (var node in nodes)
            {
                var nodeIds = GetInstanceMember(node!, "Nodes") as IEnumerable;
                if (nodeIds != null && nodeIds.Cast<object>().Any(id => Convert.ToUInt64(id) == targetId))
                {
                    matchedNode = node;
                    break;
                }
            }
            if (matchedNode == null)
                continue;

            var itemIds = GetInstanceMember(matchedNode, "ItemIds") as IEnumerable;
            if (itemIds == null)
                continue;
            foreach (var rawId in itemIds)
            {
                var id = Convert.ToInt32(rawId);
                var info = itemInfo[id];
                if (info == null)
                    continue;
                var name = Convert.ToString(GetInstanceMember(info, "ItemName")) ?? id.ToString();
                if (result.TryGetValue(id, out var existing))
                    result[id] = existing with { PerLoop = existing.PerLoop + 1 };
                else
                    result[id] = new ItemSnapshot(id, name, 1, GetItemCount(id));
            }
        }
        return result;
    }

    private int GetItemCount(int itemId)
    {
        var method = playerHelperType!.GetMethod("GetItemCount", AllStatic)
                     ?? throw new MissingMethodException(playerHelperType.FullName, "GetItemCount");
        object?[] args = [itemId, 0];
        method.Invoke(null, args);
        return Convert.ToInt32(args[1]);
    }

    private void SaveAndApplyConfig(RouteSnapshot route, int loops)
    {
        RestoreConfig();
        savedConfig = GetStaticMember(pluginType!, "C")
                      ?? throw new MissingMemberException(pluginType!.FullName, "C");
        savedRunMaxLoops = Convert.ToBoolean(GetInstanceMember(savedConfig, "RunMaxLoops"));
        savedRunMultiple = Convert.ToBoolean(GetInstanceMember(savedConfig, "RunMultiple"));
        SetInstanceMember(savedConfig, "RunMaxLoops", false);
        SetInstanceMember(savedConfig, "RunMultiple", false);

        var amounts = GetInstanceMember(savedConfig, "ItemGatherAmount") as IDictionary
                      ?? throw new InvalidCastException("ItemGatherAmount is not a dictionary");
        foreach (var item in route.Items)
        {
            savedAmounts[item.Name] = amounts.Contains(item.Name) ? Convert.ToInt32(amounts[item.Name]) : 0;
            amounts[item.Name] = Math.Clamp(loops * item.PerLoop, 1, 999);
        }
    }

    private void RestoreConfig()
    {
        if (savedConfig == null)
            return;
        try
        {
            SetInstanceMember(savedConfig, "RunMaxLoops", savedRunMaxLoops);
            SetInstanceMember(savedConfig, "RunMultiple", savedRunMultiple);
            if (GetInstanceMember(savedConfig, "ItemGatherAmount") is IDictionary amounts)
            {
                foreach (var entry in savedAmounts)
                    amounts[entry.Key] = entry.Value;
            }
        }
        finally
        {
            savedAmounts.Clear();
            savedConfig = null;
            startedByManager = false;
        }
    }

    private static object? GetStaticMember(Type type, string name) =>
        type.GetProperty(name, AllStatic)?.GetValue(null) ?? type.GetField(name, AllStatic)?.GetValue(null);

    private static object? GetInstanceMember(object instance, string name) =>
        instance.GetType().GetProperty(name, AllInstance)?.GetValue(instance)
        ?? instance.GetType().GetField(name, AllInstance)?.GetValue(instance);

    private static void SetInstanceMember(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, AllInstance);
        if (property != null)
            property.SetValue(instance, value);
        else
            throw new MissingMemberException(instance.GetType().FullName, name);
    }

    private static void SetStaticField(Type type, string name, object value)
    {
        var field = type.GetField(name, AllStatic) ?? throw new MissingFieldException(type.FullName, name);
        field.SetValue(null, value);
    }
}
