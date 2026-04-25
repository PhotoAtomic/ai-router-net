namespace AiRouter.Logging;

public enum LogEntryType
{
    Request,
    Response,
}

// One JSONL line per record. A full transaction is a pair of records sharing
// the same CorrelationId: a "Request" written when the request arrives and a
// "Response" written when the upstream response has been fully received
// (or when the router decides to fail the request).
public record LogEntry(
    Guid           CorrelationId,
    LogEntryType   Type,
    DateTimeOffset Timestamp,

    // ---- Request-only fields (null on Response entries) ----
    string?                      Model            = null,
    string?                      MatchedRule      = null,
    string?                      TargetUrl        = null,
    long?                        RequestSizeBytes = null,
    Dictionary<string, string>?  RequestHeaders   = null,
    string?                      RequestBody      = null,

    // ---- Response-only fields (null on Request entries) ----
    int?                         StatusCode        = null,
    double?                      DurationMs        = null,
    long?                        ResponseSizeBytes = null,
    Dictionary<string, string>?  ResponseHeaders   = null,
    string?                      ResponseBody      = null,

    // ---- Recovery diagnostics (Response entries only) ----
    // Free-form lines describing every recovery attempt the router performed
    // because the upstream initially returned a 500. The request may still
    // have eventually succeeded — these lines exist purely to surface the
    // transient failure in the dashboard.
    List<string>?                RecoveryAttempts  = null
);
