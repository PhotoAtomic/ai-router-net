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
    public JsonElement                 Metadata { get; set; }  // raw — we parse it ourselves
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

/// <summary>
/// Parsed content of the <c>metadata.user_id</c> field.
/// Claude Code sends a JSON string in that field with device_id / account_uuid / session_id.
/// </summary>
public sealed class RequestMetadata
{
    public string? DeviceId    { get; set; }
    public string? AccountUuid { get; set; }
    public string? SessionId   { get; set; }

    /// <summary>Raw text of the user_id field before any JSON parsing attempt.</summary>
    public string? RawUserId   { get; set; }
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
            var req = JsonSerializer.Deserialize<AnthropicRequest>(json, _opts);
            if (req is null) return null;

            // The logged request body is the converted (upstream) payload.
            // When upstream expects OpenAI, tools have name/description inside "function".
            // If deserialization produced tools but all names are empty, fall back to OpenAI extraction.
            if (req.Tools is { Count: > 0 } && req.Tools.All(t => string.IsNullOrEmpty(t.Name)))
            {
                req.Tools = ExtractToolsFromOpenAi(json);
            }

            return req;
        }
        catch { return null; }
    }

    private static List<AnthropicTool> ExtractToolsFromOpenAi(string json)
    {
        var tools = new List<AnthropicTool>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tools", out var toolsEl)
                || toolsEl.ValueKind != JsonValueKind.Array)
                return tools;

            foreach (var tool in toolsEl.EnumerateArray())
            {
                string? name = null;
                string? description = null;

                if (tool.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var n)) name = n.GetString();
                    if (fn.TryGetProperty("description", out var d)) description = d.GetString();
                }
                else
                {
                    // Plain Anthropic-style object (fallback)
                    if (tool.TryGetProperty("name", out var n2)) name = n2.GetString();
                    if (tool.TryGetProperty("description", out var d2)) description = d2.GetString();
                }

                tools.Add(new AnthropicTool { Name = name, Description = description });
            }
        }
        catch { }
        return tools;
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

    /// <summary>
    /// Extracts the structured metadata from an <see cref="AnthropicRequest"/>.
    /// The <c>metadata.user_id</c> field is a JSON-encoded string, so we decode it twice.
    /// </summary>
    public static RequestMetadata? ParseMetadata(AnthropicRequest req)
    {
        var meta = req.Metadata;
        if (meta.ValueKind == JsonValueKind.Undefined || meta.ValueKind == JsonValueKind.Null)
            return null;

        // user_id is a JSON string whose *value* is itself a JSON object
        if (!meta.TryGetProperty("user_id", out var userIdEl)) return null;
        var rawUserId = userIdEl.ValueKind == JsonValueKind.String
            ? userIdEl.GetString()
            : userIdEl.GetRawText();

        if (string.IsNullOrWhiteSpace(rawUserId))
            return new RequestMetadata { RawUserId = rawUserId };

        try
        {
            using var inner = JsonDocument.Parse(rawUserId);
            var root = inner.RootElement;
            return new RequestMetadata
            {
                RawUserId   = rawUserId,
                DeviceId    = root.TryGetProperty("device_id",    out var d) ? d.GetString() : null,
                AccountUuid = root.TryGetProperty("account_uuid", out var a) ? a.GetString() : null,
                SessionId   = root.TryGetProperty("session_id",   out var s) ? s.GetString() : null,
            };
        }
        catch
        {
            return new RequestMetadata { RawUserId = rawUserId };
        }
    }
}
