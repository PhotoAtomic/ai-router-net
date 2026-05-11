namespace AiRouter.Protocol.OpenAi;

public class OpenAiChatResponse
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "chat.completion";
    public long Created { get; set; }
    public string Model { get; set; } = string.Empty;
    public List<OpenAiChoice> Choices { get; set; } = new();
    public OpenAiUsage? Usage { get; set; }
}

public class OpenAiChoice
{
    public int Index { get; set; }
    public OpenAiMessage Message { get; set; } = new();
    public string? FinishReason { get; set; }
}
