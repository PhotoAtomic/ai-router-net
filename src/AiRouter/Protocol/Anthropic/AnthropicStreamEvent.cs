using System.Text.Json;

namespace AiRouter.Protocol.Anthropic;

// Individual SSE events for Anthropic streaming.
// We keep them flat (optional fields) so deserialization is simple.

public class AnthropicStreamEvent
{
    public string Type { get; set; } = string.Empty;
}

public class AnthropicMessageStartEvent : AnthropicStreamEvent
{
    public AnthropicMessageResponse? Message { get; set; }
}

public class AnthropicContentBlockStartEvent : AnthropicStreamEvent
{
    public int Index { get; set; }
    public AnthropicContentBlock? ContentBlock { get; set; }
}

public class AnthropicContentBlockDeltaEvent : AnthropicStreamEvent
{
    public int Index { get; set; }
    public AnthropicContentBlock? Delta { get; set; }
}

public class AnthropicContentBlockStopEvent : AnthropicStreamEvent
{
    public int Index { get; set; }
}

public class AnthropicMessageDeltaEvent : AnthropicStreamEvent
{
    public AnthropicMessageDelta? Delta { get; set; }
    public AnthropicUsage? Usage { get; set; }
}

public class AnthropicMessageDelta
{
    public string? StopReason { get; set; }
    public string? StopSequence { get; set; }
}

public class AnthropicMessageStopEvent : AnthropicStreamEvent
{
}
