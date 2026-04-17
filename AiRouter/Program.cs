using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;

namespace AiRouter;

// ---------------------------------------------------------------------------
// Configuration helpers
// ---------------------------------------------------------------------------

class ConfigHelper
{
    // Resolves ${VAR_NAME} placeholders against IConfiguration (env vars / appsettings)
    public static string Resolve(string value, IConfiguration config)
    {
        if (value.StartsWith("${") && value.EndsWith("}"))
        {
            var name = value[2..^1];
            return config[name] ?? value;
        }
        return value;
    }

    public static Dictionary<string, string> ResolveAll(Dictionary<string, string> dict, IConfiguration config)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in dict)
            result[k] = Resolve(v, config);
        return result;
    }
}

// ---------------------------------------------------------------------------
// Routing model
// ---------------------------------------------------------------------------

class RoutingRule
{
    public string Pattern { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public Regex? CompiledRegex { get; set; }
}

// ---------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------

class Router
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

    private readonly List<RoutingRule> _rules;
    private readonly Dictionary<string, string> _apiKeys; // key = substring of BaseUrl
    private readonly string _defaultApiKey;

    public Router(List<RoutingRule> rules, Dictionary<string, string> apiKeys, string defaultApiKey)
    {
        _rules = rules;
        _apiKeys = apiKeys;
        _defaultApiKey = defaultApiKey;

        foreach (var rule in _rules)
        {
            try
            {
                rule.CompiledRegex = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"[config] Invalid regex '{rule.Pattern}': {ex.Message}");
                throw;
            }
        }
    }

    // Returns the first BaseUrl whose regex matches modelName, or null
    private RoutingRule? FindRule(string modelName)
    {
        foreach (var rule in _rules)
        {
            if (rule.CompiledRegex?.IsMatch(modelName) == true)
                return rule;
        }
        return null;
    }

    // Main entry point for /v1/messages
    public async Task HandleMessagesAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // --- Read body -------------------------------------------------------
        string body;
        using (var sr = new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true))
            body = await sr.ReadToEndAsync();

        // --- Extract model ---------------------------------------------------
        string? model = ExtractModel(body);
        if (string.IsNullOrEmpty(model))
        {
            await WriteErrorAsync(res, 400, "missing 'model' field in request body");
            return;
        }

        // --- Find route ------------------------------------------------------
        var rule = FindRule(model);
        if (rule is null)
        {
            await WriteErrorAsync(res, 404, $"No routing rule matched model '{model}'");
            return;
        }

        var targetUrl = rule.BaseUrl.TrimEnd('/') + MessagesPath;
        Console.WriteLine($"[route] {model} → {targetUrl}  (rule: {rule.Pattern})");

        // --- Build upstream request ------------------------------------------
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
        upstreamReq.Content = new StringContent(body, Encoding.UTF8, "application/json");

        // Forward original headers (skip hop-by-hop and content-* which belong to HttpContent)
        foreach (var (name, values) in req.Headers)
        {
            if (SkippedRequestHeaders.Contains(name)) continue;

            var nameLower = name.ToLowerInvariant();

            // Replace x-api-key / authorization with our resolved key
            if (nameLower == "x-api-key" || nameLower == "authorization")
                continue;

            if (nameLower.StartsWith("content-"))
            {
                // Already set by StringContent; skip to avoid duplicates
                continue;
            }

            try { upstreamReq.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values); }
            catch { /* ignore malformed headers */ }
        }

        // Set auth header appropriate for the target
        SetAuthHeader(upstreamReq, rule.BaseUrl);

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

        // --- Stream body back to client --------------------------------------
        var contentType = upstreamRes.Content.Headers.ContentType?.MediaType ?? string.Empty;
        bool isStream = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (isStream)
        {
            // SSE: flush each event as it arrives so Claude Code gets real-time tokens
            res.Headers.ContentType = "text/event-stream";
            res.Headers.CacheControl = "no-cache";
            res.Headers.Connection = "keep-alive";

            await using var upstreamStream = await upstreamRes.Content.ReadAsStreamAsync();
            var buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = await upstreamStream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                await res.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ctx.RequestAborted);
                await res.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        else
        {
            // Non-streaming: copy body as-is
            await using var upstreamStream = await upstreamRes.Content.ReadAsStreamAsync();
            await upstreamStream.CopyToAsync(res.Body, ctx.RequestAborted);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SetAuthHeader(HttpRequestMessage req, string baseUrl)
    {
        var apiKey = ResolveApiKey(baseUrl);
        if (string.IsNullOrEmpty(apiKey)) return;

        if (baseUrl.Contains(AnthropicHost, StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        else
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private string ResolveApiKey(string baseUrl)
    {
        foreach (var (fragment, key) in _apiKeys)
        {
            if (baseUrl.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return _defaultApiKey;
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

    private static async Task WriteErrorAsync(HttpResponse res, int status, string message)
    {
        if (res.HasStarted) { Console.WriteLine($"[warn] Cannot write error ({status}): {message}"); return; }
        res.StatusCode = status;
        res.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { error = new { type = "proxy_error", message } });
        await res.WriteAsync(body);
    }
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

class Program
{
    static async Task Main(string[] args)
    {
        // --- Configuration ---------------------------------------------------
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var rules = config.GetSection("RoutingRules").Get<List<RoutingRule>>()
            ?? throw new InvalidOperationException("RoutingRules missing from configuration");

        var apiKeysRaw = config.GetSection("ApiKeys").Get<Dictionary<string, string>>()
            ?? new Dictionary<string, string>();
        var apiKeys = ConfigHelper.ResolveAll(apiKeysRaw, config);

        var defaultApiKey = ConfigHelper.Resolve(config["DefaultApiKey"] ?? string.Empty, config);

        var host = config["Host"] ?? "http://0.0.0.0";
        var port = config["Port"] ?? "5000";
        var listenUrl = $"{host}:{port}";

        Console.WriteLine($"AiRouter starting on {listenUrl}");
        Console.WriteLine("Routing rules (first match wins):");
        foreach (var r in rules)
            Console.WriteLine($"  {r.Pattern,-40} → {r.BaseUrl}");
        Console.WriteLine();

        var router = new Router(rules, apiKeys, defaultApiKey);

        // --- Web host --------------------------------------------------------
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(listenUrl);
        // Disable request body size limit so large prompts are not rejected
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

        var app = builder.Build();

        // --- Routes ----------------------------------------------------------
        app.MapPost("/v1/messages", async (HttpContext ctx) =>
            await router.HandleMessagesAsync(ctx));

        await app.RunAsync();
    }
}

