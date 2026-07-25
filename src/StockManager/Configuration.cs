using Dalamud.Configuration;
using Newtonsoft.Json;

namespace StockManager;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;
    public bool Enabled { get; set; }
    public bool AutoTravelToIsland { get; set; }
    public uint MountId { get; set; }
    public ResourcePriority ResourcePriority { get; set; } = ResourcePriority.FastestRoute;
    public bool SkipStuckRoutes { get; set; } = true;
    public int StuckTimeoutSeconds { get; set; } = 15;
    public int BulkTarget { get; set; } = 999;
    public CompletionAction CompletionAction { get; set; } = CompletionAction.Stop;
    public int ExportBatch { get; set; } = 100;
    public Dictionary<int, int> Targets { get; set; } = new();
    public HashSet<int> EnabledItems { get; set; } = new();
    public List<UserRouteConfiguration> UserRoutes { get; set; } = new();
    [JsonProperty("BulkSellLimit", NullValueHandling = NullValueHandling.Ignore)]
    public int? LegacyBulkSellLimit { get; set; }
    [JsonProperty("SellLimits", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<int, int>? LegacySellLimits { get; set; }
    [JsonProperty("ExcludedRoutes", NullValueHandling = NullValueHandling.Ignore)]
    public HashSet<string>? LegacyExcludedRoutes { get; set; }
    [JsonProperty("MovementMode", NullValueHandling = NullValueHandling.Ignore)]
    public int? LegacyMovementMode { get; set; }
}

public sealed class UserRouteConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom route";
    public string SourceRouteName { get; set; } = "";
    public bool UseForAutomation { get; set; }
    public List<UserWaypointConfiguration> Waypoints { get; set; } = new();
}

public sealed class UserWaypointConfiguration
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public uint ZoneId { get; set; }
    public float Radius { get; set; } = 3;
    public RouteMovement Movement { get; set; }
    public bool Pathfind { get; set; } = true;
    public uint ObjectId { get; set; }
    public string ObjectName { get; set; } = "";
    public float InteractionX { get; set; }
    public float InteractionY { get; set; }
    public float InteractionZ { get; set; }
    public int Interaction { get; set; } = 1;
    public bool ShowInteractions { get; set; }
    public bool ShowWaits { get; set; }
    public int WaitForCondition { get; set; }
    public int WaitTimeMs { get; set; }
    public float WaitTimeEtX { get; set; }
    public float WaitTimeEtY { get; set; }
    public string RouteName { get; set; } = "";
}

public enum CompletionAction
{
    Stop,
    FarmAndExport,
}

public enum ResourcePriority
{
    RelativeDeficit,
    LowestStock,
    HighestStock,
    FastestRoute,
}
