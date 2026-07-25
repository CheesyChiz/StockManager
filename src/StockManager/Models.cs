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
    [property: JsonIgnore] string SerializedRoute,
    [property: JsonPropertyName("Items")] List<ItemSnapshot> Items,
    [property: JsonIgnore] List<RouteNodeSnapshot> Nodes);

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
