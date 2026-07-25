using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace StockManager;

public sealed partial class Plugin
{
    private string? routeWorkbenchImportedName;
    private string? routeWorkbenchUserRouteId;
    private int routeWorkbenchNodeIndex;
    private string routeWorkbenchStatus = "Create an editable copy of an imported route or save a generated preview.";

    private void DrawRouteWorkbench()
    {
        if (snapshot == null)
        {
            ImGui.TextWrapped("Travel to the Island and load Visland routes before using the route workbench.");
            return;
        }

        ImGui.TextWrapped("Imported Visland routes remain unchanged. Editing creates a separate Stock Manager copy that can be tested or enabled for automatic selection.");
        ImGui.TextColored(new Vector4(1f, .68f, .25f, 1),
            "Experimental: supervise route tests. A valid node can still be inaccessible because of terrain, water transitions, flight, or Island progression.");

        if (ImGui.CollapsingHeader("Generator", ImGuiTreeNodeFlags.DefaultOpen))
            DrawExperimentalRouteGenerator(snapshot);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Create from an imported route");
        var imported = snapshot.Routes.OrderBy(x => x.Name).ToList();
        routeWorkbenchImportedName ??= imported.FirstOrDefault()?.Name;
        var source = imported.FirstOrDefault(x => string.Equals(x.Name, routeWorkbenchImportedName, StringComparison.OrdinalIgnoreCase))
                     ?? imported.FirstOrDefault();
        if (source != null)
        {
            ImGui.SetNextItemWidth(Math.Min(520, ImGui.GetContentRegionAvail().X - 180));
            if (ImGui.BeginCombo("##ImportedRoute", source.Name, ImGuiComboFlags.HeightLarge))
            {
                foreach (var route in imported)
                {
                    if (!ImGui.Selectable(route.Name, route == source)) continue;
                    source = route;
                    routeWorkbenchImportedName = route.Name;
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Create editable copy"))
            {
                var saved = CreateUserRoute(source, $"{source.Name} (custom)");
                config.UserRoutes.Add(saved);
                routeWorkbenchUserRouteId = saved.Id;
                Save();
                routeWorkbenchStatus = $"Created {saved.Name}. The original Visland route was not changed.";
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Editable Stock Manager routes");
        if (config.UserRoutes.Count == 0)
        {
            ImGui.TextDisabled("No editable routes have been saved yet.");
            ImGui.TextWrapped(routeWorkbenchStatus);
            return;
        }

        routeWorkbenchUserRouteId ??= config.UserRoutes[0].Id;
        var selected = config.UserRoutes.FirstOrDefault(x => x.Id == routeWorkbenchUserRouteId) ?? config.UserRoutes[0];
        routeWorkbenchUserRouteId = selected.Id;
        ImGui.SetNextItemWidth(Math.Min(520, ImGui.GetContentRegionAvail().X - 125));
        if (ImGui.BeginCombo("##EditableRoute", selected.Name, ImGuiComboFlags.HeightLarge))
        {
            foreach (var route in config.UserRoutes.OrderBy(x => x.Name))
            {
                if (!ImGui.Selectable($"{route.Name}##{route.Id}", route == selected)) continue;
                selected = route;
                routeWorkbenchUserRouteId = route.Id;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete route")) ImGui.OpenPopup("Delete editable route?");
        if (ImGui.BeginPopup("Delete editable route?"))
        {
            ImGui.TextWrapped($"Delete {selected.Name}? The imported Visland route will not be affected.");
            if (ImGui.Button("Delete"))
            {
                config.UserRoutes.Remove(selected);
                routeWorkbenchUserRouteId = config.UserRoutes.FirstOrDefault()?.Id;
                Save();
                routeWorkbenchStatus = "Editable route deleted.";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        var name = selected.Name;
        ImGui.SetNextItemWidth(Math.Min(520, ImGui.GetContentRegionAvail().X));
        if (ImGui.InputText("Name", ref name, 100))
        {
            selected.Name = string.IsNullOrWhiteSpace(name) ? "Custom route" : name.TrimStart();
            Save();
        }
        var nameConflict = snapshot.Routes.Any(x => string.Equals(x.Name, selected.Name, StringComparison.OrdinalIgnoreCase))
                           || config.UserRoutes.Any(x => x != selected && string.Equals(x.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
        if (nameConflict)
            ImGui.TextColored(new Vector4(1f, .4f, .3f, 1), "Choose a unique name before enabling this route for automation.");

        var useForAutomation = selected.UseForAutomation;
        if (nameConflict) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Include this route in automatic selection", ref useForAutomation))
        {
            selected.UseForAutomation = useForAutomation;
            Save();
        }
        if (nameConflict) ImGui.EndDisabled();

        var editableRoute = BuildUserRouteSnapshot(selected, snapshot);
        var routeCompatible = snapshot.FlightUnlocked == true || !editableRoute.RequiresFlying;
        ImGui.TextDisabled($"{selected.Waypoints.Count} waypoints | {editableRoute.Nodes.Count} recognized gathering nodes | "
                           + (editableRoute.RequiresFlying ? "flight required" : "ground/underwater compatible"));
        var physicalNodeCount = editableRoute.Nodes.GroupBy(NodeKey).Count();
        if (physicalNodeCount < 11)
            ImGui.TextColored(new Vector4(1f, .45f, .3f, 1), "Fewer than 11 unique gathering nodes cannot form a complete Island respawn loop.");
        else if (physicalNodeCount == 11)
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1), "11 unique gathering nodes is the exact minimum; one missed interaction can leave the next loop unrespawned.");

        var canTest = !config.Enabled && !snapshot.IsRunning && pendingRouteStart == null && !experimentalTestRunning
                      && adapter.IsNavmeshReady && routeCompatible && selected.Waypoints.Count > 0;
        if (!canTest) ImGui.BeginDisabled();
        if (ImGui.Button("Run one test loop"))
        {
            QueueRouteStart(editableRoute, null, PendingRoutePurpose.Experimental);
            routeWorkbenchStatus = $"Testing {selected.Name}.";
        }
        if (!canTest) ImGui.EndDisabled();
        ImGui.SameLine();
        var testActive = experimentalTestRunning || pendingRouteStart?.Purpose == PendingRoutePurpose.Experimental;
        if (!testActive) ImGui.BeginDisabled();
        if (ImGui.Button("Stop test loop")) StopExperimentalTest();
        if (!testActive) ImGui.EndDisabled();
        if (!routeCompatible) ImGui.TextColored(new Vector4(1f, .45f, .3f, 1), "This route requires Island flight, which is not currently unlocked.");
        ImGui.TextWrapped(routeWorkbenchStatus);

        DrawWaypointTools(selected, snapshot);
        DrawWaypointTable(selected);
    }

    private void DrawWaypointTools(UserRouteConfiguration route, VislandSnapshot data)
    {
        ImGui.Spacing();
        if (ImGui.Button("Add current position"))
        {
            var player = services.Objects.LocalPlayer;
            if (player != null)
            {
                route.Waypoints.Add(new UserWaypointConfiguration
                {
                    X = player.Position.X,
                    Y = player.Position.Y,
                    Z = player.Position.Z,
                    Radius = 3,
                    Movement = RouteMovement.Normal,
                    Pathfind = true,
                });
                Save();
                routeWorkbenchStatus = "Added the current character position as a navigation waypoint.";
            }
        }

        var nodes = GetKnownNodes(data)
            .OrderBy(x => x.ObjectName).ThenBy(x => x.Position.X).ThenBy(x => x.Position.Z).ToList();
        if (nodes.Count == 0) return;
        routeWorkbenchNodeIndex = Math.Clamp(routeWorkbenchNodeIndex, 0, nodes.Count - 1);
        var node = nodes[routeWorkbenchNodeIndex];
        ImGui.SetNextItemWidth(Math.Min(520, ImGui.GetContentRegionAvail().X - 115));
        if (ImGui.BeginCombo("##RegisteredNode", $"{node.ObjectName}  ({node.Position.X:F0}, {node.Position.Z:F0})", ImGuiComboFlags.HeightLarge))
        {
            for (var index = 0; index < nodes.Count; index++)
            {
                var candidate = nodes[index];
                if (!ImGui.Selectable($"{candidate.ObjectName}  ({candidate.Position.X:F0}, {candidate.Position.Z:F0})##node{index}", index == routeWorkbenchNodeIndex)) continue;
                routeWorkbenchNodeIndex = index;
                node = candidate;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Add node"))
        {
            var previous = route.Waypoints.LastOrDefault();
            var distance = previous == null ? 0 : Vector3.Distance(new Vector3(previous.X, previous.Y, previous.Z), node.Position);
            route.Waypoints.Add(new UserWaypointConfiguration
            {
                X = node.Position.X,
                Y = node.Position.Y,
                Z = node.Position.Z,
                ZoneId = node.ZoneId,
                Radius = 3,
                Movement = previous != null && distance <= 18 ? RouteMovement.Normal
                    : data.FlightUnlocked == true ? RouteMovement.MountFly : RouteMovement.MountNoFly,
                Pathfind = true,
                ObjectId = node.ObjectId,
                ObjectName = node.ObjectName,
                InteractionX = node.Position.X,
                InteractionY = node.Position.Y,
                InteractionZ = node.Position.Z,
                Interaction = 1,
                ShowInteractions = true,
            });
            Save();
            routeWorkbenchStatus = $"Added {node.ObjectName}.";
        }
    }

    private void DrawWaypointTable(UserRouteConfiguration route)
    {
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("EditableWaypoints", 7, flags, new Vector2(0, -1))) return;
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 32);
        ImGui.TableSetupColumn("Node / purpose", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Radius", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Movement", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableSetupColumn("Pathfind", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 118);
        ImGui.TableHeadersRow();

        var changed = false;
        var moveFrom = -1;
        var moveTo = -1;
        var remove = -1;
        for (var index = 0; index < route.Waypoints.Count; index++)
        {
            var waypoint = route.Waypoints[index];
            ImGui.PushID(index);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted((index + 1).ToString());
            ImGui.TableNextColumn();
            if (string.IsNullOrWhiteSpace(waypoint.ObjectName)) ImGui.TextDisabled("Navigation waypoint");
            else ImGui.TextWrapped(waypoint.ObjectName);
            ImGui.TableNextColumn();
            var position = new Vector3(waypoint.X, waypoint.Y, waypoint.Z);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat3("##Position", ref position))
            {
                waypoint.X = position.X; waypoint.Y = position.Y; waypoint.Z = position.Z;
                changed = true;
            }
            ImGui.TableNextColumn();
            var radius = waypoint.Radius;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat("##Radius", ref radius, .5f, 1f, "%.1f"))
            {
                waypoint.Radius = Math.Clamp(radius, 1, 10);
                changed = true;
            }
            ImGui.TableNextColumn();
            var movement = (int)waypoint.Movement;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##Movement", ref movement, "Walk / dismount\0Mount + fly\0Mount / swim\0"))
            {
                waypoint.Movement = (RouteMovement)movement;
                changed = true;
            }
            ImGui.TableNextColumn();
            var pathfind = waypoint.Pathfind;
            if (ImGui.Checkbox("##Pathfind", ref pathfind)) { waypoint.Pathfind = pathfind; changed = true; }
            ImGui.TableNextColumn();
            if (index == 0) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Up")) { moveFrom = index; moveTo = index - 1; }
            if (index == 0) ImGui.EndDisabled();
            ImGui.SameLine();
            if (index == route.Waypoints.Count - 1) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Down")) { moveFrom = index; moveTo = index + 1; }
            if (index == route.Waypoints.Count - 1) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("X")) remove = index;
            ImGui.PopID();
        }
        ImGui.EndTable();

        if (moveFrom >= 0)
        {
            (route.Waypoints[moveFrom], route.Waypoints[moveTo]) = (route.Waypoints[moveTo], route.Waypoints[moveFrom]);
            changed = true;
        }
        if (remove >= 0)
        {
            route.Waypoints.RemoveAt(remove);
            changed = true;
        }
        if (changed) Save();
    }

    private UserRouteConfiguration CreateUserRoute(RouteSnapshot source, string suggestedName)
    {
        var name = suggestedName;
        var suffix = 2;
        while (config.UserRoutes.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
               || snapshot?.Routes.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
            name = $"{suggestedName} {suffix++}";

        return new UserRouteConfiguration
        {
            Name = name,
            SourceRouteName = source.Name,
            Waypoints = source.Waypoints.Select(ToUserWaypoint).ToList(),
        };
    }

    private static UserWaypointConfiguration ToUserWaypoint(RouteWaypointSnapshot waypoint) => new()
    {
        X = waypoint.Position.X,
        Y = waypoint.Position.Y,
        Z = waypoint.Position.Z,
        ZoneId = waypoint.ZoneId,
        Radius = waypoint.Radius,
        Movement = waypoint.Movement,
        Pathfind = waypoint.Pathfind,
        ObjectId = waypoint.ObjectId,
        ObjectName = waypoint.ObjectName,
        InteractionX = waypoint.InteractionPosition.X,
        InteractionY = waypoint.InteractionPosition.Y,
        InteractionZ = waypoint.InteractionPosition.Z,
        Interaction = waypoint.Interaction,
        ShowInteractions = waypoint.ShowInteractions,
        ShowWaits = waypoint.ShowWaits,
        WaitForCondition = waypoint.WaitForCondition,
        WaitTimeMs = waypoint.WaitTimeMs,
        WaitTimeEtX = waypoint.WaitTimeEt.X,
        WaitTimeEtY = waypoint.WaitTimeEt.Y,
        RouteName = waypoint.RouteName,
    };

    private IEnumerable<RouteSnapshot> GetUserRouteSnapshots(VislandSnapshot data, bool automationOnly)
    {
        var importedNames = data.Routes.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uniqueCustomNames = config.UserRoutes.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return config.UserRoutes
            .Where(x => !automationOnly || x.UseForAutomation
                && uniqueCustomNames.Contains(x.Name)
                && !importedNames.Contains(x.Name))
            .Select(x => BuildUserRouteSnapshot(x, data))
            .Where(x => x.Waypoints.Count > 0 && x.Items.Count > 0);
    }

    private List<RouteNodeSnapshot> GetKnownNodes(VislandSnapshot data) => data.Routes.SelectMany(x => x.Nodes)
        .Concat(GetUserRouteSnapshots(data, false).SelectMany(x => x.Nodes))
        .GroupBy(NodeKey).Select(x => x.First()).ToList();

    private static RouteSnapshot BuildUserRouteSnapshot(UserRouteConfiguration saved, VislandSnapshot data)
    {
        var currentItems = data.Routes.SelectMany(x => x.Items).GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var waypoints = saved.Waypoints.Select(x => new RouteWaypointSnapshot(
            new Vector3(x.X, x.Y, x.Z), x.ZoneId, Math.Clamp(x.Radius, 1, 10), x.Movement, x.Pathfind,
            x.ObjectId, x.ObjectName, new Vector3(x.InteractionX, x.InteractionY, x.InteractionZ), x.Interaction,
            x.ShowInteractions, x.ShowWaits, x.WaitForCondition, x.WaitTimeMs,
            new Vector2(x.WaitTimeEtX, x.WaitTimeEtY), x.RouteName)).ToList();
        var resources = saved.Waypoints
            .Where(x => !string.IsNullOrWhiteSpace(x.ObjectName) && IslandResources.ByNode.ContainsKey(x.ObjectName))
            .SelectMany(x => IslandResources.ByNode[x.ObjectName]).GroupBy(x => x.Id).ToList();
        var items = resources.Select(group =>
        {
            var resource = group.First();
            var current = currentItems.GetValueOrDefault(resource.Id);
            return new ItemSnapshot(resource.Id, resource.Name, group.Count(), current?.CurrentCount ?? 0, current?.IsAvailable ?? false);
        }).OrderBy(x => x.Name).ToList();
        var nodes = saved.Waypoints.Where(x => x.ObjectId != 0 && IslandResources.ByNode.TryGetValue(x.ObjectName, out _))
            .Select(x => new RouteNodeSnapshot(new Vector3(x.X, x.Y, x.Z), x.ZoneId, x.ObjectId, x.ObjectName,
                IslandResources.ByNode[x.ObjectName].Select(resource => resource.Id).ToArray())).ToList();
        var hasUnderwater = waypoints.Any(x => RouteAccessibility.IsUnderwater(x.Position));
        var hasFlightMovement = waypoints.Any(x => x.Movement == RouteMovement.MountFly);
        var requiresFlying = saved.Name.Contains("flying", StringComparison.OrdinalIgnoreCase)
                             || waypoints.Any(x => RouteAccessibility.IsFlightOnlyAltitude(x.Position))
                             || (hasFlightMovement && !hasUnderwater);
        return new RouteSnapshot(saved.Name, "Stock Manager", requiresFlying, 0, 0, items, nodes, waypoints);
    }
}
