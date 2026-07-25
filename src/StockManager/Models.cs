using System.Text.Json.Serialization;

namespace StockManager;

public sealed record VislandSnapshot(
    [property: JsonPropertyName("IsRunning")] bool IsRunning,
    [property: JsonPropertyName("AutoExportEnabled")] bool AutoExportEnabled,
    [property: JsonPropertyName("AutoExportLimit")] int AutoExportLimit,
    [property: JsonPropertyName("Routes")] List<RouteSnapshot> Routes);

public sealed record RouteSnapshot(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Group")] string Group,
    [property: JsonPropertyName("RequiresFlying")] bool RequiresFlying,
    [property: JsonIgnore] string SerializedRoute,
    [property: JsonPropertyName("Items")] List<ItemSnapshot> Items);

public sealed record ItemSnapshot(
    [property: JsonPropertyName("Id")] int Id,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("PerLoop")] int PerLoop,
    [property: JsonPropertyName("CurrentCount")] int CurrentCount,
    [property: JsonPropertyName("IsAvailable")] bool IsAvailable);
