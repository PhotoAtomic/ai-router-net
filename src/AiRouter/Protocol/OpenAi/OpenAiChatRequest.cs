using System.Text.Json;

namespace AiRouter.Protocol.OpenAi;

public class OpenAiChatRequest
{
    public string Model { get; set; } = string.Empty;
    public List<OpenAiMessage> Messages { get; set; } = new();
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public bool? Stream { get; set; }
    public JsonElement? Stop { get; set; }
    public List<OpenAiTool>? Tools { get; set; }
    public JsonElement? ToolChoice { get; set; }
    public JsonElement? ResponseFormat { get; set; }
    public string? User { get; set; }
}
