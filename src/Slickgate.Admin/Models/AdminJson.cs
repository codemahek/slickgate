using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slickgate.Admin.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(RoutesResponse))]
[JsonSerializable(typeof(ReloadResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(ContainersResponse))]
[JsonSerializable(typeof(MappingsResponse))]
[JsonSerializable(typeof(MappingResponse))]
[JsonSerializable(typeof(RouteMapping))]
public sealed partial class AdminJson : JsonSerializerContext;
