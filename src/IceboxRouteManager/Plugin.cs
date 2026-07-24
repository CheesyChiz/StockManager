using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Numerics;
using System.Text.Json;

namespace IceboxRouteManager;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/iceboxmanager";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Services services;
    private readonly Configuration config;
    private readonly ICallGateSubscriber<string> snapshotSubscriber;
    private readonly ICallGateSubscriber<string, int, bool> startSubscriber;
    private readonly ICallGateSubscriber<object?> stopSubscriber;

    private IceboxSnapshot? snapshot;
    private DateTime nextPoll = DateTime.MinValue;
    private DateTime nextStartAttempt = DateTime.MinValue;
    private bool windowOpen;
    private string status = "Waiting for ExplorersIcebox...";
    private string? activeRoute;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        services = pluginInterface.Create<Services>()
                   ?? throw new InvalidOperationException("Dalamud services are unavailable.");
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        snapshotSubscriber = pluginInterface.GetIpcSubscriber<string>("ExplorersIcebox.RouteManager.Snapshot");
        startSubscriber = pluginInterface.GetIpcSubscriber<string, int, bool>("ExplorersIcebox.RouteManager.StartRoute");
        stopSubscriber = pluginInterface.GetIpcSubscriber<object?>("ExplorersIcebox.RouteManager.Stop");

        services.Commands.AddHandler(Command, new CommandInfo((_, _) => windowOpen = true)
        {
            HelpMessage = "Open Icebox Route Manager"
        });
        services.Framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public string Name => "Icebox Route Manager";

    public void Dispose()
    {
        config.Enabled = false;
        services.Framework.Update -= OnUpdate;
        services.Commands.RemoveHandler(Command);
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
    }

    private void OpenMainUi() => windowOpen = true;

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextPoll)
            return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);

        try
        {
            var json = snapshotSubscriber.InvokeFunc();
            snapshot = JsonSerializer.Deserialize<IceboxSnapshot>(json);
            if (snapshot == null || snapshot.ApiVersion != 1)
            {
                status = "Incompatible ExplorersIcebox IPC version.";
                return;
            }

            InitializeDefaults(snapshot);
            if (!config.Enabled)
            {
                status = "Stopped";
                return;
            }

            if (snapshot.IsRunning)
            {
                status = activeRoute == null
                    ? $"ExplorersIcebox is running ({snapshot.State})"
                    : $"Running: {activeRoute} ({snapshot.State})";
                return;
            }

            if (DateTime.UtcNow < nextStartAttempt)
                return;

            var choice = SelectNextRoute(snapshot);
            if (choice == null)
            {
                config.Enabled = false;
                Save();
                activeRoute = null;
                status = "All configured targets have been reached.";
                return;
            }

            if (startSubscriber.InvokeFunc(choice.Value.Route.Name, choice.Value.Loops))
            {
                activeRoute = choice.Value.Route.Name;
                status = $"Starting {activeRoute}, {choice.Value.Loops} loop(s) for {choice.Value.Item.Name}.";
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            }
            else
            {
                status = "ExplorersIcebox rejected start; retrying in 5 seconds.";
                nextStartAttempt = DateTime.UtcNow.AddSeconds(5);
            }
        }
        catch (Exception ex)
        {
            status = $"IPC unavailable: {ex.Message}";
            snapshot = null;
        }
    }

    private (RouteSnapshot Route, ItemSnapshot Item, int Loops)? SelectNextRoute(IceboxSnapshot data)
    {
        var items = data.Routes
            .SelectMany(route => route.Items)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Where(item => config.Targets.TryGetValue(item.Id, out var target) && target > item.CurrentCount)
            .OrderBy(item => (double)item.CurrentCount / config.Targets[item.Id])
            .ThenBy(item => item.CurrentCount)
            .ToList();

        foreach (var item in items)
        {
            var route = data.Routes
                .Where(candidate => !config.ExcludedRoutes.Contains(candidate.Name))
                .Select(candidate => new
                {
                    Route = candidate,
                    Item = candidate.Items.FirstOrDefault(x => x.Id == item.Id)
                })
                .Where(candidate => candidate.Item != null && candidate.Item.PerLoop > 0)
                .OrderByDescending(candidate => candidate.Item!.PerLoop)
                .ThenByDescending(candidate => RouteUtility(candidate.Route))
                .FirstOrDefault();

            if (route?.Item == null)
                continue;

            var deficit = config.Targets[item.Id] - item.CurrentCount;
            var loops = Math.Clamp(
                (deficit + route.Item.PerLoop - 1) / route.Item.PerLoop,
                1,
                Math.Clamp(config.MaxLoopsPerRun, 1, 999));
            return (route.Route, item, loops);
        }

        return null;
    }

    private double RouteUtility(RouteSnapshot route) => route.Items.Sum(item =>
    {
        if (!config.Targets.TryGetValue(item.Id, out var target) || target <= 0 || item.CurrentCount >= target)
            return 0;
        return ((double)(target - item.CurrentCount) / target) * item.PerLoop;
    });

    private void InitializeDefaults(IceboxSnapshot data)
    {
        var changed = false;
        foreach (var item in data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First()))
        {
            if (config.Targets.TryAdd(item.Id, 999))
                changed = true;
        }
        if (changed)
            Save();
    }

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(760, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Icebox Route Manager###IceboxRouteManager", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped(status);
        ImGui.Separator();

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Automatic route switching", ref enabled))
        {
            config.Enabled = enabled;
            activeRoute = null;
            nextStartAttempt = DateTime.MinValue;
            Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Emergency stop"))
        {
            config.Enabled = false;
            try { stopSubscriber.InvokeAction(); } catch { }
            Save();
        }

        var maxLoops = config.MaxLoopsPerRun;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Maximum loops before reevaluation", ref maxLoops))
        {
            config.MaxLoopsPerRun = Math.Clamp(maxLoops, 1, 999);
            Save();
        }

        if (snapshot == null)
        {
            ImGui.TextWrapped("Install and enable the compatible ExplorersIcebox build included with this plugin.");
            ImGui.End();
            return;
        }

        if (ImGui.CollapsingHeader("Resource targets", ImGuiTreeNodeFlags.DefaultOpen))
            DrawTargets(snapshot);
        if (ImGui.CollapsingHeader("Enabled routes", ImGuiTreeNodeFlags.DefaultOpen))
            DrawRoutes(snapshot);

        ImGui.End();
    }

    private void DrawTargets(IceboxSnapshot data)
    {
        if (ImGui.Button("Set all to 999"))
        {
            foreach (var id in config.Targets.Keys.ToList())
                config.Targets[id] = 999;
            Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Disable all targets"))
        {
            foreach (var id in config.Targets.Keys.ToList())
                config.Targets[id] = 0;
            Save();
        }

        if (!ImGui.BeginTable("Targets", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 300)))
            return;
        ImGui.TableSetupColumn("Resource");
        ImGui.TableSetupColumn("Current");
        ImGui.TableSetupColumn("Target");
        ImGui.TableSetupColumn("Completion");
        ImGui.TableHeadersRow();

        foreach (var item in data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Name))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.CurrentCount.ToString());
            ImGui.TableNextColumn();
            var target = config.Targets[item.Id];
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt($"##target{item.Id}", ref target))
            {
                config.Targets[item.Id] = Math.Clamp(target, 0, 999);
                Save();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(target <= 0 ? "disabled" : $"{Math.Min(100, item.CurrentCount * 100 / target)}%");
        }
        ImGui.EndTable();
    }

    private void DrawRoutes(IceboxSnapshot data)
    {
        foreach (var route in data.Routes.OrderBy(x => x.Name))
        {
            var enabled = !config.ExcludedRoutes.Contains(route.Name);
            if (ImGui.Checkbox($"{route.Name}##route", ref enabled))
            {
                if (enabled)
                    config.ExcludedRoutes.Remove(route.Name);
                else
                    config.ExcludedRoutes.Add(route.Name);
                Save();
            }
        }
    }

    private void Save() => pluginInterface.SavePluginConfig(config);

    private sealed class Services
    {
        [PluginService] internal ICommandManager Commands { get; private init; } = null!;
        [PluginService] internal IFramework Framework { get; private init; } = null!;
    }
}
