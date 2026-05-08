using System.Text;
using System.Text.Json;
using AiRouter.Process;

namespace AiRouter.Routing;

// Polls /models on each llama.cpp server (rules with IsLLamaCpp = true) every second
// and exposes the list of currently-loaded model names per base URL.
// Also allows callers to unload a model via POST /models/unload.
internal sealed class LlamaCppMonitorService : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly Lock _lock = new();
    private List<string> _llamaBaseUrls = [];

    // baseUrl -> list of loaded model ids
    private readonly Dictionary<string, List<string>> _loadedModels = new();

    // baseUrl -> list of all available model ids (loaded or not)
    private readonly Dictionary<string, List<string>> _allModels = new();

    // baseUrl -> process config (for termination)
    private readonly Dictionary<string, ProcessConfig> _processConfigs = new();

    // baseUrl -> isOwned flag
    private readonly Dictionary<string, bool> _processIsOwned = new();

    private Timer? _timer;

    public event Action? Changed;

    public void UpdateRules(IEnumerable<RoutingRule> rules)
    {
        var urls = rules
            .Where(r => r.IsLLamaCpp)
            .Select(r => r.BaseUrl.TrimEnd('/'))
            .Distinct()
            .ToList();

        lock (_lock)
        {
            _llamaBaseUrls = urls;
            // Remove stale entries
            foreach (var key in _loadedModels.Keys.Except(urls).ToList())
                _loadedModels.Remove(key);
            foreach (var key in _allModels.Keys.Except(urls).ToList())
                _allModels.Remove(key);
            foreach (var key in _processConfigs.Keys.Except(urls).ToList())
                _processConfigs.Remove(key);

            //// Update process configs for current URLs
            //foreach (var rule in rules.Where(r => r.IsLLamaCpp))
            //{
            //    var url = rule.BaseUrl.TrimEnd('/');
            //    if (rule.Process is not null)
            //    {
            //        _processConfigs[url] = rule.Process;
            //        // Track if the process is owned (only if ProcessManager exists and is owned)
            //        _processIsOwned[url] = rule.ProcessManager is not null && rule.ProcessManager.IsOwned;
            //    }
            //}
        }
    }

    public void Start()
    {
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    // Returns a snapshot of loaded models per base URL for all monitored servers.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetSnapshot()
    {
        lock (_lock)
        {
            return _loadedModels.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<string>)kvp.Value.ToList());
        }
    }

    // Returns all available models (not just loaded) for the given baseUrl
    public IReadOnlyList<string> GetAllModels(string baseUrl)
    {
        lock (_lock)
        {
            if (_allModels.TryGetValue(baseUrl.TrimEnd('/'), out var models))
                return models.ToList();
            return [];
        }
    }

    // Returns the process config for the given baseUrl (if any)
    public ProcessConfig? GetProcessConfig(string baseUrl)
    {
        lock (_lock)
        {
            if (_processConfigs.TryGetValue(baseUrl.TrimEnd('/'), out var config))
                return config;
            return null;
        }
    }

    // Returns whether the process for the given baseUrl is owned by this instance
    public bool IsProcessOwned(string baseUrl)
    {
        lock (_lock)
        {
            // The dictionary keys are stored with trailing slashes trimmed
            // Use TryGetValue directly since the Razor page passes trimmed baseUrl
            if (!_processIsOwned.TryGetValue(baseUrl, out var isOwned))
                return false;
            return isOwned;
        }
    }

    public async Task<bool> UnloadAsync(string baseUrl, string modelId, CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["model"] = modelId });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{baseUrl}/models/unload", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Kills the process associated with the given baseUrl (if any)
    // Returns true if a process was found and killed
    public async Task<bool> KillProcessAsync(string baseUrl, ProcessRegistry registry, CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');
        ProcessConfig? config;
        lock (_lock)
        {
            _processConfigs.TryGetValue(baseUrl, out config);
        }

        if (config is null)
            return false;

        var key = ProcessRegistry.MakeKey(config);
        await registry.KillProcessAsync(key);
        return true;
    }

    private async Task PollAsync()
    {
        List<string> urlsSnapshot;
        lock (_lock) { urlsSnapshot = [.. _llamaBaseUrls]; }

        bool anyChanged = false;
        foreach (var baseUrl in urlsSnapshot)
        {
            try
            {
                using var resp = await _http.GetAsync($"{baseUrl}/models", CancellationToken.None);
                if (!resp.IsSuccessStatusCode) continue;

                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                var loaded = new List<string>();
                var all = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        string? idValue = null;
                        if (item.TryGetProperty("id", out var idEl))
                        {
                            idValue = idEl.GetString();
                            if (!string.IsNullOrEmpty(idValue))
                            {
                                all.Add(idValue);
                                // Also check if loaded
                                string? statusValue = null;
                                if (item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object
                                    && st.TryGetProperty("value", out var v))
                                {
                                    statusValue = v.GetString();
                                }

                                if (!string.Equals(statusValue, "loaded", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                loaded.Add(idValue);
                            }
                        }
                    }
                }

                lock (_lock)
                {
                    bool loadedChanged = !_loadedModels.TryGetValue(baseUrl, out var prevLoaded) || !prevLoaded.SequenceEqual(loaded);
                    bool allChanged = !_allModels.TryGetValue(baseUrl, out var prevAll) || !prevAll.SequenceEqual(all);

                    if (loadedChanged)
                        _loadedModels[baseUrl] = loaded;
                    if (allChanged)
                        _allModels[baseUrl] = all;

                    if (loadedChanged || allChanged)
                        anyChanged = true;
                }
            }
            catch
            {
                lock (_lock)
                {
                    if (_loadedModels.TryGetValue(baseUrl, out var prev) && prev.Count > 0)
                    {
                        _loadedModels[baseUrl] = [];
                        anyChanged = true;
                    }
                    if (_allModels.TryGetValue(baseUrl, out var prevAll) && prevAll.Count > 0)
                    {
                        _allModels[baseUrl] = [];
                        anyChanged = true;
                    }
                }
            }
        }

        if (anyChanged)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}
