using System.Collections.Generic;
using System.Text.Json.Serialization;
using AiRouter.Logging;
using AiRouter.Protocol.Anthropic;
using AiRouter.Protocol.OpenAi;

namespace AiRouter.Serialization;

[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(LogEntryType))]
[JsonSerializable(typeof(ProxyErrorResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]

// Anthropic protocol
[JsonSerializable(typeof(AnthropicMessageRequest))]
[JsonSerializable(typeof(AnthropicMessageResponse))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicContentBlock))]
[JsonSerializable(typeof(List<AnthropicContentBlock>))]
[JsonSerializable(typeof(AnthropicTool))]
[JsonSerializable(typeof(AnthropicToolChoice))]
[JsonSerializable(typeof(AnthropicUsage))]
[JsonSerializable(typeof(AnthropicStreamEvent))]
[JsonSerializable(typeof(AnthropicMessageStartEvent))]
[JsonSerializable(typeof(AnthropicContentBlockStartEvent))]
[JsonSerializable(typeof(AnthropicContentBlockDeltaEvent))]
[JsonSerializable(typeof(AnthropicContentBlockStopEvent))]
[JsonSerializable(typeof(AnthropicMessageDeltaEvent))]
[JsonSerializable(typeof(AnthropicMessageDelta))]
[JsonSerializable(typeof(AnthropicMessageStopEvent))]

// OpenAI protocol
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(OpenAiMessage))]
[JsonSerializable(typeof(OpenAiTool))]
[JsonSerializable(typeof(OpenAiFunctionDef))]
[JsonSerializable(typeof(OpenAiToolCall))]
[JsonSerializable(typeof(OpenAiFunctionCall))]
[JsonSerializable(typeof(OpenAiToolChoice))]
[JsonSerializable(typeof(OpenAiFunctionChoice))]
[JsonSerializable(typeof(OpenAiUsage))]
[JsonSerializable(typeof(OpenAiChoice))]
[JsonSerializable(typeof(OpenAiStreamChoice))]
[JsonSerializable(typeof(OpenAiStreamDelta))]

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(JsonStringEnumConverter<LogEntryType>) })]
internal partial class AiRouterJsonContext : JsonSerializerContext { }
