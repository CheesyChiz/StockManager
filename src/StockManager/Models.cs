using System.Text.Json.Serialization;
using System.Numerics;

namespace StockManager;

public sealed record VislandSnapshot(
    [property: JsonPropertyName("IsRunning")] bool IsRunning,
    [property: JsonPropertyName("AutoExportEnabled")] bool AutoExportEnabled,
    [property: JsonPropertyName("FlightUnlocked")] bool? FlightUnlocked,
    [property: JsonPropertyName("Routes")] List<RouteSnapshot> Routes);

public sealed record RouteSnapshot(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Group")] string Group,
    [property: JsonPropertyName("RequiresFlying")] bool RequiresFlying,
    [property: JsonIgnore] int Food,
    [property: JsonIgnore] int TargetGatherItem,
    [property: JsonPropertyName("Items")] List<ItemSnapshot> Items,
    [property: JsonIgnore] List<RouteNodeSnapshot> Nodes,
    [property: JsonIgnore] List<RouteWaypointSnapshot> Waypoints);

public sealed record RouteWaypointSnapshot(
    Vector3 Position,
    uint ZoneId,
    float Radius,
    RouteMovement Movement,
    bool Pathfind,
    uint ObjectId,
    string ObjectName,
    Vector3 InteractionPosition,
    int Interaction,
    bool ShowInteractions,
    bool ShowWaits,
    int WaitForCondition,
    int WaitTimeMs,
    Vector2 WaitTimeEt,
    string RouteName);

public enum RouteMovement
{
    Normal,
    MountFly,
    MountNoFly,
}

public sealed record RouteNodeSnapshot(
    Vector3 Position,
    uint ZoneId,
    uint ObjectId,
    string ObjectName,
    IReadOnlyList<int> ItemIds);

public sealed record ItemSnapshot(
    [property: JsonPropertyName("Id")] int Id,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("PerLoop")] int PerLoop,
    [property: JsonPropertyName("CurrentCount")] int CurrentCount,
    [property: JsonPropertyName("IsAvailable")] bool IsAvailable);
