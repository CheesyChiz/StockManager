using Dalamud.Configuration;

namespace StockManager;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool Enabled { get; set; }
    public int BulkTarget { get; set; } = 999;
    public RouteMovementMode MovementMode { get; set; } = RouteMovementMode.GroundOnly;
    public CompletionAction CompletionAction { get; set; } = CompletionAction.Stop;
    public int BulkSellLimit { get; set; } = 800;
    public int ExportBatch { get; set; } = 100;
    public Dictionary<int, int> Targets { get; set; } = new();
    public Dictionary<int, int> SellLimits { get; set; } = new();
    public HashSet<string> ExcludedRoutes { get; set; } = new();
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
