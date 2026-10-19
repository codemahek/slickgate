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
[JsonSerializable(typeof(RawConfigResponse))]
[JsonSerializable(typeof(RawConfigRequest))]
[JsonSerializable(typeof(ConfigActionResponse))]
public sealed partial class AdminJson : JsonSerializerContext;
