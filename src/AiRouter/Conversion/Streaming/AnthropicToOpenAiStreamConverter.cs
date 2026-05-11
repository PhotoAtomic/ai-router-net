using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRouter.Protocol.OpenAi;
using AiRouter.Serialization;

namespace AiRouter.Conversion.Streaming;

public static class AnthropicToOpenAiStreamConverter
{
    public static async IAsyncEnumerable<SseEvent> ConvertAsync(
        IAsyncEnumerable<SseEvent> source,
        ConversionContext? context = null)
    {
        var tools = new Dictionary<int, ToolAcc>();
        bool roleSent = false;
        string? lastModel = null;
        string? lastId = null;
        StringBuilder? thinkingBuffer = null;
        bool inThinkingBlock = false;
        bool inRedactedThinking = false;

        await foreach (var ev in source.ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(ev.Data)) continue;

            JsonElement root = default;
            bool parsed = false;
            try { root = JsonDocument.Parse(ev.Data).RootElement; parsed = true; }
            catch { }
            if (!parsed) { yield return ev; continue; }

            if (!root.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg))
                    {
                        lastId = msg.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                        lastModel = msg.TryGetProperty("model", out var mp) ? mp.GetString() : null;
                    }
                    if (!roleSent)
                    {
                        roleSent = true;
                        yield return MakeChunk(lastId, lastModel, new OpenAiStreamDelta { Role = "assistant" });
                    }
                    break;

                case "content_block_start":
                    if (!root.TryGetProperty("index", out var idxStart)) break;
                    var idxS = idxStart.GetInt32();
                    if (root.TryGetProperty("content_block", out var cb)
                        && cb.TryGetProperty("type", out var cbt))
                    {
                        var blockType = cbt.GetString();
                        if (blockType == "tool_use")
                        {
                            var toolName = cb.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                            if (context?.ToolNameForwardMap.TryGetValue(toolName, out var truncated) == true)
                                toolName = truncated;

                            tools[idxS] = new ToolAcc
                            {
                                Id = cb.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "",
                                Name = toolName,
                            };
                        }
                        else if (blockType == "thinking")
                        {
                            inThinkingBlock = true;
                            thinkingBuffer = new StringBuilder();
                        }
                        else if (blockType == "redacted_thinking")
                        {
                            inRedactedThinking = true;
                            var data = cb.TryGetProperty("data", out var rd) ? rd.GetString() ?? "" : "";
                            yield return MakeChunk(lastId, lastModel, new OpenAiStreamDelta
                            {
                                ThinkingBlocks = JsonSerializer.SerializeToElement(new[]
                                {
                                    new Dictionary<string, string> { ["type"] = "redacted_thinking", ["data"] = data }
                                }),
                            });
                        }
                    }
                    break;

                case "content_block_delta":
                    if (!root.TryGetProperty("index", out var idxDelta)) break;
                    var idxD = idxDelta.GetInt32();
                    if (!root.TryGetProperty("delta", out var delta)) break;
                    var dt = delta.TryGetProperty("type", out var dtp) ? dtp.GetString() : null;

                    if (dt == "text_delta" && delta.TryGetProperty("text", out var tv))
                    {
                        yield return MakeChunk(lastId, lastModel, new OpenAiStreamDelta { Content = tv.GetString() });
                    }
                    else if (dt == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
                    {
                        if (tools.TryGetValue(idxD, out var acc))
                            acc.Args.Append(pj.GetString());
                    }
                    else if (dt == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                    {
                        if (inThinkingBlock && thinkingBuffer is not null)
                        {
                            thinkingBuffer.Append(th.GetString());
                            yield return MakeChunk(lastId, lastModel, new OpenAiStreamDelta { ReasoningContent = th.GetString() });
                        }
                    }
                    break;

                case "content_block_stop":
                    if (!root.TryGetProperty("index", out var idxStop)) break;
                    var idxSt = idxStop.GetInt32();
                    if (tools.TryGetValue(idxSt, out var finished))
                    {
                        yield return MakeChunk(lastId, lastModel, new OpenAiStreamDelta
                        {
                            ToolCalls =
                            [
                                new OpenAiToolCall
                                {
                                    Index = idxSt,
                                    Id = finished.Id,
                                    Type = "function",
                                    Function = new OpenAiFunctionCall
                                    {
                                        Name = finished.Name,
                                        Arguments = finished.Args.ToString(),
                                    }
                                }
                            ]
                        });
                        tools.Remove(idxSt);
                    }
                    else if (inThinkingBlock)
                    {
                        inThinkingBlock = false;
                        thinkingBuffer = null;
                    }
                    else if (inRedactedThinking)
                    {
                        inRedactedThinking = false;
                    }
                    break;

                case "message_delta":
                    string? finishReason = null;
                    if (root.TryGetProperty("delta", out var md)
                        && md.TryGetProperty("stop_reason", out var sr))
                    {
                        finishReason = MapStopReason(sr.GetString());
                    }
                    yield return MakeChunk(lastId, lastModel, new OpenAiStreamDelta(), finishReason);
                    break;

                case "message_stop":
                    yield return new SseEvent("message", "[DONE]");
                    break;
            }
        }
    }

    private static SseEvent MakeChunk(string? id, string? model, OpenAiStreamDelta delta, string? finishReason = null)
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = id ?? string.Empty,
            Object = "chat.completion.chunk",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model ?? string.Empty,
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = delta,
                    FinishReason = finishReason,
                }
            ]
        };
        var json = JsonSerializer.Serialize(chunk, AiRouterJsonContext.Default.OpenAiStreamChunk);
        return new SseEvent("message", json);
    }

    private static string? MapStopReason(string? reason)
    {
        return reason switch
        {
            "end_turn" => "stop",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            "tool_use" => "tool_calls",
            _ => reason,
        };
    }

    private sealed class ToolAcc
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Args { get; } = new();
    }
}
