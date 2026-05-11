namespace AiRouter.Conversion;

public sealed class ConversionContext
{
    /// <summary>
    /// Maps original Anthropic tool names → sanitized OpenAI names (when truncation occurred).
    /// </summary>
    public Dictionary<string, string> ToolNameForwardMap { get; } = new();

    /// <summary>
    /// Maps sanitized OpenAI tool names → original Anthropic names.
    /// </summary>
    public Dictionary<string, string> ToolNameReverseMap { get; } = new();

    public static readonly ConversionContext Empty = new();
}
