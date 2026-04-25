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

    /// <summary>Parsed metadata from the request body (device_id / session_id / …).</summary>
    public RequestMetadata?          Metadata         { get; private set; }

    /// <summary>Number of messages in the request body.</summary>
    public int                       MessageCount     { get; private set; }

    /// <summary>Number of tools defined in the request body.</summary>
    public int                       ToolCount        { get; private set; }

    /// <summary>
    /// The identity key used to assign a session color.
    /// Prefers session_id, falls back to device_id, then raw user_id.
    /// </summary>
    public string                    SessionIdentity  { get; private set; } = string.Empty;

    /// <summary>Neon hex color derived from <see cref="SessionIdentity"/>.</summary>
    public string                    SessionColor     { get; private set; } = "#4a5568";

    // --- response half (populated when the Response entry arrives) ---
    public bool                      HasResponse       { get; private set; }
    public DateTimeOffset?           ResponseTimestamp { get; private set; }
    public double                    DurationMs        { get; private set; }
    public int                       StatusCode        { get; private set; }
    public long                      ResponseBytes     { get; private set; }
    public Dictionary<string, string>? ResponseHeaders { get; private set; }
    public string                    ResponseBody      { get; private set; } = string.Empty;
    public string                    ResponsePreview   { get; private set; } = string.Empty;

    /// <summary>True when the response contains at least one tool_use block.</summary>
    public bool                      HasToolUse        { get; private set; }

    /// <summary>
    /// Diagnostic lines describing every llama.cpp model-recovery attempt
    /// performed for this transaction. Non-empty means the upstream initially
    /// returned a 500 and the router tried to recover (and may or may not
    /// have succeeded — the final <see cref="StatusCode"/> tells the rest).
    /// </summary>
    public IReadOnlyList<string>     RecoveryAttempts  { get; private set; } = Array.Empty<string>();

    /// <summary>True when at least one recovery attempt was logged.</summary>
    public bool                      HasRecovery       => RecoveryAttempts.Count > 0;

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

        var req = AnthropicRequestParser.TryParse(RequestBody);
        Metadata        = req is not null ? AnthropicRequestParser.ParseMetadata(req) : null;
        MessageCount    = req?.Messages?.Count ?? 0;
        ToolCount       = req?.Tools?.Count    ?? 0;
        SessionIdentity = Metadata?.SessionId
                       ?? Metadata?.DeviceId
                       ?? Metadata?.RawUserId
                       ?? string.Empty;
        SessionColor    = SessionColorHelper.ColorFor(SessionIdentity);
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
        HasToolUse        = DetectToolUse(ResponseBody);
        RecoveryAttempts  = e.RecoveryAttempts is { Count: > 0 }
                            ? e.RecoveryAttempts.ToArray()
                            : Array.Empty<string>();
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

    private static bool DetectToolUse(string body)
    {
        try
        {
            var entries = LoggedResponseParser.ParseEnvelope(body);
            if (entries.Count > 0)
            {
                var rec = LoggedResponseParser.Reconstruct(entries);
                return rec.ToolUses.Count > 0;
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
                        return true;
            }
        }
        catch { }
        return false;
    }

    private static string ExtractResponsePreview(string body)
    {
        try
        {
            // New wrapped envelope: { "responses": [...] } — reconstruct text from it.
            var entries = LoggedResponseParser.ParseEnvelope(body);
            if (entries.Count > 0)
            {
                var rec = LoggedResponseParser.Reconstruct(entries);
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(rec.Text))
                    parts.Add(Truncate(rec.Text));
                if (rec.ToolUses.Count > 0)
                {
                    var names = string.Join(", ", rec.ToolUses.ConvertAll(t => t.Name ?? "?"));
                    parts.Add($"🔨 {names}");
                }
                if (!string.IsNullOrEmpty(rec.Thinking))
                    parts.Add($"💭 {Truncate(rec.Thinking)}");
                if (parts.Count > 0)
                    return Truncate(string.Join(" · ", parts));
            }

            // Fallback: legacy / unknown shape.
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
            string? textPreview = null, toolResultPreview = null, toolUsePreview = null, thinkingPreview = null;
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                switch (type)
                {
                    case "text":
                        if (textPreview is null && block.TryGetProperty("text", out var tv))
                            textPreview = tv.GetString();
                        break;
                    case "tool_result":
                        if (toolResultPreview is null)
                        {
                            var id = block.TryGetProperty("tool_use_id", out var tid) ? (tid.GetString() ?? "") : "";
                            var resultText = ExtractToolResultText(block);
                            toolResultPreview = string.IsNullOrEmpty(resultText)
                                ? $"↳ {id}"
                                : string.IsNullOrEmpty(id) ? resultText : $"↳ {id}: {resultText}";
                        }
                        break;
                    case "tool_use":
                        if (toolUsePreview is null)
                        {
                            var name = block.TryGetProperty("name", out var nv) ? (nv.GetString() ?? "?") : "?";
                            toolUsePreview = $"🔨 {name}";
                        }
                        break;
                    case "thinking":
                        if (thinkingPreview is null && block.TryGetProperty("thinking", out var th))
                            thinkingPreview = $"💭 {th.GetString()}";
                        break;
                }
            }
            var result = textPreview ?? toolResultPreview ?? toolUsePreview ?? thinkingPreview ?? string.Empty;
            return Truncate(result);
        }
        return string.Empty;
    }

    private static string ExtractToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var cv)) return string.Empty;
        if (cv.ValueKind == JsonValueKind.String) return cv.GetString() ?? string.Empty;
        if (cv.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in cv.EnumerateArray())
                if (part.TryGetProperty("text", out var pt)) return pt.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string Truncate(string s)
        => s.Length <= PreviewLength ? s : s[..PreviewLength] + "…";
}
