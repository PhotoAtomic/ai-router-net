using System.Text.Json;
using AiRouter.Logging;

namespace AiRouter.Dashboard;

/// <summary>
/// View-model representing a single proxied transaction (request + response),
/// merged from two JSONL log entries that share the same <see cref="CorrelationId"/>.
/// Mutable: the request half is set first, the response half is filled in later.
/// </summary>
public sealed class LogEntryViewModel
{
    // --- identity ---
    public Guid CorrelationId { get; init; }

    // --- request half ---
    public DateTimeOffset            RequestTimestamp { get; private set; }
    public string                    Model            { get; private set; } = string.Empty;
    public string                    ForcedModel      { get; private set; } = string.Empty;
    public string                    MatchedRule      { get; private set; } = string.Empty;
    public string                    TargetUrl        { get; private set; } = string.Empty;
    public long                      RequestBytes     { get; private set; }
    public Dictionary<string, string>? RequestHeaders { get; private set; }
    public string                    RequestBody      { get; private set; } = string.Empty;
    public string                    RequestPreview   { get; private set; } = string.Empty;

    // --- response half (populated when the Response entry arrives) ---
    public bool                      HasResponse       { get; private set; }
    public DateTimeOffset?           ResponseTimestamp { get; private set; }
    public double                    DurationMs        { get; private set; }
    public int                       StatusCode        { get; private set; }
    public long                      ResponseBytes     { get; private set; }
    public Dictionary<string, string>? ResponseHeaders { get; private set; }
    public string                    ResponseBody      { get; private set; } = string.Empty;
    public string                    ResponsePreview   { get; private set; } = string.Empty;

    // -------------------------------------------------------------------------

    public static LogEntryViewModel FromRequest(LogEntry e)
    {
        var vm = new LogEntryViewModel { CorrelationId = e.CorrelationId };
        vm.ApplyRequest(e);
        return vm;
    }

    public void ApplyRequest(LogEntry e)
    {
        RequestTimestamp = e.Timestamp;
        Model            = e.Model ?? string.Empty;
        ForcedModel      = ExtractForcedModel(e.RequestBody ?? string.Empty, e.Model);
        MatchedRule      = e.MatchedRule ?? string.Empty;
        TargetUrl        = e.TargetUrl   ?? string.Empty;
        RequestBytes     = e.RequestSizeBytes ?? 0;
        RequestHeaders   = e.RequestHeaders;
        RequestBody      = e.RequestBody ?? string.Empty;
        RequestPreview   = ExtractLastMessagePreview(RequestBody);
    }

    public void ApplyResponse(LogEntry e)
    {
        HasResponse       = true;
        ResponseTimestamp = e.Timestamp;
        DurationMs        = e.DurationMs        ?? 0;
        StatusCode        = e.StatusCode        ?? 0;
        ResponseBytes     = e.ResponseSizeBytes ?? 0;
        ResponseHeaders   = e.ResponseHeaders;
        ResponseBody      = e.ResponseBody ?? string.Empty;
        ResponsePreview   = ExtractResponsePreview(ResponseBody);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const int PreviewLength = 120;

    private static string ExtractForcedModel(string body, string? requestedModel)
    {
        var bodyModel = ExtractModelFromBody(body);
        // If the body model differs from the originally requested model, that's the forced one.
        if (!string.IsNullOrEmpty(requestedModel) &&
            !string.Equals(bodyModel, requestedModel, StringComparison.Ordinal))
        {
            return bodyModel;
        }
        return string.Empty;
    }

    private static string ExtractModelFromBody(string body)
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
            if (doc.RootElement.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var t))
                        return Truncate(t.GetString() ?? string.Empty);
                }
            }
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
