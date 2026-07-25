using Dalamud.Configuration;
using System.Text.Json.Serialization;

namespace StockManager;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;
    public bool Enabled { get; set; }
    public int BulkTarget { get; set; } = 999;
    public RouteMovementMode MovementMode { get; set; } = RouteMovementMode.GroundOnly;
    public CompletionAction CompletionAction { get; set; } = CompletionAction.Stop;
    public int ExportBatch { get; set; } = 100;
    public Dictionary<int, int> Targets { get; set; } = new();
    public HashSet<string> ExcludedRoutes { get; set; } = new();
    [JsonPropertyName("BulkSellLimit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyBulkSellLimit { get; set; }
    [JsonPropertyName("SellLimits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<int, int>? LegacySellLimits { get; set; }
}

public enum CompletionAction
{
    Stop,
    FarmAndExport,
}

public enum RouteMovementMode
{
    GroundOnly,
    GroundAndFlying,
}
