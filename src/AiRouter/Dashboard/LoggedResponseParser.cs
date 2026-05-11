using System.Text;
using System.Text.Json;

namespace AiRouter.Dashboard;

/// <summary>
/// One entry inside the persisted <c>responses</c> array.
/// </summary>
public sealed class LoggedResponseEntry
{
    /// <summary>"event" (SSE) or "response" (single JSON / text reply).</summary>
    public string Kind { get; init; } = "response";

    /// <summary>SSE event name (only for <see cref="Kind"/> == "event").</summary>
    public string? EventName { get; init; }

    /// <summary>Content-type (only for <see cref="Kind"/> == "response").</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Raw payload as received from upstream. For JSON payloads this contains the
    /// re-serialized JSON text; for plain text it contains the text as-is.
    /// </summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>Parsed JSON document, when <see cref="Raw"/> was valid JSON; otherwise null.</summary>
    public JsonElement? Json { get; init; }
}

/// <summary>
/// Result of replaying a transaction's response array into a single, human-readable view.
/// </summary>
public sealed class ReconstructedResponse
{
    public string? Id          { get; init; }
    public string? Model       { get; init; }
    public string? Role        { get; init; }
    public string? StopReason  { get; init; }

    public string  Text        { get; init; } = string.Empty;
    public string  Thinking    { get; init; } = string.Empty;

    public List<ReconstructedToolUse> ToolUses { get; init; } = new();

    public long?   InputTokens          { get; init; }
    public long?   OutputTokens         { get; init; }
    public long?   CacheReadInputTokens { get; init; }
}

public sealed class ReconstructedToolUse
{
    public string? Id    { get; init; }
    public string? Name  { get; init; }
    /// <summary>Pretty-printed JSON of the tool input (assembled from input_json_delta chunks).</summary>
    public string  Input { get; init; } = string.Empty;
}

public static class LoggedResponseParser
{
    /// <summary>
    /// Parses the wrapped <c>{ "responses": [...] }</c> envelope written by the proxy.
    /// Returns an empty list on any error.
    /// </summary>
    public static List<LoggedResponseEntry> ParseEnvelope(string body)
    {
        var result = new List<LoggedResponseEntry>();
        if (string.IsNullOrWhiteSpace(body)) return result;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { return result; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("responses", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var el in arr.EnumerateArray())
            {
                var kind        = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "response" : "response";
                var eventName   = el.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                var contentType = el.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;

                JsonElement? jsonPayload = null;
                string raw = string.Empty;

                JsonElement payload = default;
                bool hasPayload = false;
                if (kind == "event" && el.TryGetProperty("data", out var d))
                {
                    payload = d;
                    hasPayload = true;
                }
                else if (el.TryGetProperty("body", out var b))
                {
                    payload = b;
                    hasPayload = true;
                }

                if (hasPayload)
                {
                    if (payload.ValueKind == JsonValueKind.String)
                    {
                        raw = payload.GetString() ?? string.Empty;
                        try
                        {
                            using var inner = JsonDocument.Parse(raw);
                            jsonPayload = inner.RootElement.Clone();
                        }
                        catch { /* keep raw only */ }
                    }
                    else if (payload.ValueKind != JsonValueKind.Undefined &&
                             payload.ValueKind != JsonValueKind.Null)
                    {
                        jsonPayload = payload.Clone();
                        raw         = payload.GetRawText();
                    }
                }

                result.Add(new LoggedResponseEntry
                {
                    Kind        = kind,
                    EventName   = eventName,
                    ContentType = contentType,
                    Raw         = raw,
                    Json        = jsonPayload,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Walks the entries and rebuilds the assistant message (text, thinking, tool calls, usage).
    /// Supports both Anthropic SSE deltas and a single non-stream JSON response.
    /// </summary>
    public static ReconstructedResponse Reconstruct(IReadOnlyList<LoggedResponseEntry> entries)
    {
        // single non-stream response shortcut
        if (entries.Count == 1 && entries[0].Kind == "response" && entries[0].Json is JsonElement single)
        {
            return ReconstructFromSingle(single);
        }

        // Detect if the first event looks like Anthropic or OpenAI
        foreach (var e in entries)
        {
            if (e.Json is not JsonElement root || root.ValueKind != JsonValueKind.Object) continue;
            // Anthropic events have "type" field; OpenAI chunks have "object" or "choices"
            if (root.TryGetProperty("type", out _))
                return ReconstructAnthropic(entries);
            if (root.TryGetProperty("object", out _))
                return ReconstructOpenAi(entries);
        }

        return new ReconstructedResponse();
    }

    private static ReconstructedResponse ReconstructAnthropic(IReadOnlyList<LoggedResponseEntry> entries)
    {
        string? id = null, model = null, role = null, stopReason = null;
        var text     = new StringBuilder();
        var thinking = new StringBuilder();
        long? inputTokens = null, outputTokens = null, cacheReadInputTokens = null;

        var pendingToolUses = new Dictionary<int, ToolUseBuilder>();
        var orderedToolUses = new List<ToolUseBuilder>();

        foreach (var e in entries)
        {
            if (e.Json is not JsonElement root) continue;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!root.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg))
                    {
                        id    ??= msg.TryGetProperty("id",    out var pid)   ? pid.GetString()   : null;
                        model ??= msg.TryGetProperty("model", out var pm)    ? pm.GetString()    : null;
                        role  ??= msg.TryGetProperty("role",  out var pr)    ? pr.GetString()    : null;
                        if (msg.TryGetProperty("usage", out var u))
                        {
                            if (u.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt64();
                            if (u.TryGetProperty("cache_read_input_tokens", out var cr)) cacheReadInputTokens = cr.GetInt64();
                        }
                    }
                    break;

                case "content_block_start":
                {
                    if (!root.TryGetProperty("index", out var idxEl)) break;
                    var idx = idxEl.GetInt32();
                    if (root.TryGetProperty("content_block", out var cb)
                        && cb.TryGetProperty("type", out var cbt)
                        && cbt.GetString() == "tool_use")
                    {
                        var tu = new ToolUseBuilder
                        {
                            Id   = cb.TryGetProperty("id", out var tid)   ? tid.GetString() : null,
                            Name = cb.TryGetProperty("name", out var tn)  ? tn.GetString() : null,
                        };
                        if (cb.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object)
                            tu.Json.Append(inp.GetRawText());
                        pendingToolUses[idx] = tu;
                        orderedToolUses.Add(tu);
                    }
                    break;
                }

                case "content_block_delta":
                {
                    if (!root.TryGetProperty("delta", out var delta)) break;
                    var dt = delta.TryGetProperty("type", out var dtp) ? dtp.GetString() : null;
                    if (dt == "text_delta" && delta.TryGetProperty("text", out var t))
                        text.Append(t.GetString());
                    else if (dt == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                        thinking.Append(th.GetString());
                    else if (dt == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
                    {
                        if (root.TryGetProperty("index", out var idx2)
                            && pendingToolUses.TryGetValue(idx2.GetInt32(), out var tu))
                        {
                            tu.Json.Append(pj.GetString());
                        }
                    }
                    break;
                }

                case "message_delta":
                    if (root.TryGetProperty("delta", out var md)
                        && md.TryGetProperty("stop_reason", out var sr))
                        stopReason = sr.GetString();
                    if (root.TryGetProperty("usage", out var u2))
                    {
                        if (u2.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt64();
                        if (u2.TryGetProperty("input_tokens",  out var it2)) inputTokens  = it2.GetInt64();
                    }
                    break;
            }
        }

        return new ReconstructedResponse
        {
            Id                   = id,
            Model                = model,
            Role                 = role,
            StopReason           = stopReason,
            Text                 = text.ToString(),
            Thinking             = thinking.ToString(),
            ToolUses             = orderedToolUses.ConvertAll(t => t.ToToolUse()),
            InputTokens          = inputTokens,
            OutputTokens         = outputTokens,
            CacheReadInputTokens = cacheReadInputTokens,
        };
    }

    private static ReconstructedResponse ReconstructOpenAi(IReadOnlyList<LoggedResponseEntry> entries)
    {
        string? id = null, model = null, stopReason = null;
        var text = new StringBuilder();
        var toolUses = new List<ReconstructedToolUse>();
        long? inputTokens = null, outputTokens = null;

        var pendingToolCalls = new Dictionary<int, OpenAiToolAcc>();

        foreach (var e in entries)
        {
            if (e.Json is not JsonElement root || root.ValueKind != JsonValueKind.Object) continue;

            id ??= root.TryGetProperty("id", out var idp) ? idp.GetString() : null;
            model ??= root.TryGetProperty("model", out var mp) ? mp.GetString() : null;

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                        text.Append(content.GetString());

                    if (delta.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcArr.EnumerateArray())
                        {
                            if (!tc.TryGetProperty("index", out var idxProp)) continue;
                            var idx = idxProp.GetInt32();

                            if (!pendingToolCalls.TryGetValue(idx, out var acc))
                            {
                                acc = new OpenAiToolAcc();
                                pendingToolCalls[idx] = acc;
                            }

                            if (tc.TryGetProperty("id", out var tid))
                                acc.Id = tid.GetString() ?? acc.Id;
                            if (tc.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var n))
                                    acc.Name = n.GetString() ?? acc.Name;
                                if (fn.TryGetProperty("arguments", out var a))
                                    acc.Arguments.Append(a.GetString());
                            }
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                    stopReason = fr.GetString();
            }
        }

        foreach (var acc in pendingToolCalls.Values)
        {
            toolUses.Add(new ReconstructedToolUse
            {
                Id = acc.Id,
                Name = acc.Name,
                Input = acc.Arguments.ToString(),
            });
        }

        return new ReconstructedResponse
        {
            Id           = id,
            Model        = model,
            Role         = "assistant",
            StopReason   = stopReason,
            Text         = text.ToString(),
            ToolUses     = toolUses,
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
        };
    }

    // -------------------------------------------------------------------------

    private static ReconstructedResponse ReconstructFromSingle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new ReconstructedResponse();

        // OpenAI non-streaming responses have "choices"; Anthropic have "type":"message" + "content" array.
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            return ReconstructOpenAiSingle(root);

        return ReconstructAnthropicSingle(root);
    }

    private static ReconstructedResponse ReconstructAnthropicSingle(JsonElement root)
    {
        string? id = null, model = null, role = null, stopReason = null;
        var text     = new StringBuilder();
        var thinking = new StringBuilder();
        var tools    = new List<ReconstructedToolUse>();
        long? inputTokens = null, outputTokens = null, cacheReadInputTokens = null;

        if (root.TryGetProperty("id", out var pid))         id         = pid.GetString();
        if (root.TryGetProperty("model", out var pm))       model      = pm.GetString();
        if (root.TryGetProperty("role", out var pr))        role       = pr.GetString();
        if (root.TryGetProperty("stop_reason", out var sr)) stopReason = sr.GetString();

        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens",            out var it)) inputTokens          = it.GetInt64();
            if (u.TryGetProperty("output_tokens",           out var ot)) outputTokens         = ot.GetInt64();
            if (u.TryGetProperty("cache_read_input_tokens", out var cr)) cacheReadInputTokens = cr.GetInt64();
        }

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var bt)) continue;
                var btype = bt.GetString();
                switch (btype)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var tt))
                            text.Append(tt.GetString());
                        break;
                    case "thinking":
                        if (block.TryGetProperty("thinking", out var th))
                            thinking.Append(th.GetString());
                        break;
                    case "tool_use":
                        tools.Add(new ReconstructedToolUse
                        {
                            Id    = block.TryGetProperty("id",    out var tid) ? tid.GetString() : null,
                            Name  = block.TryGetProperty("name",  out var tn)  ? tn.GetString()  : null,
                            Input = block.TryGetProperty("input", out var inp) ? PrettyPrint(inp) : string.Empty,
                        });
                        break;
                }
            }
        }

        return new ReconstructedResponse
        {
            Id                   = id,
            Model                = model,
            Role                 = role,
            StopReason           = stopReason,
            Text                 = text.ToString(),
            Thinking             = thinking.ToString(),
            ToolUses             = tools,
            InputTokens          = inputTokens,
            OutputTokens         = outputTokens,
            CacheReadInputTokens = cacheReadInputTokens,
        };
    }

    private static ReconstructedResponse ReconstructOpenAiSingle(JsonElement root)
    {
        string? id = null, model = null, stopReason = null;
        var text = new StringBuilder();
        var tools = new List<ReconstructedToolUse>();
        long? inputTokens = null, outputTokens = null;

        if (root.TryGetProperty("id", out var pid)) id = pid.GetString();
        if (root.TryGetProperty("model", out var pm)) model = pm.GetString();

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var s = content.GetString();
                        if (!string.IsNullOrEmpty(s)) text.Append(s);
                    }

                    if (msg.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcArr.EnumerateArray())
                        {
                            var toolId = tc.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                            string? toolName = null;
                            string? toolArgs = null;
                            if (tc.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var n)) toolName = n.GetString();
                                if (fn.TryGetProperty("arguments", out var a)) toolArgs = a.GetString();
                            }
                            tools.Add(new ReconstructedToolUse
                            {
                                Id = toolId,
                                Name = toolName,
                                Input = toolArgs ?? string.Empty,
                            });
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                    stopReason = fr.GetString();
            }
        }

        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var pt)) inputTokens = pt.GetInt64();
            if (u.TryGetProperty("completion_tokens", out var ct)) outputTokens = ct.GetInt64();
        }

        return new ReconstructedResponse
        {
            Id           = id,
            Model        = model,
            Role         = "assistant",
            StopReason   = stopReason,
            Text         = text.ToString(),
            ToolUses     = tools,
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
        };
    }

    private static string PrettyPrint(JsonElement el)
    {
        try
        {
            return JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return el.GetRawText(); }
    }

    private sealed class ToolUseBuilder
    {
        public string?       Id   { get; set; }
        public string?       Name { get; set; }
        public StringBuilder Json { get; } = new();

        public ReconstructedToolUse ToToolUse()
        {
            var raw = Json.ToString();
            string pretty = raw;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                }
                catch { /* keep raw — partial/unparseable JSON */ }
            }
            return new ReconstructedToolUse { Id = Id, Name = Name, Input = pretty };
        }
    }

    private sealed class OpenAiToolAcc
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
