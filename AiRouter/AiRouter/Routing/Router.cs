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
                : string.Empty;
            Console.WriteLine($"         {r.Pattern,-40} → {r.BaseUrl}{procInfo}");
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
    public async Task HandleMessagesAsync(HttpContext ctx)
    {
        // Capture snapshot once; reload during this request has no effect on it.
        var snap = _snapshot;
        var req = ctx.Request;
        var res = ctx.Response;

        var requestTimestamp = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        // --- Read body -------------------------------------------------------
        string body;
        using (var sr = new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true))
            body = await sr.ReadToEndAsync();

        var requestSizeBytes = (long)Encoding.UTF8.GetByteCount(body);

        // --- Extract model ---------------------------------------------------
        string? model = ExtractModel(body);
        if (string.IsNullOrEmpty(model))
        {
            await WriteErrorAsync(res, 400, "missing 'model' field in request body");
            return;
        }

        // --- Find route ------------------------------------------------------
        var rule = FindRule(snap, model);
        if (rule is null)
        {
            await WriteErrorAsync(res, 404, $"No routing rule matched model '{model}'");
            return;
        }

        var targetUrl = rule.BaseUrl.TrimEnd('/') + MessagesPath;
        Console.WriteLine($"[route] {model} → {targetUrl}  (rule: {rule.Pattern})");

        // --- Replace model in body if ForceModel is configured ---------------
        if (!string.IsNullOrEmpty(rule.ForceModel))
        {
            body = ReplaceModel(body, rule.ForceModel);
            Console.WriteLine($"[route] model overridden: '{model}' → '{rule.ForceModel}'");
        }

        // --- Ensure managed process is running (if configured for this rule) -
        if (rule.ProcessManager is not null)
        {
            try
            {
                await rule.ProcessManager.EnsureRunningAsync(ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] Failed to start process for rule '{rule.Pattern}': {ex.Message}");
                await WriteErrorAsync(res, 503, $"Managed process failed to start: {ex.Message}");
                return;
            }
        }

        // --- Build upstream request ------------------------------------------
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
        upstreamReq.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var requestHeaders = CollectRequestHeaders(req.Headers);

        foreach (var (name, values) in req.Headers)
        {
            if (SkippedRequestHeaders.Contains(name)) continue;

            var nameLower = name.ToLowerInvariant();

            if (nameLower == "x-api-key" || nameLower == "authorization")
                continue;

            if (nameLower.StartsWith("content-"))
                continue;

            try { upstreamReq.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values); }
            catch { /* ignore malformed headers */ }
        }

        SetAuthHeader(upstreamReq, rule.BaseUrl, snap);

        // --- Send (streaming-aware) ------------------------------------------
        using var httpClient = CreateHttpClient();
        HttpResponseMessage upstreamRes;
        try
        {
            upstreamRes = await httpClient.SendAsync(
                upstreamReq,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[error] Upstream call failed: {ex.Message}");
            await WriteErrorAsync(res, 502, $"Upstream unreachable: {ex.Message}");
            return;
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

        // --- Log entry -------------------------------------------------------
        if (_logger is not null)
        {
            stopwatch.Stop();
            var responseTimestamp = DateTimeOffset.Now;
            var loggedResponseBody = isStream
                ? AiRouter.Logging.SsePostProcessor.Process(responseBody)
                : responseBody;
            var entry = new LogEntry(
                RequestTimestamp:  requestTimestamp,
                ResponseTimestamp: responseTimestamp,
                DurationMs:        stopwatch.Elapsed.TotalMilliseconds,
                Model:             model,
                MatchedRule:       rule.Pattern,
                TargetUrl:         targetUrl,
                StatusCode:        (int)upstreamRes.StatusCode,
                RequestSizeBytes:  requestSizeBytes,
                ResponseSizeBytes: responseSizeBytes,
                RequestHeaders:    requestHeaders,
                ResponseHeaders:   responseHeaders,
                RequestBody:       body,
                ResponseBody:      loggedResponseBody);
            await _logger.LogAsync(entry);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
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

    private static async Task WriteErrorAsync(HttpResponse res, int status, string message)
    {
        if (res.HasStarted) { Console.WriteLine($"[warn] Cannot write error ({status}): {message}"); return; }
        res.StatusCode = status;
        res.ContentType = "application/json";
        var body = JsonSerializer.Serialize(
            new ProxyErrorResponse(new ProxyErrorDetail("proxy_error", message)),
            AiRouterJsonContext.Default.ProxyErrorResponse);
        await res.WriteAsync(body);
    }

    public void Dispose() { /* ProcessManagers are owned by ProcessRegistry */ }
}
