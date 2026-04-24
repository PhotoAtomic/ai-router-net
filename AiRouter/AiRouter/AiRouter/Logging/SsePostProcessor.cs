using System.Text;
using System.Text.Json;

namespace AiRouter.Logging;

/// <summary>
/// Collapses an Anthropic-style SSE stream into a single, readable JSON object for logging.
/// If the body doesn't look like the expected format, the raw string is returned unchanged.
/// </summary>
static class SsePostProcessor
{
    public static string Process(string rawBody)
    {
        try
        {
            return TryCollapse(rawBody) ?? rawBody;
        }
        catch
        {
            return rawBody;
        }
    }

    private static string? TryCollapse(string rawBody)
    {
        // Parse all SSE event/data pairs
        var events = ParseSseEvents(rawBody);
        if (events.Count == 0) return null;

        // Must have at least message_start and one content_block_delta to be worth collapsing
        bool hasMessageStart  = events.Any(e => e.EventName == "message_start");
        bool hasContentDelta  = events.Any(e => e.EventName == "content_block_delta");
        if (!hasMessageStart || !hasContentDelta) return null;

        // Accumulators
        string? id         = null;
        string? model      = null;
        string? role       = null;
        string? stopReason = null;
        var textBuilder     = new StringBuilder();
        var thinkingBuilder = new StringBuilder();

        // usage fields
        long cacheReadInputTokens = 0;
        long inputTokens          = 0;
        long outputTokens         = 0;

        foreach (var ev in events)
        {
            if (string.IsNullOrEmpty(ev.Data) || ev.Data == "[DONE]") continue;

            using var doc = JsonDocument.Parse(ev.Data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return null; // unexpected shape → bail

            switch (typeProp.GetString())
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg))
                    {
                        id    = msg.TryGetProperty("id",    out var pid)    ? pid.GetString()   : id;
                        model = msg.TryGetProperty("model", out var pmodel) ? pmodel.GetString(): model;
                        role  = msg.TryGetProperty("role",  out var prole)  ? prole.GetString() : role;
                        if (msg.TryGetProperty("usage", out var u))
                        {
                            cacheReadInputTokens = u.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt64() : cacheReadInputTokens;
                            inputTokens          = u.TryGetProperty("input_tokens",            out var it) ? it.GetInt64() : inputTokens;
                        }
                    }
                    break;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                        if (deltaType == "text_delta" && delta.TryGetProperty("text", out var txt))
                            textBuilder.Append(txt.GetString());
                        else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                            thinkingBuilder.Append(th.GetString());
                    }
                    break;

                case "message_delta":
                    if (root.TryGetProperty("delta", out var msgDelta))
                        stopReason = msgDelta.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : stopReason;
                    if (root.TryGetProperty("usage", out var u2))
                        outputTokens = u2.TryGetProperty("output_tokens", out var ot) ? ot.GetInt64() : outputTokens;
                    break;

                // content_block_start, content_block_stop, message_stop → nothing to extract
            }
        }

        // Build the collapsed JSON
        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        if (id         is not null) writer.WriteString("id",          id);
        if (model      is not null) writer.WriteString("model",       model);
        if (role       is not null) writer.WriteString("role",        role);
        if (stopReason is not null) writer.WriteString("stop_reason", stopReason);

        var thinking = thinkingBuilder.ToString();
        if (thinking.Length > 0) writer.WriteString("thinking", thinking);

        writer.WriteString("text", textBuilder.ToString());

        writer.WriteStartObject("usage");
        writer.WriteNumber("cache_read_input_tokens", cacheReadInputTokens);
        writer.WriteNumber("input_tokens",            inputTokens);
        writer.WriteNumber("output_tokens",           outputTokens);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private record SseEvent(string EventName, string Data);

    private static List<SseEvent> ParseSseEvents(string rawBody)
    {
        var result    = new List<SseEvent>();
        var lines     = rawBody.Split('\n');
        string? currentEvent = null;
        string? currentData  = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                currentData = line["data:".Length..].Trim();
            }
            else if (line.Length == 0)
            {
                // blank line = end of event
                if (currentEvent is not null && currentData is not null)
                    result.Add(new SseEvent(currentEvent, currentData));
                currentEvent = null;
                currentData  = null;
            }
        }

        // flush last event if file didn't end with blank line
        if (currentEvent is not null && currentData is not null)
            result.Add(new SseEvent(currentEvent, currentData));

        return result;
    }
}
