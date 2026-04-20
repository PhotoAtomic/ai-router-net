using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;

// This application targets Windows only (published as win-x64 self-contained).
[assembly: SupportedOSPlatform("windows")]

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
// Process configuration (optional, per routing rule)
// ---------------------------------------------------------------------------

class ProcessConfig
{
    // Executable path, e.g. "pwsh" or "C:\llama\llama-server.exe"
    public string FileName { get; set; } = string.Empty;

    // Arguments string passed to the process, e.g. "-File C:\llama\start.ps1"
    public string Arguments { get; set; } = string.Empty;

    // Seconds to wait after a fresh start before forwarding the first request (default: 2)
    public int StartupDelaySeconds { get; set; } = 2;
}

// ---------------------------------------------------------------------------
// ProcessManager – owns one Process instance per RoutingRule
// ---------------------------------------------------------------------------

class ProcessManager : IDisposable
{
    private readonly ProcessConfig _cfg;
    private readonly string _label; // used in log output
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    // True when we attached to an already-running OS process rather than launching it.
    // A pre-existing process must never be killed by the router.
    private bool _wasPreExisting;

    public ProcessManager(ProcessConfig cfg, string label)
    {
        _cfg = cfg;
        _label = label;
    }

    // Ensures the process is running, starting/restarting if necessary.
    // Must be called before forwarding each request to this rule's backend.
    [SupportedOSPlatform("windows")]
    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (IsAlive) return;

            // Clean up any dead handle (only if we owned it)
            if (_process is not null)
            {
                if (_wasPreExisting)
                    Console.WriteLine($"[proc:{_label}] Pre-existing process exited — will try to find/start a new one.");
                else
                    Console.WriteLine($"[proc:{_label}] Process exited (code {SafeExitCode()}), restarting…");
                _process.Dispose();
                _process = null;
                _wasPreExisting = false;
            }

            // Check if the process is already running on the system.
            var existing = FindExistingProcess();
            if (existing is not null)
            {
                _process = existing;
                _wasPreExisting = true;
                existing.EnableRaisingEvents = true;
                existing.Exited += (_, _) =>
                    Console.WriteLine($"[proc:{_label}] Pre-existing process (PID {existing.Id}) exited.");
                Console.WriteLine($"[proc:{_label}] Found pre-existing '{_cfg.FileName}' (PID {existing.Id}), attaching — will NOT terminate on router exit.");
                return;
            }

            // Not found: launch it ourselves.
            _wasPreExisting = false;
            Console.WriteLine($"[proc:{_label}] Starting: {_cfg.FileName} {_cfg.Arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = _cfg.FileName,
                Arguments = _cfg.Arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // Stream stdout / stderr to the router console
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) Console.WriteLine($"[proc:{_label}] {e.Data}");
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) Console.WriteLine($"[proc:{_label}] ERR {e.Data}");
            };
            _process.Exited += (_, _) =>
                Console.WriteLine($"[proc:{_label}] Process exited with code {SafeExitCode()}");

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Console.WriteLine($"[proc:{_label}] Started (PID {_process.Id}), waiting {_cfg.StartupDelaySeconds}s for readiness…");
            await Task.Delay(TimeSpan.FromSeconds(_cfg.StartupDelaySeconds), ct);
            Console.WriteLine($"[proc:{_label}] Ready.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsAlive
    {
        get
        {
            if (_process is null) return false;
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    // Whether the router may kill this process (false for pre-existing ones).
    public bool IsOwned => !_wasPreExisting;

    public async Task KillAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!IsAlive) return;

            if (_wasPreExisting)
            {
                Console.WriteLine($"[proc:{_label}] Skipping kill — process (PID {_process!.Id}) was pre-existing and not owned by the router.");
                return;
            }

            Console.WriteLine($"[proc:{_label}] Killing process (PID {_process!.Id})…");
            try
            {
                _process!.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                Console.WriteLine($"[proc:{_label}] Killed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[proc:{_label}] Kill failed: {ex.Message}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // Looks up a running OS process whose executable path and arguments match the config.
    // Uses WMI Win32_Process to read the full command line of each candidate process.
    [SupportedOSPlatform("windows")]
    private Process? FindExistingProcess()
    {
        // Resolve the configured FileName to a full path so we can compare apples-to-apples.
        // If FileName has no directory component (e.g. "pwsh") we search PATH via Where.exe.
        var resolvedExe = ResolveExecutablePath(_cfg.FileName);

        try
        {
            // WMI query: fetch ExecutablePath and CommandLine for every running process.
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process");
            using var results = searcher.Get();

            foreach (ManagementObject mo in results)
            {
                var pid        = Convert.ToInt32(mo["ProcessId"]);
                var exePath    = mo["ExecutablePath"]  as string ?? string.Empty;
                var cmdLine    = mo["CommandLine"]     as string ?? string.Empty;

                if (!ExeMatches(exePath, resolvedExe)) continue;
                if (!ArgumentsMatch(cmdLine, exePath, _cfg.Arguments)) continue;

                try
                {
                    var proc = Process.GetProcessById(pid);
                    Console.WriteLine($"[proc:{_label}] Found pre-existing process matching '{_cfg.FileName} {_cfg.Arguments}' (PID {pid}).");
                    return proc;
                }
                catch { /* process exited between query and GetProcessById */ }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[proc:{_label}] Warning: WMI process scan failed: {ex.Message}");
        }

        return null;
    }

    // True when the WMI ExecutablePath matches the configured executable.
    [SupportedOSPlatform("windows")]
    private static bool ExeMatches(string wmiExePath, string? resolvedExe)
    {
        if (string.IsNullOrEmpty(wmiExePath)) return false;
        if (string.IsNullOrEmpty(resolvedExe)) return false;
        return string.Equals(
            Path.GetFullPath(wmiExePath),
            Path.GetFullPath(resolvedExe),
            StringComparison.OrdinalIgnoreCase);
    }

    // True when the WMI CommandLine contains the expected arguments.
    // CommandLine = "<exe-path>" <arguments>, so we strip the exe part first.
    [SupportedOSPlatform("windows")]
    private static bool ArgumentsMatch(string cmdLine, string exePath, string expectedArgs)
    {
        // Normalise whitespace for comparison
        expectedArgs = expectedArgs.Trim();

        // Strip the leading exe token from the command line.
        // The exe may appear quoted ("C:\path\exe.exe") or unquoted.
        string argsFromCmdLine;
        if (cmdLine.StartsWith('"'))
        {
            var closeQuote = cmdLine.IndexOf('"', 1);
            argsFromCmdLine = closeQuote >= 0
                ? cmdLine[(closeQuote + 1)..].TrimStart()
                : cmdLine;
        }
        else
        {
            var firstSpace = cmdLine.IndexOf(' ');
            argsFromCmdLine = firstSpace >= 0
                ? cmdLine[(firstSpace + 1)..].TrimStart()
                : string.Empty;
        }

        return string.Equals(argsFromCmdLine, expectedArgs, StringComparison.OrdinalIgnoreCase);
    }

    // Resolves a bare executable name (e.g. "pwsh") to its full path using PATH lookup.
    // Returns the original string if resolution fails or if it already looks absolute.
    private static string? ResolveExecutablePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (Path.IsPathRooted(fileName)) return fileName;

        // Try appending common extensions if missing
        var extensions = new[] { ".exe", ".cmd", ".bat", "" };
        var pathDirs   = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir, fileName + ext);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return fileName; // fallback: return as-is, ExeMatches will just not match
    }

    private string SafeExitCode()
    {
        try { return _process?.ExitCode.ToString() ?? "?"; }
        catch { return "?"; }
    }

    public void Dispose()
    {
        try { _process?.Dispose(); } catch { /* ignore */ }
        _lock.Dispose();
    }
}

// ---------------------------------------------------------------------------
// ProcessRegistry – deduplicates ProcessManager instances across rules
//   Two rules sharing the same FileName+Arguments get the same manager,
//   so only one OS process is ever launched for that executable.
// ---------------------------------------------------------------------------

class ProcessRegistry : IDisposable
{
    // Key = "FileName|Arguments" (case-insensitive, trimmed)
    private readonly Dictionary<string, ProcessManager> _managers =
        new(StringComparer.OrdinalIgnoreCase);

    public ProcessManager GetOrCreate(ProcessConfig cfg, string label)
    {
        var key = MakeKey(cfg);
        if (!_managers.TryGetValue(key, out var mgr))
        {
            mgr = new ProcessManager(cfg, label);
            _managers[key] = mgr;
        }
        return mgr;
    }

    public bool AnyAlive => _managers.Values.Any(m => m.IsAlive);
    public bool AnyOwnedAlive => _managers.Values.Any(m => m.IsAlive && m.IsOwned);

    public IReadOnlyCollection<ProcessManager> All => _managers.Values;

    public async Task KillAllAsync()
    {
        var owned = _managers.Values.Where(m => m.IsAlive && m.IsOwned).ToList();
        if (owned.Count == 0) return;
        Console.WriteLine($"[registry] Killing {owned.Count} owned process(es)…");
        await Task.WhenAll(owned.Select(m => m.KillAsync()));
    }

    // Kills and removes any manager whose process key is no longer referenced
    // by the new set of active configs.  Managers still referenced are untouched.
    public async Task RetireUnusedAsync(IEnumerable<ProcessConfig> activeConfigs)
    {
        var activeKeys = new HashSet<string>(
            activeConfigs.Select(MakeKey),
            StringComparer.OrdinalIgnoreCase);

        var orphanedKeys = _managers.Keys
            .Where(k => !activeKeys.Contains(k))
            .ToList();

        if (orphanedKeys.Count == 0) return;

        Console.WriteLine($"[registry] {orphanedKeys.Count} process(es) no longer referenced by any rule.");
        foreach (var key in orphanedKeys)
        {
            var mgr = _managers[key];
            _managers.Remove(key);
            if (mgr.IsAlive)
            {
                if (mgr.IsOwned)
                {
                    Console.WriteLine($"[registry] Terminating orphaned owned process: {key}");
                    await mgr.KillAsync();
                }
                else
                {
                    Console.WriteLine($"[registry] Detaching orphaned pre-existing process: {key} (not owned, left running).");
                }
            }
            mgr.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var mgr in _managers.Values)
            mgr.Dispose();
    }

    private static string MakeKey(ProcessConfig cfg) =>
        $"{cfg.FileName.Trim()}|{cfg.Arguments.Trim()}";
}

// ---------------------------------------------------------------------------
// Routing model
// ---------------------------------------------------------------------------

// Plain data class bound by IConfiguration — only simple/supported property types.
// Keeping Regex and ProcessManager out of this class eliminates SYSLIB1100/1101.
class RoutingRuleConfig
{
    public string Pattern { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ForceModel { get; set; }
    public ProcessConfig? Process { get; set; }
}

// Runtime wrapper: holds the compiled/resolved fields alongside the raw config.
class RoutingRule
{
    public RoutingRule(RoutingRuleConfig cfg, Regex regex, ProcessManager? mgr)
    {
        Config         = cfg;
        CompiledRegex  = regex;
        ProcessManager = mgr;
    }

    public RoutingRuleConfig Config  { get; }
    public string Pattern            => Config.Pattern;
    public string BaseUrl            => Config.BaseUrl;
    public string? ForceModel        => Config.ForceModel;
    public ProcessConfig? Process    => Config.Process;
    public Regex          CompiledRegex  { get; }
    public ProcessManager? ProcessManager { get; }
}

// ---------------------------------------------------------------------------
// Router configuration snapshot (immutable, swapped atomically on reload)
// ---------------------------------------------------------------------------

record RouterSnapshot(
    List<RoutingRule> Rules,
    Dictionary<string, string> ApiKeys,
    string DefaultApiKey);

// ---------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------

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

        // --- Log request (if logging is enabled) -----------------------------
        if (_logger is not null)
            await _logger.LogAsync(model, rule.Pattern, targetUrl, req.Headers, body);

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

    // Replaces the "model" field value in the JSON body with the given override.
    private static string ReplaceModel(string json, string newModel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value;

            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var (key, value) in dict)
            {
                if (string.Equals(key, "model", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("model", newModel);
                }
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
            return json; // fallback: leave body untouched
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

// ---------------------------------------------------------------------------
// Trim-safe JSON serialization for error responses
// ---------------------------------------------------------------------------

record ProxyErrorDetail(string type, string message);
record ProxyErrorResponse(ProxyErrorDetail error);

[JsonSerializable(typeof(ProxyErrorResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class AiRouterJsonContext : JsonSerializerContext { }

// ---------------------------------------------------------------------------
// RequestLogger – appends incoming request details to a log file
// ---------------------------------------------------------------------------

class RequestLogger : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Headers worth recording (excluding auth which is redacted, excluding noise)
    private static readonly HashSet<string> RelevantHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "anthropic-version", "anthropic-beta", "user-agent", "x-request-id"
    };

    public RequestLogger(string path)
    {
        _path = path;
        Console.WriteLine($"[log] Logging requests to: {path}");
    }

    public async Task LogAsync(string model, string matchedRule, string targetUrl,
        IHeaderDictionary headers, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- {DateTimeOffset.Now:O} ---");
        sb.AppendLine($"Model  : {model}");
        sb.AppendLine($"Rule   : {matchedRule}");
        sb.AppendLine($"Target : {targetUrl}");

        foreach (var (name, values) in headers)
        {
            var lower = name.ToLowerInvariant();
            if (lower is "x-api-key" or "authorization")
                sb.AppendLine($"{name}: [redacted]");
            else if (RelevantHeaders.Contains(lower))
                sb.AppendLine($"{name}: {string.Join(", ", (IEnumerable<string>)values)}");
        }

        sb.AppendLine("Body:");
        try
        {
            using var doc = JsonDocument.Parse(body);
            sb.AppendLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            sb.AppendLine(body);
        }
        sb.AppendLine();

        await _lock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_path, sb.ToString());
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
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

        var host = config["Host"] ?? "http://0.0.0.0";
        var port = config["Port"] ?? "5000";
        var listenUrl = $"{host}:{port}";

        var registry = new ProcessRegistry();
        var initialSnapshot = BuildSnapshot(config, registry);

        // --- Parse --log [file] from command line ----------------------------
        RequestLogger? requestLogger = null;
        var logIdx = Array.IndexOf(args, "--log");
        if (logIdx >= 0)
        {
            string logPath;
            if (logIdx + 1 < args.Length && !args[logIdx + 1].StartsWith('-'))
                logPath = args[logIdx + 1];
            else
                logPath = Path.Combine(
                    AppContext.BaseDirectory, "requests.log");
            requestLogger = new RequestLogger(logPath);
        }

        var router = new Router(initialSnapshot, requestLogger);

        Console.WriteLine($"AiRouter starting on {listenUrl}");
        Router.PrintRules(initialSnapshot);
        Console.WriteLine();

        // Live reload: rebuild snapshot whenever appsettings.json changes on disk.
        Microsoft.Extensions.Primitives.ChangeToken.OnChange(
            () => config.GetReloadToken(),
            () =>
            {
                Console.WriteLine("[config] Change detected, reloading configuration…");
                try
                {
                    var newSnapshot = BuildSnapshot(config, registry);

                    // Retire processes that are no longer referenced by any rule.
                    var activeConfigs = newSnapshot.Rules
                        .Where(r => r.Process is not null)
                        .Select(r => r.Process!);
                    // Fire-and-forget with error logging; reload must not block.
                    _ = registry.RetireUnusedAsync(activeConfigs).ContinueWith(t =>
                    {
                        if (t.Exception is not null)
                            Console.WriteLine($"[registry] RetireUnused error: {t.Exception.GetBaseException().Message}");
                    }, TaskScheduler.Default);

                    router.Reload(newSnapshot);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[config] Reload failed, keeping previous rules: {ex.Message}");
                }
            });

        // --- Web host --------------------------------------------------------
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(listenUrl);
        // Disable request body size limit so large prompts are not rejected
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

        var app = builder.Build();

        // --- Routes ----------------------------------------------------------
        app.MapPost("/v1/messages", async (HttpContext ctx) =>
            await router.HandleMessagesAsync(ctx));

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            router.Dispose();
            requestLogger?.Dispose();
        });

        // --- Background keyboard listener ------------------------------------
        Console.WriteLine("[keys] Ctrl+K = kill managed processes  |  Ctrl+U = kill processes + shut down router");
        Console.WriteLine();
        using var keyListenerCts = new CancellationTokenSource();
        var keyListenerTask = Task.Run(() => KeyListenerAsync(registry, keyListenerCts.Token));

        await app.RunAsync();

        // --- Shutdown: stop key listener -------------------------------------
        keyListenerCts.Cancel();
        try { await keyListenerTask; } catch { }

        // --- Ask user whether to terminate managed processes -----------------
        await AskAndKillAsync(registry);
        registry.Dispose();
    }

    // Runs on a background thread; polls for Ctrl+K and Ctrl+U
    static async Task KeyListenerAsync(ProcessRegistry registry, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.Key == ConsoleKey.K &&
                        (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("[keys] Ctrl+K — killing all owned managed processes…");
                        await registry.KillAllAsync();
                    }
                    else if (key.Key == ConsoleKey.U &&
                             (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        Console.WriteLine();
                        Console.Write("[keys] Ctrl+U — kill all managed processes and shut down the router? [Y/n]: ");
                        string? answer;
                        try { answer = Console.ReadLine(); }
                        catch { answer = "n"; }

                        if (!string.IsNullOrWhiteSpace(answer) &&
                            !answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine("[keys] Aborted.");
                        }
                        else
                        {
                            Console.WriteLine("[keys] Killing owned processes…");
                            await registry.KillAllAsync();
                            Console.WriteLine("[keys] Done. Router keeps running.");
                        }
                    }
                }
                else
                {
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* console not available (redirected I/O, etc.) — silently skip */ }
    }

    // Reads current IConfiguration and builds a fully compiled RouterSnapshot.
    static RouterSnapshot BuildSnapshot(IConfiguration config, ProcessRegistry registry)
    {
        var rawRules = config.GetSection("RoutingRules").Get<List<RoutingRuleConfig>>()
            ?? throw new InvalidOperationException("RoutingRules missing from configuration");

        var apiKeysRaw = config.GetSection("ApiKeys").Get<Dictionary<string, string>>()
            ?? new Dictionary<string, string>();
        var apiKeys = ConfigHelper.ResolveAll(apiKeysRaw, config);
        var defaultApiKey = ConfigHelper.Resolve(config["DefaultApiKey"] ?? string.Empty, config);

        var rules = rawRules.Select(cfg =>
        {
            var regex = new Regex(cfg.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var mgr   = cfg.Process is not null ? registry.GetOrCreate(cfg.Process, cfg.Pattern) : null;
            return new RoutingRule(cfg, regex, mgr);
        }).ToList();

        return new RouterSnapshot(rules, apiKeys, defaultApiKey);
    }

    // Called after the web host shuts down
    static async Task AskAndKillAsync(ProcessRegistry registry)
    {
        if (!registry.AnyOwnedAlive) return;

        Console.WriteLine();
        Console.Write("[shutdown] Managed processes started by the router are still running. Terminate them? [Y/n]: ");

        string? answer;
        try { answer = Console.ReadLine(); }
        catch { answer = "y"; }

        if (string.IsNullOrWhiteSpace(answer) ||
            answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            await registry.KillAllAsync();
        }
        else
        {
            Console.WriteLine("[shutdown] Managed processes left running.");
        }
    }
}

