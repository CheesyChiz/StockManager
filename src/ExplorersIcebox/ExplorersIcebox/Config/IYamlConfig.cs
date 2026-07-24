namespace ExplorersIcebox.Config;

public interface IYamlConfig
{
    abstract static string ConfigPath { get; }
    void Save();
}
