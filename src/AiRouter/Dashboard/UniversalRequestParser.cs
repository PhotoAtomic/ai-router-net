using System.Text.Json;
using System.Text.Json.Serialization;
using AiRouter.Protocol.OpenAi;

namespace AiRouter.Dashboard;

public static class UniversalRequestParser
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Attempts to parse a request body as either Anthropic or OpenAI format.
    /// Returns null if parsing fails for both.
    /// </summary>
    public static ParsedRequest? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // Try Anthropic first
        var anthropic = AnthropicRequestParser.TryParse(json);
        if (anthropic is not null)
        {
            return new ParsedRequest
            {
                Format = RequestFormat.Anthropic,
                Model = anthropic.Model,
                MaxTokens = anthropic.MaxTokens,
                Temperature = anthropic.Temperature,
                MessageCount = anthropic.Messages?.Count ?? 0,
                ToolCount = anthropic.Tools?.Count ?? 0,
                Metadata = AnthropicRequestParser.ParseMetadata(anthropic),
                LastMessagePreview = ExtractAnthropicPreview(anthropic),
            };
        }

        // Try OpenAI
        try
        {
            var openAi = JsonSerializer.Deserialize<OpenAiChatRequest>(json, _opts);
            if (openAi is not null && openAi.Messages is not null)
            {
                return new ParsedRequest
                {
                    Format = RequestFormat.OpenAI,
                    Model = openAi.Model,
                    MaxTokens = openAi.MaxCompletionTokens ?? openAi.MaxTokens,
                    Temperature = openAi.Temperature,
                    MessageCount = openAi.Messages.Count,
                    ToolCount = openAi.Tools?.Count ?? 0,
                    Metadata = null,
                    LastMessagePreview = ExtractOpenAiPreview(openAi),
                };
            }
        }
        catch { }

        return null;
    }

    private static string ExtractAnthropicPreview(AnthropicRequest req)
    {
        if (req.Messages is null || req.Messages.Count == 0) return string.Empty;
        var last = req.Messages[^1];
        return AnthropicRequestParser.GetMessageText(last);
    }

    private static string ExtractOpenAiPreview(OpenAiChatRequest req)
    {
        if (req.Messages.Count == 0) return string.Empty;
        var last = req.Messages[^1];
        if (!last.Content.HasValue) return string.Empty;
        if (last.Content.Value.ValueKind == JsonValueKind.String)
            return last.Content.Value.GetString() ?? string.Empty;
        return string.Empty;
    }
}

public enum RequestFormat
{
    Anthropic,
    OpenAI,
}

public sealed class ParsedRequest
{
    public RequestFormat Format { get; init; }
    public string? Model { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public int MessageCount { get; init; }
    public int ToolCount { get; init; }
    public RequestMetadata? Metadata { get; init; }
    public string LastMessagePreview { get; init; } = string.Empty;
}
