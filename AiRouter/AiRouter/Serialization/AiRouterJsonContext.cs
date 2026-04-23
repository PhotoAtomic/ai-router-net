using System.Collections.Generic;
using System.Text.Json.Serialization;
using AiRouter.Logging;

namespace AiRouter.Serialization;

[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(ProxyErrorResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AiRouterJsonContext : JsonSerializerContext { }
