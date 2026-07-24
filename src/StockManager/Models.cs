using System.Text.Json.Serialization;

namespace StockManager;

public sealed record IceboxSnapshot(
    [property: JsonPropertyName("ApiVersion")] int ApiVersion,
    [property: JsonPropertyName("IsRunning")] bool IsRunning,
    [property: JsonPropertyName("State")] string State,
    [property: JsonPropertyName("Routes")] List<RouteSnapshot> Routes);

public sealed record RouteSnapshot(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Items")] List<ItemSnapshot> Items);

public sealed record ItemSnapshot(
    [property: JsonPropertyName("Id")] int Id,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("PerLoop")] int PerLoop,
    [property: JsonPropertyName("CurrentCount")] int CurrentCount);
