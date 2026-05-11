using System.Text.Json;

namespace AiRouter.Protocol.OpenAi;

public class OpenAiStreamChunk
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "chat.completion.chunk";
    public long Created { get; set; }
    public string Model { get; set; } = string.Empty;
    public List<OpenAiStreamChoice> Choices { get; set; } = new();
}

public class OpenAiStreamChoice
{
    public int Index { get; set; }
    public OpenAiStreamDelta Delta { get; set; } = new();
    public string? FinishReason { get; set; }
}

public class OpenAiStreamDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public JsonElement? ThinkingBlocks { get; set; }
    public List<OpenAiToolCall>? ToolCalls { get; set; }
}
