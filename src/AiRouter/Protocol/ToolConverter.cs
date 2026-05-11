using AiRouter.Protocol.Anthropic;
using AiRouter.Protocol.OpenAi;

namespace AiRouter.Protocol;

public static class ToolConverter
{
    public static OpenAiTool AnthropicToOpenAi(AnthropicTool tool)
    {
        return new OpenAiTool
        {
            Type = "function",
            Function = new OpenAiFunctionDef
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.InputSchema,
            },
        };
    }

    public static AnthropicTool OpenAiToAnthropic(OpenAiTool tool)
    {
        return new AnthropicTool
        {
            Name = tool.Function?.Name ?? string.Empty,
            Description = tool.Function?.Description,
            InputSchema = tool.Function?.Parameters,
        };
    }
}
