namespace AiRouter.Logging;

// One record per proxied request/response pair (serialised as JSONL)
public record LogEntry(
    DateTimeOffset RequestTimestamp,
    DateTimeOffset ResponseTimestamp,
    double DurationMs,
    string Model,
    string MatchedRule,
    string TargetUrl,
    int StatusCode,
    long RequestSizeBytes,
    long ResponseSizeBytes,
    Dictionary<string, string> RequestHeaders,
    Dictionary<string, string> ResponseHeaders,
    string RequestBody,
    string ResponseBody
);
