using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiRouter.Protocol.Anthropic;
using AiRouter.Serialization;

namespace AiRouter.Conversion.Streaming;

public static class OpenAiToAnthropicStreamConverter
{
    public static async IAsyncEnumerable<SseEvent> ConvertAsync(
        IAsyncEnumerable<SseEvent> source,
        [EnumeratorCancellation] CancellationToken ct = default,
        ConversionContext? context = null)
    {
        bool messageStarted = false;
        int nextBlockIndex = 0;
        int? textIndex = null;
        int? thinkingIndex = null;
        int toolOffset = -1;
        var toolIndicesStarted = new HashSet<int>();
        string? lastId = null;
        string? lastModel = null;

        await foreach (var ev in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) yield break;

            // OpenAI sends [DONE] as raw data line
            if (ev.Data.Trim() == "[DONE]")
            {
                // Close any open text block
                if (textIndex.HasValue)
                {
                    yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{textIndex.Value}}}");
                    textIndex = null;
                }
                // Close any open thinking block
                if (thinkingIndex.HasValue)
                {
                    yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{thinkingIndex.Value}}}");
                    thinkingIndex = null;
                }
                foreach (var idx in toolIndicesStarted)
                {
                    yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{idx}}}");
                }
                toolIndicesStarted.Clear();
                yield return AnthropicEvent("message_stop", "{\"type\":\"message_stop\"}");
                continue;
            }

            if (string.IsNullOrEmpty(ev.Data)) continue;

            JsonElement root = default;
            bool parsed = false;
            try { root = JsonDocument.Parse(ev.Data).RootElement; parsed = true; }
            catch { }
            if (!parsed) { yield return ev; continue; }

            if (!root.TryGetProperty("choices", out var choicesArr)
                || choicesArr.ValueKind != JsonValueKind.Array
                || choicesArr.GetArrayLength() == 0)
                continue;

            var choice = choicesArr[0];
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            // Emit message_start on first valid chunk
            if (!messageStarted)
            {
                messageStarted = true;
                lastId = root.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                lastModel = root.TryGetProperty("model", out var mp) ? mp.GetString() : null;

                var startMsg = new AnthropicMessageResponse
                {
                    Id = lastId ?? string.Empty,
                    Type = "message",
                    Role = "assistant",
                    Model = lastModel ?? string.Empty,
                };
                var json = JsonSerializer.Serialize(startMsg, AiRouterJsonContext.Default.AnthropicMessageResponse);
                yield return AnthropicEvent("message_start", $"{{\"type\":\"message_start\",\"message\":{json}}}");
            }

            // Reasoning content / thinking blocks
            var reasoningText = ExtractThinkingFromDelta(delta);
            if (!string.IsNullOrEmpty(reasoningText))
            {
                if (!thinkingIndex.HasValue)
                {
                    thinkingIndex = nextBlockIndex++;
                    yield return AnthropicEvent("content_block_start",
                        $"{{\"type\":\"content_block_start\",\"index\":{thinkingIndex.Value},\"content_block\":{{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}}}");
                }
                yield return AnthropicEvent("content_block_delta",
                    $"{{\"type\":\"content_block_delta\",\"index\":{thinkingIndex.Value},\"delta\":{{\"type\":\"thinking_delta\",\"thinking\":{JsonEncodedText.Encode(reasoningText).ToString()}}}}}");
            }

            if (delta.TryGetProperty("thinking_blocks", out var tbProp) && tbProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in tbProp.EnumerateArray())
                {
                    var ttype = b.TryGetProperty("type", out var ttp) ? ttp.GetString() : null;
                    if (ttype == "thinking")
                    {
                        var thinking = b.TryGetProperty("thinking", out var thp) ? thp.GetString() : null;
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            if (!thinkingIndex.HasValue)
                            {
                                thinkingIndex = nextBlockIndex++;
                                var sig = b.TryGetProperty("signature", out var sp) ? sp.GetString() ?? "" : "";
                                yield return AnthropicEvent("content_block_start",
                                    $"{{\"type\":\"content_block_start\",\"index\":{thinkingIndex.Value},\"content_block\":{{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":{JsonEncodedText.Encode(sig).ToString()}}}}}");
                            }
                            yield return AnthropicEvent("content_block_delta",
                                $"{{\"type\":\"content_block_delta\",\"index\":{thinkingIndex.Value},\"delta\":{{\"type\":\"thinking_delta\",\"thinking\":{JsonEncodedText.Encode(thinking).ToString()}}}}}");
                        }
                    }
                    else if (ttype == "redacted_thinking")
                    {
                        var data = b.TryGetProperty("data", out var dp) ? dp.GetString() ?? "" : "";
                        var idx = nextBlockIndex++;
                        yield return AnthropicEvent("content_block_start",
                            $"{{\"type\":\"content_block_start\",\"index\":{idx},\"content_block\":{{\"type\":\"redacted_thinking\",\"data\":{JsonEncodedText.Encode(data).ToString()}}}}}");
                        yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{idx}}}");
                    }
                }
            }

            // Text content
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind != JsonValueKind.Null)
            {
                var text = contentProp.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    if (!textIndex.HasValue)
                    {
                        textIndex = nextBlockIndex++;
                        yield return AnthropicEvent("content_block_start",
                            $"{{\"type\":\"content_block_start\",\"index\":{textIndex.Value},\"content_block\":{{\"type\":\"text\",\"text\":\"\"}}}}");
                    }
                    yield return AnthropicEvent("content_block_delta",
                        $"{{\"type\":\"content_block_delta\",\"index\":{textIndex.Value},\"delta\":{{\"type\":\"text_delta\",\"text\":{JsonEncodedText.Encode(text).ToString()}}}}}");
                }
            }

            // Tool calls
            if (delta.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
            {
                if (toolOffset < 0) toolOffset = nextBlockIndex;

                foreach (var tc in tcArr.EnumerateArray())
                {
                    if (!tc.TryGetProperty("index", out var idxProp)) continue;
                    var openAiIdx = idxProp.GetInt32();
                    var idx = toolOffset + openAiIdx;

                    if (!toolIndicesStarted.Contains(idx))
                    {
                        toolIndicesStarted.Add(idx);
                        var id = tc.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
                        var name = tc.TryGetProperty("function", out var fn)
                            && fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                        if (context?.ToolNameReverseMap.TryGetValue(name, out var original) == true)
                            name = original;

                        yield return AnthropicEvent("content_block_start",
                            $"{{\"type\":\"content_block_start\",\"index\":{idx},\"content_block\":{{\"type\":\"tool_use\",\"id\":{JsonEncodedText.Encode(id).ToString()},\"name\":{JsonEncodedText.Encode(name).ToString()},\"input\":{{}}}}}}");
                    }

                    if (tc.TryGetProperty("function", out var fn2)
                        && fn2.TryGetProperty("arguments", out var argsProp))
                    {
                        var args = argsProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(args))
                        {
                            yield return AnthropicEvent("content_block_delta",
                                $"{{\"type\":\"content_block_delta\",\"index\":{idx},\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonEncodedText.Encode(args).ToString()}}}}}");
                        }
                    }
                }
            }

            // Finish reason
            if (choice.TryGetProperty("finish_reason", out var frProp)
                && frProp.ValueKind != JsonValueKind.Null)
            {
                var fr = frProp.GetString();
                if (!string.IsNullOrEmpty(fr))
                {
                    if (textIndex.HasValue)
                    {
                        yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{textIndex.Value}}}");
                        textIndex = null;
                    }
                    if (thinkingIndex.HasValue)
                    {
                        yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{thinkingIndex.Value}}}");
                        thinkingIndex = null;
                    }
                    foreach (var idx in toolIndicesStarted)
                    {
                        yield return AnthropicEvent("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{idx}}}");
                    }
                    toolIndicesStarted.Clear();

                    var stopReason = MapFinishReason(fr);
                    yield return AnthropicEvent("message_delta",
                        $"{{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":{JsonEncodedText.Encode(stopReason).ToString()}}}}}");
                    yield return AnthropicEvent("message_stop", "{\"type\":\"message_stop\"}");
                }
            }
        }
    }

    private static SseEvent AnthropicEvent(string name, string data)
        => new(name, data);

    private static string ExtractThinkingFromDelta(JsonElement delta)
    {
        foreach (var key in new[] { "reasoning_content", "thinking", "thought" })
        {
            if (delta.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var text = prop.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    Console.WriteLine($"[stream-debug] found thinking field '{key}': {text[..Math.Min(80, text.Length)]}...");
                    return text;
                }
            }
        }
        return string.Empty;
    }

    private static string MapFinishReason(string? reason)
    {
        return reason switch
        {
            "stop" => "end_turn",
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            _ => reason ?? "end_turn",
        };
    }
}
