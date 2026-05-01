using System.Text;
using System.Text.Json;

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
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        // Only list models that are currently loaded
                        string? statusValue = null;
                        if (item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object
                            && st.TryGetProperty("value", out var v))
                        {
                            statusValue = v.GetString();
                        }

                        if (!string.Equals(statusValue, "loaded", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (item.TryGetProperty("id", out var idEl))
                        {
                            var id = idEl.GetString();
                            if (!string.IsNullOrEmpty(id))
                                loaded.Add(id);
                        }
                    }
                }

                lock (_lock)
                {
                    if (!_loadedModels.TryGetValue(baseUrl, out var prev) || !prev.SequenceEqual(loaded))
                    {
                        _loadedModels[baseUrl] = loaded;
                        anyChanged = true;
                    }
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
