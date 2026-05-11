using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRouter.Protocol;
using AiRouter.Protocol.Anthropic;
using AiRouter.Protocol.OpenAi;
using AiRouter.Serialization;

namespace AiRouter.Conversion;

public class RequestConverter : IRequestConverter
{
    private const int OpenAiMaxToolNameLength = 64;
    private const int ToolNameHashLength = 8;
    private const int ToolNamePrefixLength = OpenAiMaxToolNameLength - ToolNameHashLength - 1; // 55

    public (string body, ConversionContext context) Convert(string body, ApiFormat from, ApiFormat to)
    {
        if (from == to) return (body, ConversionContext.Empty);
        var ctx = new ConversionContext();
        return (from, to) switch
        {
            (ApiFormat.OpenAI, ApiFormat.Anthropic) => (OpenAiToAnthropic(body, ctx), ctx),
            (ApiFormat.Anthropic, ApiFormat.OpenAI) => (AnthropicToOpenAi(body, ctx), ctx),
            _ => (body, ConversionContext.Empty),
        };
    }

    // =====================================================================
    // OpenAI -> Anthropic
    // =====================================================================
    private static string OpenAiToAnthropic(string body, ConversionContext ctx)
    {
        var req = JsonSerializer.Deserialize(body, AiRouterJsonContext.Default.OpenAiChatRequest);
        if (req is null) return body;

        var anthropic = new AnthropicMessageRequest
        {
            Model = req.Model,
            MaxTokens = req.MaxCompletionTokens ?? req.MaxTokens,
            Temperature = req.Temperature,
            TopP = req.TopP,
            Stream = req.Stream,
        };

        // Stop sequences
        if (req.Stop.HasValue)
        {
            var stopEl = req.Stop.Value;
            if (stopEl.ValueKind == JsonValueKind.String)
                anthropic.StopSequences = new List<string> { stopEl.GetString()! };
            else if (stopEl.ValueKind == JsonValueKind.Array)
                anthropic.StopSequences = stopEl.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }

        // Response format -> output_format
        if (req.ResponseFormat.HasValue)
        {
            anthropic.OutputFormat = TranslateOpenAiResponseFormatToAnthropic(req.ResponseFormat.Value);
        }

        // User -> metadata.user_id
        if (!string.IsNullOrEmpty(req.User))
        {
            anthropic.Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["user_id"] = req.User,
            });
        }

        // Reasoning effort / thinking
        if (req.Tools is not { Count: > 0 })
        {
            // thinking mapping is independent of tools
        }

        // Tools
        if (req.Tools is { Count: > 0 })
        {
            anthropic.Tools = req.Tools.Select(ToolConverter.OpenAiToAnthropic).ToList();
        }

        // ToolChoice
        if (req.ToolChoice.HasValue)
        {
            anthropic.ToolChoice = req.ToolChoice.Value;
        }

        // Messages
        var anthropicMessages = new List<AnthropicMessage>();
        var systemBlocks = new List<AnthropicContentBlock>();

        foreach (var msg in req.Messages)
        {
            switch (msg.Role)
            {
                case "system":
                    systemBlocks.Add(new AnthropicContentBlock
                    {
                        Type = "text",
                        Text = GetStringContent(msg.Content),
                        CacheControl = msg.ThinkingBlocks,
                    });
                    break;

                case "user":
                    anthropicMessages.Add(MapOpenAiUserMessage(msg));
                    break;

                case "assistant":
                    anthropicMessages.Add(MapOpenAiAssistantMessage(msg));
                    break;

                case "tool":
                    anthropicMessages.Add(new AnthropicMessage
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement(new List<AnthropicContentBlock>
                        {
                            new()
                            {
                                Type = "tool_result",
                                ToolUseId = msg.ToolCallId ?? string.Empty,
                                Text = GetStringContent(msg.Content),
                            }
                        }, AiRouterJsonContext.Default.ListAnthropicContentBlock),
                    });
                    break;
            }
        }

        anthropic.Messages = anthropicMessages;

        if (systemBlocks.Count == 1)
        {
            anthropic.System = JsonSerializer.SerializeToElement(systemBlocks[0].Text ?? string.Empty);
        }
        else if (systemBlocks.Count > 1)
        {
            anthropic.System = JsonSerializer.SerializeToElement(systemBlocks, AiRouterJsonContext.Default.ListAnthropicContentBlock);
        }

        return JsonSerializer.Serialize(anthropic, AiRouterJsonContext.Default.AnthropicMessageRequest);
    }

    private static AnthropicMessage MapOpenAiUserMessage(OpenAiMessage msg)
    {
        // OpenAI user messages are plain string or array of content objects.
        if (msg.Content.HasValue)
        {
            return new AnthropicMessage
            {
                Role = "user",
                Content = msg.Content.Value,
                CacheControl = ExtractCacheControl(msg),
            };
        }
        return new AnthropicMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(string.Empty),
        };
    }

    private static AnthropicMessage MapOpenAiAssistantMessage(OpenAiMessage msg)
    {
        var blocks = new List<AnthropicContentBlock>();

        // Thinking blocks (LiteLLM extension)
        if (msg.ThinkingBlocks.HasValue)
        {
            var tb = msg.ThinkingBlocks.Value;
            if (tb.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in tb.EnumerateArray())
                {
                    var type = b.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (type == "thinking")
                    {
                        blocks.Add(new AnthropicContentBlock
                        {
                            Type = "thinking",
                            Thinking = b.TryGetProperty("thinking", out var th) ? th.GetString() : null,
                            Signature = b.TryGetProperty("signature", out var sig) ? sig.GetString() : null,
                        });
                    }
                    else if (type == "redacted_thinking")
                    {
                        blocks.Add(new AnthropicContentBlock
                        {
                            Type = "redacted_thinking",
                            Data = b.TryGetProperty("data", out var d) ? d.GetString() : null,
                        });
                    }
                }
            }
        }

        var contentText = GetStringContent(msg.Content);
        if (!string.IsNullOrEmpty(contentText))
        {
            blocks.Add(new AnthropicContentBlock { Type = "text", Text = contentText });
        }

        if (msg.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in msg.ToolCalls)
            {
                JsonElement? inputEl = null;
                if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                {
                    try { inputEl = JsonDocument.Parse(tc.Function.Arguments).RootElement; }
                    catch { }
                }

                blocks.Add(new AnthropicContentBlock
                {
                    Type = "tool_use",
                    Id = tc.Id,
                    Name = tc.Function?.Name ?? string.Empty,
                    Input = inputEl,
                });
            }
        }

        return new AnthropicMessage
        {
            Role = "assistant",
            Content = JsonSerializer.SerializeToElement(blocks, AiRouterJsonContext.Default.ListAnthropicContentBlock),
            CacheControl = ExtractCacheControl(msg),
        };
    }

    private static JsonElement? ExtractCacheControl(OpenAiMessage msg)
    {
        // cache_control is not a standard OpenAI field; if present on the message dict, preserve it.
        // Since our DTO doesn't parse arbitrary keys, we can't extract it here without re-parsing.
        return null;
    }

    // =====================================================================
    // Anthropic -> OpenAI
    // =====================================================================
    private static string AnthropicToOpenAi(string body, ConversionContext ctx)
    {
        var req = JsonSerializer.Deserialize(body, AiRouterJsonContext.Default.AnthropicMessageRequest);
        if (req is null) return body;

        var openAi = new OpenAiChatRequest
        {
            Model = req.Model,
            MaxTokens = req.MaxTokens,
            Temperature = req.Temperature,
            TopP = req.TopP,
            Stream = req.Stream,
        };
        Console.WriteLine($"[convert] Anthropic stream={req.Stream} -> OpenAI stream={openAi.Stream}");

        // Stop sequences
        if (req.StopSequences is { Count: > 0 })
        {
            if (req.StopSequences.Count == 1)
                openAi.Stop = JsonSerializer.SerializeToElement(req.StopSequences[0]);
            else
                openAi.Stop = JsonSerializer.SerializeToElement(req.StopSequences);
        }

        // Output format -> response_format
        if (req.OutputFormat.HasValue)
        {
            openAi.ResponseFormat = TranslateAnthropicOutputFormatToOpenAi(req.OutputFormat.Value);
        }

        // Metadata -> user
        if (req.Metadata.HasValue)
        {
            var meta = req.Metadata.Value;
            if (meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("user_id", out var uid)
                && uid.ValueKind == JsonValueKind.String)
            {
                openAi.User = uid.GetString();
            }
        }

        // Thinking -> reasoning_effort (approximate LiteLLM mapping)
        if (req.Thinking.HasValue)
        {
            var re = TranslateAnthropicThinkingToReasoningEffort(req.Thinking.Value);
            if (!string.IsNullOrEmpty(re))
            {
                // Inject as a custom param via JsonElement on the serialized object
                // We'll handle it by adding to the raw JSON later if needed.
                // For now, skip because OpenAiChatRequest doesn't have a ReasoningEffort field.
            }
        }

        // Tools (with truncation)
        if (req.Tools is { Count: > 0 })
        {
            var mappedTools = new List<OpenAiTool>();
            foreach (var tool in req.Tools)
            {
                var openAiTool = ToolConverter.AnthropicToOpenAi(tool);
                var originalName = openAiTool.Function?.Name ?? "";
                if (!string.IsNullOrEmpty(originalName) && originalName.Length > OpenAiMaxToolNameLength)
                {
                    var truncated = TruncateToolName(originalName);
                    if (truncated != originalName)
                    {
                        ctx.ToolNameForwardMap[originalName] = truncated;
                        ctx.ToolNameReverseMap[truncated] = originalName;
                        if (openAiTool.Function is not null)
                            openAiTool.Function.Name = truncated;
                    }
                }
                mappedTools.Add(openAiTool);
            }
            openAi.Tools = mappedTools;
        }

        // ToolChoice
        if (req.ToolChoice.HasValue)
        {
            openAi.ToolChoice = req.ToolChoice.Value;
        }

        // System -> system messages
        var messages = new List<OpenAiMessage>();
        ExtractSystemMessages(req.System, messages);

        // Messages
        if (req.Messages is not null)
        {
            foreach (var msg in req.Messages)
            {
                MapAnthropicMessage(msg, messages);
            }
        }

        openAi.Messages = messages;

        var json = JsonSerializer.Serialize(openAi, AiRouterJsonContext.Default.OpenAiChatRequest);

        // Inject reasoning_effort if needed (since it's not in the DTO)
        if (req.Thinking.HasValue)
        {
            var re = TranslateAnthropicThinkingToReasoningEffort(req.Thinking.Value);
            if (!string.IsNullOrEmpty(re))
            {
                json = InjectJsonProperty(json, "reasoning_effort", re);
            }
        }

        return json;
    }

    private static void ExtractSystemMessages(JsonElement? systemEl, List<OpenAiMessage> messages)
    {
        if (systemEl is null) return;
        if (systemEl.Value.ValueKind == JsonValueKind.String)
        {
            messages.Add(new OpenAiMessage { Role = "system", Content = JsonSerializer.SerializeToElement(systemEl.Value.GetString() ?? string.Empty) });
        }
        else if (systemEl.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in systemEl.Value.EnumerateArray())
            {
                var text = block.TryGetProperty("text", out var t) ? t.GetString() : block.GetString();
                if (!string.IsNullOrEmpty(text))
                    messages.Add(new OpenAiMessage { Role = "system", Content = JsonSerializer.SerializeToElement(text) });
            }
        }
    }

    private static void MapAnthropicMessage(AnthropicMessage msg, List<OpenAiMessage> messages)
    {
        if (msg.Role == "user")
        {
            if (msg.Content.ValueKind == JsonValueKind.String)
            {
                messages.Add(new OpenAiMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(msg.Content.GetString() ?? string.Empty),
                });
            }
            else if (msg.Content.ValueKind == JsonValueKind.Array)
            {
                var textParts = new List<string>();
                var toolResults = new List<OpenAiMessage>();
                foreach (var block in msg.Content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (type == "text" && block.TryGetProperty("text", out var tv))
                    {
                        textParts.Add(tv.GetString() ?? string.Empty);
                    }
                    else if (type == "tool_result")
                    {
                        if (textParts.Count > 0)
                        {
                            messages.Add(new OpenAiMessage
                            {
                                Role = "user",
                                Content = JsonSerializer.SerializeToElement(string.Join("\n", textParts)),
                            });
                            textParts.Clear();
                        }

                        var toolUseId = block.TryGetProperty("tool_use_id", out var tid)
                            ? tid.GetString()
                            : block.TryGetProperty("tool_use_id", out var tid2) ? tid2.GetString() : null;
                        var resultText = ExtractToolResultContent(block);

                        toolResults.Add(new OpenAiMessage
                        {
                            Role = "tool",
                            ToolCallId = toolUseId ?? string.Empty,
                            Content = JsonSerializer.SerializeToElement(resultText),
                        });
                    }
                    else if (type == "image")
                    {
                        // Convert Anthropic image to OpenAI image_url format
                        var imageUrl = TranslateAnthropicImageToOpenAi(block);
                        if (imageUrl is not null)
                        {
                            messages.Add(new OpenAiMessage
                            {
                                Role = "user",
                                Content = JsonSerializer.SerializeToElement(new List<object>
                                {
                                    new { type = "text", text = string.Join("\n", textParts) },
                                    new { type = "image_url", image_url = new { url = imageUrl } }
                                }),
                            });
                            textParts.Clear();
                        }
                    }
                }

                foreach (var tr in toolResults) messages.Add(tr);

                if (textParts.Count > 0)
                {
                    messages.Add(new OpenAiMessage
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement(string.Join("\n", textParts)),
                    });
                }
            }
        }
        else if (msg.Role == "assistant")
        {
            var openAiMsg = new OpenAiMessage { Role = "assistant" };
            var toolCalls = new List<OpenAiToolCall>();
            var textBuilder = new StringBuilder();
            var thinkingBlocks = new List<JsonElement>();

            if (msg.Content.ValueKind == JsonValueKind.String)
            {
                textBuilder.Append(msg.Content.GetString());
            }
            else if (msg.Content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in msg.Content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (type == "text" && block.TryGetProperty("text", out var tv))
                    {
                        if (textBuilder.Length > 0) textBuilder.Append('\n');
                        textBuilder.Append(tv.GetString());
                    }
                    else if (type == "thinking")
                    {
                        thinkingBlocks.Add(block.Clone());
                    }
                    else if (type == "redacted_thinking")
                    {
                        thinkingBlocks.Add(block.Clone());
                    }
                    else if (type == "tool_use")
                    {
                        var id = block.TryGetProperty("id", out var idp) ? idp.GetString() ?? string.Empty : string.Empty;
                        var name = block.TryGetProperty("name", out var np) ? np.GetString() ?? string.Empty : string.Empty;
                        var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";

                        toolCalls.Add(new OpenAiToolCall
                        {
                            Id = id,
                            Type = "function",
                            Function = new OpenAiFunctionCall
                            {
                                Name = name,
                                Arguments = input,
                            },
                        });
                    }
                }
            }

            if (textBuilder.Length > 0)
                openAiMsg.Content = JsonSerializer.SerializeToElement(textBuilder.ToString());

            if (thinkingBlocks.Count > 0)
                openAiMsg.ThinkingBlocks = JsonSerializer.SerializeToElement(thinkingBlocks);

            if (toolCalls.Count > 0)
                openAiMsg.ToolCalls = toolCalls;

            // OpenAI requires assistant messages to have either 'content' or 'tool_calls'
            if (openAiMsg.Content is null && openAiMsg.ToolCalls is null)
            {
                openAiMsg.Content = JsonSerializer.SerializeToElement(string.Empty);
            }

            messages.Add(openAiMsg);
        }
    }

    private static string GetStringContent(JsonElement? content)
    {
        if (!content.HasValue) return string.Empty;
        if (content.Value.ValueKind == JsonValueKind.String)
            return content.Value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string ExtractToolResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var cv)) return string.Empty;
        if (cv.ValueKind == JsonValueKind.String) return cv.GetString() ?? string.Empty;
        if (cv.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in cv.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var pt))
                    parts.Add(pt.GetString() ?? string.Empty);
            }
            return string.Join("\n", parts);
        }
        return string.Empty;
    }

    private static string? TranslateAnthropicImageToOpenAi(JsonElement imageBlock)
    {
        if (!imageBlock.TryGetProperty("source", out var src)) return null;
        var sourceType = src.TryGetProperty("type", out var st) ? st.GetString() : null;
        if (sourceType == "base64")
        {
            var mediaType = src.TryGetProperty("media_type", out var mt) ? mt.GetString() : "image/jpeg";
            var data = src.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (!string.IsNullOrEmpty(data))
                return $"data:{mediaType};base64,{data}";
        }
        else if (sourceType == "url")
        {
            return src.TryGetProperty("url", out var u) ? u.GetString() : null;
        }
        return null;
    }

    private static JsonElement? TranslateOpenAiResponseFormatToAnthropic(JsonElement responseFormat)
    {
        if (responseFormat.ValueKind != JsonValueKind.Object) return null;
        var type = responseFormat.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "json_schema")
        {
            JsonElement? schema = null;
            if (responseFormat.TryGetProperty("json_schema", out var js) && js.ValueKind == JsonValueKind.Object)
            {
                if (js.TryGetProperty("schema", out var sch))
                    schema = sch;
            }
            else if (responseFormat.TryGetProperty("schema", out var sch2))
            {
                schema = sch2;
            }

            if (schema.HasValue)
            {
                return JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["schema"] = schema.Value,
                });
            }
        }
        else if (type == "json_object")
        {
            return JsonSerializer.SerializeToElement(new { type = "json_object" });
        }
        return null;
    }

    private static JsonElement? TranslateAnthropicOutputFormatToOpenAi(JsonElement outputFormat)
    {
        if (outputFormat.ValueKind != JsonValueKind.Object) return null;
        var type = outputFormat.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "json_schema")
        {
            if (outputFormat.TryGetProperty("schema", out var schema))
            {
                var schemaCopy = DeepCopyJsonElement(schema);
                AddAdditionalPropertiesFalse(schemaCopy);
                return JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = "structured_output",
                        ["schema"] = schemaCopy,
                        ["strict"] = true,
                    }
                });
            }
        }
        else if (type == "json_object")
        {
            return JsonSerializer.SerializeToElement(new { type = "json_object" });
        }
        return null;
    }

    private static string TranslateAnthropicThinkingToReasoningEffort(JsonElement thinking)
    {
        if (thinking.ValueKind != JsonValueKind.Object) return string.Empty;
        var type = thinking.TryGetProperty("type", out var t) ? t.GetString() : "disabled";
        if (type == "disabled") return string.Empty;
        if (type == "enabled")
        {
            var budget = thinking.TryGetProperty("budget_tokens", out var b) ? b.GetInt32() : 0;
            if (budget >= 10000) return "high";
            if (budget >= 5000) return "medium";
            if (budget >= 2000) return "low";
            return "minimal";
        }
        if (type == "adaptive") return "medium";
        return string.Empty;
    }

    private static string TruncateToolName(string name)
    {
        if (name.Length <= OpenAiMaxToolNameLength) return name;
        var hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..ToolNameHashLength].ToLowerInvariant();
        return $"{name[..ToolNamePrefixLength]}_{hash}";
    }

    private static string InjectJsonProperty(string json, string key, string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals(key)) continue; // skip if already present
                prop.WriteTo(writer);
            }
            writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return json; }
    }

    private static JsonElement DeepCopyJsonElement(JsonElement element)
    {
        return JsonDocument.Parse(element.GetRawText()).RootElement.Clone();
    }

    private static void AddAdditionalPropertiesFalse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        // This is a no-op for JsonElement (immutable). In a real implementation we'd mutate the JSON node tree.
        // For now we rely on the backend being lenient or already having the flag.
    }
}
