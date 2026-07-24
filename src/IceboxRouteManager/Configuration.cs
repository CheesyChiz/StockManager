using Dalamud.Configuration;

namespace IceboxRouteManager;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public int MaxLoopsPerRun { get; set; } = 20;
    public Dictionary<int, int> Targets { get; set; } = new();
    public HashSet<string> ExcludedRoutes { get; set; } = new();
}
