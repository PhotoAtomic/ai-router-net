using System.Text;
using System.Text.Json;

namespace AiRouter.Logging;

/// <summary>
/// Builds the structured JSON we persist into <see cref="LogEntry.ResponseBody"/>.
///
/// Goal: keep the raw upstream payload (no lossy collapsing) so the dashboard can
/// inspect every detail — in particular, how/when the model asks to invoke tools.
///
/// Output shape (always wrapped so the dashboard has a single contract to parse):
/// {
///   "responses": [
///     // for SSE streams, one entry per event, in order, data preserved as-is:
///     { "kind": "event", "event": "&lt;name&gt;", "data": &lt;parsed-json-or-raw-string&gt; },
///     ...
///     // for a single non-SSE response:
///     { "kind": "response", "contentType": "application/json",
///       "body": &lt;parsed-json-or-raw-string&gt; }
///   ]
/// }
/// </summary>
static class ResponseBodyBuilder
{
    public static string Build(string rawBody, string? contentType, bool isStream)
    {
        try
        {
            return isStream
                ? BuildFromSse(rawBody)
                : BuildFromSingle(rawBody, contentType);
        }
        catch
        {
            // On any unexpected failure, fall back to a single raw entry so we never
            // lose the upstream bytes.
            return BuildFallback(rawBody, contentType);
        }
    }

    // ── single response ──────────────────────────────────────────────────────

    private static string BuildFromSingle(string rawBody, string? contentType)
    {
        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteStartArray("responses");

        writer.WriteStartObject();
        writer.WriteString("kind", "response");
        if (!string.IsNullOrEmpty(contentType))
            writer.WriteString("contentType", contentType);
        WriteDataOrBody(writer, "body", rawBody);
        writer.WriteEndObject();

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── SSE stream ───────────────────────────────────────────────────────────

    private static string BuildFromSse(string rawBody)
    {
        var events = ParseSseEvents(rawBody);

        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteStartArray("responses");

        foreach (var ev in events)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "event");
            writer.WriteString("event", ev.EventName);
            WriteDataOrBody(writer, "data", ev.Data);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void WriteDataOrBody(Utf8JsonWriter writer, string propertyName, string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            writer.WriteString(propertyName, string.Empty);
            return;
        }

        // Try to write as parsed JSON; fall back to a string so we never lose data.
        try
        {
            using var doc = JsonDocument.Parse(raw);
            writer.WritePropertyName(propertyName);
            doc.RootElement.WriteTo(writer);
        }
        catch
        {
            writer.WriteString(propertyName, raw);
        }
    }

    private static string BuildFallback(string rawBody, string? contentType)
    {
        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        writer.WriteStartArray("responses");
        writer.WriteStartObject();
        writer.WriteString("kind", "response");
        if (!string.IsNullOrEmpty(contentType))
            writer.WriteString("contentType", contentType);
        writer.WriteString("body", rawBody ?? string.Empty);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private record SseEvent(string EventName, string Data);

    /// <summary>
    /// Parses an SSE stream into a list of (event, data) pairs.
    /// Multiple consecutive <c>data:</c> lines belonging to the same event are
    /// concatenated with a newline, per the SSE spec. Comment lines (starting
    /// with <c>:</c>) are ignored.
    /// </summary>
    private static List<SseEvent> ParseSseEvents(string rawBody)
    {
        var result   = new List<SseEvent>();
        var lines    = rawBody.Split('\n');
        string? currentEvent = null;
        StringBuilder? currentData = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                // blank line = dispatch event
                if (currentEvent is not null || currentData is not null)
                {
                    result.Add(new SseEvent(
                        currentEvent ?? "message",
                        currentData?.ToString() ?? string.Empty));
                }
                currentEvent = null;
                currentData  = null;
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue; // comment

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var chunk = line["data:".Length..];
                if (chunk.StartsWith(" ", StringComparison.Ordinal)) chunk = chunk[1..];
                if (currentData is null) currentData = new StringBuilder();
                else currentData.Append('\n');
                currentData.Append(chunk);
            }
            // other fields (id:, retry:) are ignored
        }

        // Flush trailing event if the stream didn't end with a blank line.
        if (currentEvent is not null || currentData is not null)
        {
            result.Add(new SseEvent(
                currentEvent ?? "message",
                currentData?.ToString() ?? string.Empty));
        }

        return result;
    }
}
