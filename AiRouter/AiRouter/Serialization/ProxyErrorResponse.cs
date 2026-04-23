using System.Text.Json.Serialization;

namespace AiRouter.Serialization;

record ProxyErrorDetail(string type, string message);
record ProxyErrorResponse(ProxyErrorDetail error);

[JsonSerializable(typeof(ProxyErrorResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class AiRouterJsonContext : JsonSerializerContext { }
