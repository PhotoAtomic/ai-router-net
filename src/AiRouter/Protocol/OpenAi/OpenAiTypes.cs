using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiRouter.Protocol.OpenAi;

public class OpenAiMessage
{
    public string Role { get; set; } = string.Empty;
    public JsonElement? Content { get; set; }
    public List<OpenAiToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("thinking_blocks")]
    public JsonElement? ThinkingBlocks { get; set; }
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

public class OpenAiTool
{
    public string Type { get; set; } = "function";
    public OpenAiFunctionDef? Function { get; set; }
}

public class OpenAiFunctionDef
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement? Parameters { get; set; }
    public JsonElement? CacheControl { get; set; }
}

public class OpenAiToolCall
{
    public int Index { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "function";
    public OpenAiFunctionCall? Function { get; set; }
}

public class OpenAiFunctionCall
{
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

public class OpenAiToolChoice
{
    public string Type { get; set; } = "function";
    public OpenAiFunctionChoice? Function { get; set; }
}

public class OpenAiFunctionChoice
{
    public string Name { get; set; } = string.Empty;
}

public class OpenAiUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}
