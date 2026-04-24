using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiRouter.Dashboard;

public sealed class AnthropicRequest
{
    public string? Model    { get; set; }
    public int?    MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public List<AnthropicSystemBlock>? System  { get; set; }
    public List<AnthropicMessage>?     Messages { get; set; }
    public List<AnthropicTool>?        Tools    { get; set; }
    // any extra keys are ignored
}

public sealed class AnthropicMessage
{
    public string? Role    { get; set; }
    public JsonElement Content { get; set; }  // string or array of blocks
}

public sealed class AnthropicContentBlock
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    // tool_use / tool_result fields (ignore for display)
}

public sealed class AnthropicSystemBlock
{
    public string? Type { get; set; }
    public string? Text { get; set; }
}

public sealed class AnthropicTool
{
    public string? Name        { get; set; }
    public string? Description { get; set; }
}

public static class AnthropicRequestParser
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static AnthropicRequest? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AnthropicRequest>(json, _opts);
        }
        catch { return null; }
    }

    // Returns the readable text from a message's content (string or block array).
    public static string GetMessageText(AnthropicMessage msg)
    {
        var content = msg.Content;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new System.Text.StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var t))
                {
                    if (parts.Length > 0) parts.Append('\n');
                    parts.Append(t.GetString());
                }
            }
            return parts.ToString();
        }
        return string.Empty;
    }
}
