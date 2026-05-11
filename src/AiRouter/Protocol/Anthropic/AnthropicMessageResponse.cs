namespace AiRouter.Protocol.Anthropic;

public class AnthropicMessageResponse
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "message";
    public string Role { get; set; } = "assistant";
    public string Model { get; set; } = string.Empty;
    public List<AnthropicContentBlock>? Content { get; set; }
    public string? StopReason { get; set; }
    public string? StopSequence { get; set; }
    public AnthropicUsage? Usage { get; set; }
}
