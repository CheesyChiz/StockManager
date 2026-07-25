using Dalamud.Configuration;
using Newtonsoft.Json;

namespace StockManager;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;
    public bool Enabled { get; set; }
    public bool AutoTravelToIsland { get; set; }
    public int BulkTarget { get; set; } = 999;
    public CompletionAction CompletionAction { get; set; } = CompletionAction.Stop;
    public int ExportBatch { get; set; } = 100;
    public Dictionary<int, int> Targets { get; set; } = new();
    public HashSet<int> EnabledItems { get; set; } = new();
    [JsonProperty("BulkSellLimit", NullValueHandling = NullValueHandling.Ignore)]
    public int? LegacyBulkSellLimit { get; set; }
    [JsonProperty("SellLimits", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<int, int>? LegacySellLimits { get; set; }
    [JsonProperty("ExcludedRoutes", NullValueHandling = NullValueHandling.Ignore)]
    public HashSet<string>? LegacyExcludedRoutes { get; set; }
    [JsonProperty("MovementMode", NullValueHandling = NullValueHandling.Ignore)]
    public int? LegacyMovementMode { get; set; }
}

public enum CompletionAction
{
    Stop,
    FarmAndExport,
}
