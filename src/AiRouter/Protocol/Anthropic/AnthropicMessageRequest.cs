using System.Text.Json;

namespace AiRouter.Protocol.Anthropic;

public class AnthropicMessageRequest
{
    public string Model { get; set; } = string.Empty;
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public bool? Stream { get; set; }
    public JsonElement? System { get; set; }
    public List<string>? StopSequences { get; set; }
    public List<AnthropicMessage>? Messages { get; set; }
    public List<AnthropicTool>? Tools { get; set; }
    public JsonElement? ToolChoice { get; set; }
    public JsonElement? Metadata { get; set; }
    public JsonElement? Thinking { get; set; }
    public JsonElement? OutputFormat { get; set; }
}
