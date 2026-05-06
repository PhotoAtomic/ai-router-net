using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiRouter.Logging;
using AiRouter.Serialization;

namespace AiRouter.Routing;

class Router : IDisposable
{
    private const string AnthropicHost = "api.anthropic.com";
    private const string MessagesPath = "/v1/messages";

    // Headers that must not be forwarded from the client to the upstream
    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "transfer-encoding", "connection",
        "keep-alive", "te", "trailer", "upgrade"
    };

    // Response headers that must not be copied back to the client
    private static readonly HashSet<string> SkippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer-encoding", "connection", "keep-alive"
    };

    // Snapshot is swapped atomically; in-flight requests hold a local reference
    // grabbed at the start of HandleMessagesAsync, so reloads never break them.
    private volatile RouterSnapshot _snapshot;
    private readonly RequestLogger? _logger;

    public Router(RouterSnapshot snapshot, RequestLogger? logger = null)
    {
        _snapshot = snapshot;
        _logger = logger;
    }

    // Hot-reload: called whenever appsettings.json changes.
    public void Reload(RouterSnapshot newSnapshot)
    {
        _snapshot = newSnapshot;
        Console.WriteLine("[config] Routing rules reloaded.");
        PrintRules(newSnapshot);
    }

    public static void PrintRules(RouterSnapshot snap)
    {
        Console.WriteLine("[config] Active rules (first match wins):");
        foreach (var r in snap.Rules)
        {
            var procInfo = r.Process is not null
                ? $"  [process: {r.Process.FileName} {r.Process.Arguments}]"
                : "  [process: <none>]";
            var mgrInfo = r.ProcessManager is not null ? "  [mgr: ok]" : "  [mgr: <null>]";
            Console.WriteLine($"         {r.Pattern,-40} → {r.BaseUrl}{procInfo}{mgrInfo}");
        }
    }

    // Returns the first rule whose regex matches modelName, using the current snapshot.
    private static RoutingRule? FindRule(RouterSnapshot snap, string modelName)
    {
        foreach (var rule in snap.Rules)
        {
            if (rule.CompiledRegex?.IsMatch(modelName) == true)
                return rule;
        }
        return null;
    }

    // Main entry point for /v1/messages
    public async Task HandleGenericAsync(HttpContext ctx)
    {
        // Capture snapshot once; reload during this request has no effect on it.
        var snap = _snapshot;
        var req = ctx.Request;
        var res = ctx.Response;

        // UUID v7 — time-ordered, sortable; correlates the Request and Response log entries.
        var correlationId = Guid.CreateVersion7();
        var requestTimestamp = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        // --- Read body -------------------------------------------------------
        string body;
        using (var sr = new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true))
            body = await sr.ReadToEndAsync();

        var requestSizeBytes = (long)Encoding.UTF8.GetByteCount(body);
        var requestHeaders   = CollectRequestHeaders(req.Headers);

        // --- Extract model (may be null on malformed requests) ---------------
        string? model = ExtractModel(body);

        // --- Find route (may be null) ----------------------------------------
        var rule = !string.IsNullOrEmpty(model) ? FindRule(snap, model) : snap.Rules.LastOrDefault();
        var targetUrl = rule is not null ? rule.BaseUrl.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString : null;

        // --- Apply ForceModel BEFORE logging the Request so the body we log --
        // matches what the upstream will actually receive. ---------------------
        if (rule is not null && !string.IsNullOrEmpty(rule.ForceModel) && !string.IsNullOrWhiteSpace(body))
        {
            body = ReplaceModel(body, rule.ForceModel);
            requestSizeBytes = (long)Encoding.UTF8.GetByteCount(body);
        }

        // --- Log the Request entry IMMEDIATELY (before any upstream work) ----
        await LogRequestAsync(
            correlationId,
            requestTimestamp,
            model ?? string.Empty,
            rule?.Pattern ?? string.Empty,
            targetUrl ?? string.Empty,
            requestSizeBytes,
            requestHeaders,
            body);

        // --- Validate ---------------------------------------------------------
        if (rule is null || targetUrl is null)
        {
            // No applicable rule found (either no rules or no match)
            await WriteErrorAndLogAsync(res, 404, "No routing rule matched the request",
                correlationId, stopwatch);
            return;
        }

        Console.WriteLine($"[route] {model} → {targetUrl}  (rule: {rule.Pattern})");
        if (!string.IsNullOrEmpty(rule.ForceModel))
            Console.WriteLine($"[route] model overridden: '{model}' → '{rule.ForceModel}'");
        if (rule.Process is null)
            Console.WriteLine($"[route] rule '{rule.Pattern}' has NO Process configured (rule.Process == null).");
        if (rule.ProcessManager is null)
            Console.WriteLine($"[route] rule '{rule.Pattern}' has NO ProcessManager attached (rule.ProcessManager == null).");

        // --- Ensure managed process is running (if configured for this rule) -
        // We do TWO things here, in this order:
        //   1. EnsureRunningAsync — guarantees that the OS process exists (or
        //      gets launched). After this returns, the *process* is alive but
        //      the upstream HTTP server inside it may not yet be bound to its
        //      port (e.g. llama-server.exe needs a few seconds before it
        //      starts listening).
        //   2. WaitForUpstreamReadyAsync — actively probes {baseUrl}/models
        //      (with fallback on {baseUrl}) until ANY HTTP response is
        //      received, which means the server is listening. Without this,
        //      the very first request after a cold start would hit a
        //      connection-refused error and return 502 to the client even
        //      though we just spawned the process correctly.
        // The probe is cheap when the upstream is already up: the very first
        // GET succeeds immediately and we move on.
        using var readinessHttp = CreateHttpClient();
        if (rule.ProcessManager is not null)
        {
            Console.WriteLine($"[proc] Rule '{rule.Pattern}' has a managed process configured ({rule.Process?.FileName} {rule.Process?.Arguments}). Ensuring it is running…");
            try
            {
                await rule.ProcessManager.EnsureRunningAsync(ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] Failed to start process for rule '{rule.Pattern}': {ex.Message}");
                await WriteErrorAndLogAsync(res, 503, $"Managed process failed to start: {ex.Message}",
                    correlationId, stopwatch);
                return;
            }

            var readyTimeout = TimeSpan.FromSeconds(60);
            var ready = await WaitForUpstreamReadyAsync(
                readinessHttp, rule.BaseUrl, readyTimeout, ctx.RequestAborted);
            if (!ready)
            {
                Console.WriteLine($"[error] Upstream {rule.BaseUrl} did not become ready within {readyTimeout.TotalSeconds:F0}s.");
                await WriteErrorAndLogAsync(res, 503,
                    $"Upstream did not become ready within {readyTimeout.TotalSeconds:F0}s after starting the managed process.",
                    correlationId, stopwatch);
                return;
            }
        }

        // --- Build upstream request (factory: reusable for replay) -----------
        HttpRequestMessage BuildUpstreamRequest()
        {
            var method = new HttpMethod(ctx.Request.Method);
            var msg = new HttpRequestMessage(method, targetUrl);

            // Include body for methods that normally have payloads
            if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch || method == HttpMethod.Delete)
                msg.Content = new StringContent(body, Encoding.UTF8, "application/json");

            foreach (var (name, values) in req.Headers)
            {
                if (SkippedRequestHeaders.Contains(name)) continue;
                var nameLower = name.ToLowerInvariant();
                if (nameLower == "x-api-key" || nameLower == "authorization") continue;
                if (nameLower.StartsWith("content-")) continue;

                try { msg.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values); }
                catch { /* ignore malformed headers */ }
            }

            SetAuthHeader(msg, rule.BaseUrl, snap);
            return msg;
        }

        // --- Send (streaming-aware) ------------------------------------------
        using var httpClient = CreateHttpClient();
        HttpRequestMessage upstreamReq = BuildUpstreamRequest();
        HttpResponseMessage upstreamRes;
        List<string>? recoveryAttempts = null;
        try
        {
            upstreamRes = await httpClient.SendAsync(
                upstreamReq,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            upstreamReq.Dispose();
            // The upstream rejected the connection (process likely died between
            // the readiness probe and the send). If we own a ProcessManager,
            // try once more: ensure-running + readiness probe + single retry.
            if (rule.ProcessManager is not null)
            {
                Console.WriteLine($"[warn] Upstream call failed ({ex.Message}); ensuring process is running and retrying once…");
                try
                {
                    await rule.ProcessManager.EnsureRunningAsync(ctx.RequestAborted);
                    var retryReady = await WaitForUpstreamReadyAsync(
                        readinessHttp, rule.BaseUrl, TimeSpan.FromSeconds(60), ctx.RequestAborted);
                    if (retryReady)
                    {
                        upstreamReq = BuildUpstreamRequest();
                        upstreamRes = await httpClient.SendAsync(
                            upstreamReq,
                            HttpCompletionOption.ResponseHeadersRead,
                            ctx.RequestAborted);
                        goto upstreamSendDone;
                    }
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"[error] Retry after ensure+probe failed: {retryEx.Message}");
                }
            }

            Console.WriteLine($"[error] Upstream call failed: {ex.Message}");
            await WriteErrorAndLogAsync(res, 502, $"Upstream unreachable: {ex.Message}",
                correlationId, stopwatch);
            return;
        }
        upstreamSendDone:

        // --- llama.cpp model-crash recovery ----------------------------------
        // If the upstream returns 500 and the rule has the recovery flag on,
        // try to unload + reload the target model on llama.cpp and replay the
        // request transparently (up to 10 attempts with exponential backoff).
        if ((int)upstreamRes.StatusCode == 500 && rule.EnableLLamaCppModelRecover)
        {
            recoveryAttempts = new List<string>();
            var modelToRecover = !string.IsNullOrEmpty(rule.ForceModel) ? rule.ForceModel! : model;
            recoveryAttempts.Add($"Upstream returned 500 for model '{modelToRecover}'. Starting llama.cpp model recovery.");
            Console.WriteLine($"[recover] {modelToRecover}: upstream 500 → attempting llama.cpp model recovery…");

            // Drain & dispose the failed response body so we can reissue.
            try { _ = await upstreamRes.Content.ReadAsByteArrayAsync(ctx.RequestAborted); } catch { }
            var initialFailedResponse = upstreamRes;
            upstreamReq.Dispose();

            var postLoadDelay = rule.Process is not null
                ? TimeSpan.FromSeconds(rule.Process.StartupDelaySeconds)
                : TimeSpan.FromSeconds(2);

            var recovery = new LlamaCppRecoveryService(httpClient);
            var outcome = await recovery.TryRecoverAsync(
                rule.BaseUrl, modelToRecover, postLoadDelay, recoveryAttempts, ctx.RequestAborted);

            if (outcome == RecoveryOutcome.Recovered)
            {
                // Replay the original request: cold model can take a while.
                bool replayed = false;
                var delay = TimeSpan.FromSeconds(1);
                const int replayAttempts = 10;
                for (int i = 1; i <= replayAttempts; i++)
                {
                    HttpRequestMessage replayReq = BuildUpstreamRequest();
                    HttpResponseMessage? replayRes = null;
                    try
                    {
                        replayRes = await httpClient.SendAsync(
                            replayReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        recoveryAttempts.Add($"replay {i}/{replayAttempts} threw: {ex.Message}");
                        replayReq.Dispose();
                    }

                    if (replayRes is not null)
                    {
                        if ((int)replayRes.StatusCode != 500)
                        {
                            recoveryAttempts.Add($"replay {i}/{replayAttempts} → status {(int)replayRes.StatusCode}, accepting.");
                            initialFailedResponse.Dispose();
                            upstreamReq = replayReq;
                            upstreamRes = replayRes;
                            replayed = true;
                            break;
                        }
                        recoveryAttempts.Add($"replay {i}/{replayAttempts} → 500.");
                        try { _ = await replayRes.Content.ReadAsByteArrayAsync(ctx.RequestAborted); } catch { }
                        replayRes.Dispose();
                        replayReq.Dispose();
                    }

                    if (i < replayAttempts)
                    {
                        try { await Task.Delay(delay, ctx.RequestAborted); }
                        catch (OperationCanceledException) { break; }
                        delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
                    }
                }

                if (!replayed)
                {
                    recoveryAttempts.Add("All replay attempts exhausted; surfacing original 500 to client.");
                    Console.WriteLine($"[recover] {modelToRecover}: replay attempts exhausted, returning original 500.");
                    upstreamReq = BuildUpstreamRequest(); // dummy holder to keep using-pattern below clean
                    upstreamRes = initialFailedResponse;
                }
            }
            else
            {
                recoveryAttempts.Add($"Recovery outcome: {outcome}. Surfacing original 500 to client.");
                Console.WriteLine($"[recover] {modelToRecover}: recovery {outcome}, returning original 500.");
                upstreamReq = BuildUpstreamRequest();
                upstreamRes = initialFailedResponse;
            }
        }

        // --- Copy response headers -------------------------------------------
        res.StatusCode = (int)upstreamRes.StatusCode;

        foreach (var (name, values) in upstreamRes.Headers)
        {
            if (SkippedResponseHeaders.Contains(name)) continue;
            res.Headers.Append(name, values.ToArray());
        }
        foreach (var (name, values) in upstreamRes.Content.Headers)
        {
            if (SkippedResponseHeaders.Contains(name)) continue;
            res.Headers.Append(name, values.ToArray());
        }

        var responseHeaders = CollectResponseHeaders(upstreamRes);

        // --- Stream body back to client (tee into buffer for logging) --------
        var contentType = upstreamRes.Content.Headers.ContentType?.MediaType ?? string.Empty;
        bool isStream = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        string responseBody;
        long responseSizeBytes;

        if (isStream)
        {
            res.Headers.ContentType = "text/event-stream";
            res.Headers.CacheControl = "no-cache";
            res.Headers.Connection = "keep-alive";

            await using var upstreamStream = await upstreamRes.Content.ReadAsStreamAsync();
            var logBuffer = new MemoryStream();
            var buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = await upstreamStream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                await res.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ctx.RequestAborted);
                await res.Body.FlushAsync(ctx.RequestAborted);
                await logBuffer.WriteAsync(buffer.AsMemory(0, bytesRead), ctx.RequestAborted);
            }
            responseSizeBytes = logBuffer.Length;
            responseBody = Encoding.UTF8.GetString(logBuffer.ToArray());
        }
        else
        {
            var rawBytes = await upstreamRes.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            responseSizeBytes = rawBytes.LongLength;
            responseBody = Encoding.UTF8.GetString(rawBytes);
            await res.Body.WriteAsync(rawBytes, ctx.RequestAborted);
        }

        // --- Log Response entry ----------------------------------------------
        stopwatch.Stop();
        // Persist the upstream payload losslessly so the dashboard can inspect
        // every detail (in particular tool_use requests). For SSE we keep one
        // entry per event; for single responses we keep the original body.
        var loggedResponseBody = AiRouter.Logging.ResponseBodyBuilder.Build(
            responseBody, contentType, isStream);

        await LogResponseAsync(
            correlationId,
            (int)upstreamRes.StatusCode,
            stopwatch.Elapsed.TotalMilliseconds,
            responseSizeBytes,
            responseHeaders,
            loggedResponseBody,
            recoveryAttempts);

        upstreamReq.Dispose();
        upstreamRes.Dispose();
    }

    // -------------------------------------------------------------------------
    // Logging helpers
    // -------------------------------------------------------------------------

    private Task LogRequestAsync(
        Guid correlationId,
        DateTimeOffset timestamp,
        string model,
        string matchedRule,
        string targetUrl,
        long requestSizeBytes,
        Dictionary<string, string> requestHeaders,
        string requestBody)
    {
        if (_logger is null) return Task.CompletedTask;
        var entry = new LogEntry(
            CorrelationId:    correlationId,
            Type:             LogEntryType.Request,
            Timestamp:        timestamp,
            Model:            model,
            MatchedRule:      matchedRule,
            TargetUrl:        targetUrl,
            RequestSizeBytes: requestSizeBytes,
            RequestHeaders:   requestHeaders,
            RequestBody:      requestBody);
        return _logger.LogAsync(entry);
    }

    private Task LogResponseAsync(
        Guid correlationId,
        int statusCode,
        double durationMs,
        long responseSizeBytes,
        Dictionary<string, string> responseHeaders,
        string responseBody,
        List<string>? recoveryAttempts = null)
    {
        if (_logger is null) return Task.CompletedTask;
        var entry = new LogEntry(
            CorrelationId:     correlationId,
            Type:              LogEntryType.Response,
            Timestamp:         DateTimeOffset.Now,
            StatusCode:        statusCode,
            DurationMs:        durationMs,
            ResponseSizeBytes: responseSizeBytes,
            ResponseHeaders:   responseHeaders,
            ResponseBody:      responseBody,
            RecoveryAttempts:  recoveryAttempts is { Count: > 0 } ? recoveryAttempts : null);
        return _logger.LogAsync(entry);
    }

    // Writes an error response to the client AND emits the matching Response log entry.
    private async Task WriteErrorAndLogAsync(
        HttpResponse res,
        int status,
        string message,
        Guid correlationId,
        Stopwatch stopwatch)
    {
        var errorBody = JsonSerializer.Serialize(
            new ProxyErrorResponse(new ProxyErrorDetail("proxy_error", message)),
            AiRouterJsonContext.Default.ProxyErrorResponse);

        if (!res.HasStarted)
        {
            res.StatusCode = status;
            res.ContentType = "application/json";
            await res.WriteAsync(errorBody);
        }
        else
        {
            Console.WriteLine($"[warn] Cannot write error ({status}) — response already started: {message}");
        }

        stopwatch.Stop();
        await LogResponseAsync(
            correlationId,
            status,
            stopwatch.Elapsed.TotalMilliseconds,
            Encoding.UTF8.GetByteCount(errorBody),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "application/json",
            },
            errorBody);
    }

    // -------------------------------------------------------------------------
    // Helpers

    // Compatibility wrapper for legacy route
    public async Task HandleMessagesAsync(HttpContext ctx) => await HandleGenericAsync(ctx);

    // -------------------------------------------------------------------------

    private static Dictionary<string, string> CollectRequestHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            var lower = name.ToLowerInvariant();
            result[name] = lower is "x-api-key" or "authorization"
                ? "[redacted]"
                : string.Join(", ", (IEnumerable<string>)values);
        }
        return result;
    }

    private static Dictionary<string, string> CollectResponseHeaders(HttpResponseMessage upstreamRes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in upstreamRes.Headers)
            result[name] = string.Join(", ", values);
        foreach (var (name, values) in upstreamRes.Content.Headers)
            result[name] = string.Join(", ", values);
        return result;
    }

    private static void SetAuthHeader(HttpRequestMessage req, string baseUrl, RouterSnapshot snap)
    {
        var apiKey = ResolveApiKey(baseUrl, snap);
        if (string.IsNullOrEmpty(apiKey)) return;

        if (baseUrl.Contains(AnthropicHost, StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        else
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static string ResolveApiKey(string baseUrl, RouterSnapshot snap)
    {
        foreach (var (fragment, key) in snap.ApiKeys)
        {
            if (baseUrl.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return snap.DefaultApiKey;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    // Polls the upstream until ANY HTTP response is received (= the server is
    // accepting TCP connections and speaking HTTP), or until the timeout
    // elapses. We deliberately accept any status code here: a 404 / 405 / 500
    // still proves the server is alive. Only a connection refused / timeout
    // is treated as "not ready yet".
    private static async Task<bool> WaitForUpstreamReadyAsync(
        HttpClient http, string baseUrl, TimeSpan timeout, CancellationToken ct)
    {
        baseUrl = baseUrl.TrimEnd('/');
        // Try /models first (well-known on llama.cpp / OpenAI-compatible servers),
        // then the bare base URL as a fallback.
        var probeUrls = new[] { $"{baseUrl}/models", baseUrl };

        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(250);
        var probeTimeout = TimeSpan.FromSeconds(2);
        bool firstAttempt = true;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var url in probeUrls)
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(probeTimeout);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await http.SendAsync(
                        req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                    // Any HTTP response means the listener is up.
                    if (!firstAttempt)
                        Console.WriteLine($"[ready] Upstream {baseUrl} responded on probe ({(int)resp.StatusCode}).");
                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return false;
                }
                catch
                {
                    // Connection refused / DNS / timeout — server not ready yet.
                }
            }

            if (firstAttempt)
            {
                Console.WriteLine($"[ready] Waiting for upstream {baseUrl} to start accepting connections…");
                firstAttempt = false;
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return false; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
        }

        return false;
    }

    private static string? ExtractModel(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        }
        catch { return null; }
    }

    private static string ReplaceModel(string json, string newModel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var (key, value) in dict)
            {
                if (string.Equals(key, "model", StringComparison.OrdinalIgnoreCase))
                    writer.WriteString("model", newModel);
                else
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }

    public void Dispose() { /* ProcessManagers are owned by ProcessRegistry */ }
}
