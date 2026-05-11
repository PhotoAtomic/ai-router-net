using System.Text.Json;
using AiRouter.Protocol;
using AiRouter.Protocol.Anthropic;
using AiRouter.Protocol.OpenAi;
using AiRouter.Serialization;

namespace AiRouter.Conversion;

public class ResponseConverter : IResponseConverter
{
    public string Convert(string body, ApiFormat from, ApiFormat to, ConversionContext? context = null)
    {
        if (from == to) return body;

        return (from, to) switch
        {
            (ApiFormat.Anthropic, ApiFormat.OpenAI) => AnthropicToOpenAi(body, context),
            (ApiFormat.OpenAI, ApiFormat.Anthropic) => OpenAiToAnthropic(body, context),
            _ => body,
        };
    }

    // -------------------------------------------------------------------------
    // Anthropic -> OpenAI
    // -------------------------------------------------------------------------
    private static string AnthropicToOpenAi(string body, ConversionContext? ctx)
    {
        var res = JsonSerializer.Deserialize(body, AiRouterJsonContext.Default.AnthropicMessageResponse);
        if (res is null) return body;

        var textBuilder = new System.Text.StringBuilder();
        var toolCalls = new List<OpenAiToolCall>();
        var thinkingBlocks = new List<JsonElement>();

        if (res.Content is not null)
        {
            foreach (var block in res.Content)
            {
                switch (block.Type)
                {
                    case "text" when block.Text is not null:
                        if (textBuilder.Length > 0) textBuilder.Append('\n');
                        textBuilder.Append(block.Text);
                        break;

                    case "tool_use":
                        var toolName = block.Name ?? string.Empty;
                        if (ctx?.ToolNameForwardMap.TryGetValue(toolName, out var truncated) == true)
                            toolName = truncated;

                        toolCalls.Add(new OpenAiToolCall
                        {
                            Id = block.Id ?? string.Empty,
                            Type = "function",
                            Function = new OpenAiFunctionCall
                            {
                                Name = toolName,
                                Arguments = block.Input?.GetRawText() ?? "{}",
                            },
                        });
                        break;

                    case "thinking":
                        thinkingBlocks.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = block.Thinking ?? string.Empty,
                            ["signature"] = block.Signature ?? string.Empty,
                        }));
                        break;

                    case "redacted_thinking":
                        thinkingBlocks.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = block.Data ?? string.Empty,
                        }));
                        break;
                }
            }
        }

        var message = new OpenAiMessage
        {
            Role = "assistant",
            Content = textBuilder.Length > 0 ? JsonSerializer.SerializeToElement(textBuilder.ToString()) : null,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
        };

        if (thinkingBlocks.Count > 0)
        {
            message.ThinkingBlocks = JsonSerializer.SerializeToElement(thinkingBlocks);
        }

        var openAi = new OpenAiChatResponse
        {
            Id = res.Id,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = res.Model,
            Choices =
            [
                new OpenAiChoice
                {
                    Index = 0,
                    Message = message,
                    FinishReason = MapAnthropicStopReason(res.StopReason),
                }
            ],
            Usage = res.Usage is not null
                ? new OpenAiUsage
                {
                    PromptTokens = res.Usage.InputTokens,
                    CompletionTokens = res.Usage.OutputTokens,
                    TotalTokens = res.Usage.InputTokens + res.Usage.OutputTokens,
                }
                : null,
        };

        return JsonSerializer.Serialize(openAi, AiRouterJsonContext.Default.OpenAiChatResponse);
    }

    private static string GetStringContent(JsonElement? content)
    {
        if (!content.HasValue) return string.Empty;
        if (content.Value.ValueKind == JsonValueKind.String)
            return content.Value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string? MapAnthropicStopReason(string? reason)
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

    // -------------------------------------------------------------------------
    // OpenAI -> Anthropic
    // -------------------------------------------------------------------------
    private static string OpenAiToAnthropic(string body, ConversionContext? ctx)
    {
        var res = JsonSerializer.Deserialize(body, AiRouterJsonContext.Default.OpenAiChatResponse);
        if (res is null) return body;

        var choice = res.Choices.FirstOrDefault();
        if (choice is null) return body;

        var contentBlocks = new List<AnthropicContentBlock>();

        // reasoning_content (DeepSeek-style extension)
        if (!string.IsNullOrEmpty(choice.Message.ReasoningContent))
        {
            contentBlocks.Add(new AnthropicContentBlock
            {
                Type = "thinking",
                Thinking = choice.Message.ReasoningContent,
            });
        }

        // thinking_blocks (LiteLLM extension)
        if (choice.Message.ThinkingBlocks.HasValue)
        {
            var tb = choice.Message.ThinkingBlocks.Value;
            if (tb.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in tb.EnumerateArray())
                {
                    var type = b.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (type == "thinking")
                    {
                        contentBlocks.Add(new AnthropicContentBlock
                        {
                            Type = "thinking",
                            Thinking = b.TryGetProperty("thinking", out var th) ? th.GetString() : null,
                            Signature = b.TryGetProperty("signature", out var sig) ? sig.GetString() : null,
                        });
                    }
                    else if (type == "redacted_thinking")
                    {
                        contentBlocks.Add(new AnthropicContentBlock
                        {
                            Type = "redacted_thinking",
                            Data = b.TryGetProperty("data", out var d) ? d.GetString() : null,
                        });
                    }
                }
            }
        }

        var msgContent = GetStringContent(choice.Message.Content);
        if (!string.IsNullOrEmpty(msgContent))
        {
            contentBlocks.Add(new AnthropicContentBlock
            {
                Type = "text",
                Text = msgContent,
            });
        }

        if (choice.Message.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in choice.Message.ToolCalls)
            {
                JsonElement? inputEl = null;
                if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                {
                    try
                    {
                        inputEl = JsonDocument.Parse(tc.Function.Arguments).RootElement;
                    }
                    catch { }
                }

                var toolName = tc.Function?.Name ?? string.Empty;
                if (ctx?.ToolNameReverseMap.TryGetValue(toolName, out var original) == true)
                    toolName = original;

                contentBlocks.Add(new AnthropicContentBlock
                {
                    Type = "tool_use",
                    Id = tc.Id,
                    Name = toolName,
                    Input = inputEl,
                });
            }
        }

        var usage = choice.FinishReason is not null || res.Usage is not null
            ? new AnthropicUsage()
            : null;

        if (usage is not null && res.Usage is not null)
        {
            usage.InputTokens = res.Usage.PromptTokens;
            usage.OutputTokens = res.Usage.CompletionTokens;
        }

        var anthropic = new AnthropicMessageResponse
        {
            Id = res.Id,
            Model = res.Model,
            Content = contentBlocks,
            StopReason = MapOpenAiFinishReason(choice.FinishReason),
            Usage = usage,
        };

        return JsonSerializer.Serialize(anthropic, AiRouterJsonContext.Default.AnthropicMessageResponse);
    }

    private static string? MapOpenAiFinishReason(string? reason)
    {
        return reason switch
        {
            "stop" => "end_turn",
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            "content_filter" => "end_turn", // no direct equivalent
            _ => reason,
        };
    }
}
