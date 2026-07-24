using ExplorersIcebox.Util.PathCreation;
using System.Collections.Generic;
using System.IO;
namespace ExplorersIcebox.Config;

public class GatherRoutes : IYamlConfig
{
    public Dictionary<string, RouteClass.RouteUtil> Routes { get; set; } = new();

    public Dictionary<string, RouteClass.InteractionUtil> BaseRoutes { get; set; } = new();
    public static string ConfigPath => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "GatherRoutesConfig.yaml");
    public void Save() => YamlConfig.Save(this, ConfigPath);
}
