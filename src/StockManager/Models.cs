using System.Text.Json.Serialization;
using System.Numerics;

namespace StockManager;

public sealed record VislandSnapshot(
    [property: JsonPropertyName("IsRunning")] bool IsRunning,
    [property: JsonPropertyName("AutoExportEnabled")] bool AutoExportEnabled,
    [property: JsonPropertyName("FlightUnlocked")] bool? FlightUnlocked,
    [property: JsonIgnore] int? IslandRank,
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

internal static class RouteAccessibility
{
    // Island underwater gathering nodes are well below sea level. Keep a little tolerance for shore approaches.
    public const float UnderwaterY = -5f;

    // The imported Island route set has ground-accessible mountain nodes below this height; the summit-only
    // Quartz/Isleblooms nodes are above it. Route names and MountFly waypoints are also considered separately.
    public const float FlightOnlyY = 185f;

    public static bool IsUnderwater(Vector3 position) => position.Y < UnderwaterY;
    public static bool IsFlightOnlyAltitude(Vector3 position) => position.Y >= FlightOnlyY;
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
