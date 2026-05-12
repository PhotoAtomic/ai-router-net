namespace AiRouter.Process;

// ProcessRegistry – deduplicates ProcessManager instances across rules.
// Two rules sharing the same FileName+Arguments get the same manager,
// so only one OS process is ever launched for that executable.
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
            mgr = new ProcessManager(cfg);
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
    // by the new set of active configs. Managers still referenced are untouched.
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

    /// <summary>Kills the process manager for the given key (FileName|Arguments) if it exists and is owned</summary>
    public async Task KillProcessAsync(string key)
    {
        if (!_managers.TryGetValue(key, out var mgr))
            return;

        if (mgr.IsAlive && mgr.IsOwned)
        {
            Console.WriteLine($"[registry] Terminating process by request: {key}");
            await mgr.KillAsync();
        }
    }

    public async Task RefreshAllAsync()
    {
        foreach (var mgr in _managers.Values)
        {
            try { await mgr.RefreshAliveAsync(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[registry] RefreshAlive failed for a manager: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var mgr in _managers.Values)
            mgr.Dispose();
    }

    internal ProcessManager? GetManager(ProcessConfig cfg)
    {
        var key = MakeKey(cfg);
        _managers.TryGetValue(key, out var mgr);
        return mgr;
    }

    internal IReadOnlyList<string> GetLogs(ProcessConfig cfg)
    {
        var key = MakeKey(cfg);
        if (_managers.TryGetValue(key, out var mgr))
            return mgr.GetLogs();
        return new List<string>();
    }

    public static string MakeKey(ProcessConfig cfg) =>
        $"{cfg.FileName.Trim()}|{cfg.Arguments.Trim()}";
}
