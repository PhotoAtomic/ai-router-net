using System.Text.Json;

namespace AiRouter.Protocol.Anthropic;

public class AnthropicMessage
{
    public string Role { get; set; } = string.Empty;
    public JsonElement Content { get; set; }
    public JsonElement? CacheControl { get; set; }
}

public class AnthropicContentBlock
{
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? Thinking { get; set; }
    public string? Signature { get; set; }
    public string? Data { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement? Input { get; set; }
    public string? ToolUseId { get; set; }
    public string? PartialJson { get; set; }
    public JsonElement? CacheControl { get; set; }
}

public class AnthropicTool
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement? InputSchema { get; set; }
    public JsonElement? CacheControl { get; set; }
}

public class AnthropicToolChoice
{
    public string Type { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public class AnthropicUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }
}
