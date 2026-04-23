using System.Text.Json;
using AiRouter.Logging;

namespace AiRouter.Dashboard;

/// <summary>
/// View-model wrapping a <see cref="LogEntry"/> with pre-computed display fields.
/// </summary>
public sealed class LogEntryViewModel
{
    // --- raw data ---
    public LogEntry Raw { get; init; } = default!;

    // --- convenience aliases ---
    public DateTimeOffset RequestTimestamp  => Raw.RequestTimestamp;
    public DateTimeOffset ResponseTimestamp => Raw.ResponseTimestamp;
    public double         DurationMs        => Raw.DurationMs;
    public string         Model             => Raw.Model;
    public string         MatchedRule       => Raw.MatchedRule;
    public string         TargetUrl         => Raw.TargetUrl;
    public int            StatusCode        => Raw.StatusCode;
    public long           RequestBytes      => Raw.RequestSizeBytes;
    public long           ResponseBytes     => Raw.ResponseSizeBytes;
    public string         RequestBody       => Raw.RequestBody;
    public string         ResponseBody      => Raw.ResponseBody;

    // --- extracted / computed ---
    public string ForcedModel       { get; init; } = string.Empty;
    public string RequestPreview    { get; init; } = string.Empty;
    public string ResponsePreview   { get; init; } = string.Empty;

    // -------------------------------------------------------------------------

    public static LogEntryViewModel FromLogEntry(LogEntry e)
    {
        var forcedModel    = ExtractForcedModel(e.RequestBody);
        var reqPreview     = ExtractLastMessagePreview(e.RequestBody);
        var respPreview    = ExtractResponsePreview(e.ResponseBody);

        return new LogEntryViewModel
        {
            Raw           = e,
            ForcedModel   = forcedModel,
            RequestPreview = reqPreview,
            ResponsePreview= respPreview,
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const int PreviewLength = 120;

    private static string ExtractForcedModel(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("model", out var m))
                return m.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string ExtractLastMessagePreview(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var msgs)) return string.Empty;
            if (msgs.ValueKind != JsonValueKind.Array || msgs.GetArrayLength() == 0) return string.Empty;

            // last message
            JsonElement last = default;
            foreach (var m in msgs.EnumerateArray()) last = m;

            return ExtractContent(last);
        }
        catch { return string.Empty; }
    }

    private static string ExtractResponsePreview(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Anthropic-style: content[].text
            if (doc.RootElement.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var t))
                        return Truncate(t.GetString() ?? string.Empty);
                }
            }
            // OpenAI-style: choices[0].message.content
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg))
                        return ExtractContent(msg);
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string ExtractContent(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return Truncate(content.GetString() ?? string.Empty);
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var t))
                    return Truncate(t.GetString() ?? string.Empty);
            }
        }
        return string.Empty;
    }

    private static string Truncate(string s)
        => s.Length <= PreviewLength ? s : s[..PreviewLength] + "…";
}
