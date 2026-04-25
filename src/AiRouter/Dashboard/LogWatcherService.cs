using AiRouter.Logging;
using AiRouter.Serialization;

namespace AiRouter.Dashboard;

/// <summary>
/// Singleton service that tails the JSONL log file and keeps an in-memory list
/// of <see cref="LogEntryViewModel"/> for the Blazor dashboard.
/// </summary>
public sealed class LogWatcherService : IDisposable
{
    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------

    /// <summary>All entries read so far, newest first.</summary>
    public IList<LogEntryViewModel> Entries => _entries;

    public double MaxDurationMs   { get; private set; }
    public long   MaxRequestBytes { get; private set; }
    public long   MaxResponseBytes{ get; private set; }

    /// <summary>Raised on the ThreadPool whenever entries change.</summary>
    public event Action? Changed;

    /// <summary>CorrelationId of the last entry opened in the detail view. Used to highlight it on back-navigation.</summary>
    public Guid? LastViewedId { get; set; }

    // -------------------------------------------------------------------------
    // Private fields
    // -------------------------------------------------------------------------

    private readonly string            _path;
    private readonly List<LogEntryViewModel> _entries = [];
    private readonly Dictionary<Guid, LogEntryViewModel> _byCorrelation = new();
    private readonly FileSystemWatcher _watcher;
    private readonly object            _lock = new();
    private long                       _readPosition;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public LogWatcherService(string logPath)
    {
        _path = logPath;

        // Do an initial load of whatever is already in the file.
        LoadInitial();

        var dir  = Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? ".";
        var file = Path.GetFileName(logPath);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
    }

    // -------------------------------------------------------------------------
    // File reading
    // -------------------------------------------------------------------------

    private void LoadInitial()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var fs   = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
                TryAddLine(line);
            _readPosition = fs.Position;
        }
        catch { /* file not readable yet */ }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            bool changed = false;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (_readPosition > fs.Length) _readPosition = 0; // file was truncated/replaced
            fs.Seek(_readPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (TryAddLine(line)) changed = true;
            }
            _readPosition = fs.Position;
            if (changed) Changed?.Invoke();
        }
        catch { /* swallow — file may be momentarily locked */ }
    }

    private bool TryAddLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var entry = System.Text.Json.JsonSerializer.Deserialize(line, AiRouterJsonContext.Default.LogEntry);
            if (entry is null) return false;

            lock (_lock)
            {
                if (entry.Type == LogEntryType.Request)
                {
                    if (_byCorrelation.TryGetValue(entry.CorrelationId, out var existing))
                    {
                        // Defensive: a duplicate Request shouldn't normally happen.
                        existing.ApplyRequest(entry);
                    }
                    else
                    {
                        var vm = LogEntryViewModel.FromRequest(entry);
                        _byCorrelation[entry.CorrelationId] = vm;
                        _entries.Insert(0, vm); // newest first
                    }
                }
                else // Response
                {
                    if (!_byCorrelation.TryGetValue(entry.CorrelationId, out var vm))
                    {
                        // Stand-alone Response (e.g. log was truncated): create a placeholder VM.
                        vm = new LogEntryViewModel { CorrelationId = entry.CorrelationId };
                        _byCorrelation[entry.CorrelationId] = vm;
                        _entries.Insert(0, vm);
                    }
                    vm.ApplyResponse(entry);

                    if (vm.DurationMs    > MaxDurationMs)    MaxDurationMs    = vm.DurationMs;
                    if (vm.RequestBytes  > MaxRequestBytes)  MaxRequestBytes  = vm.RequestBytes;
                    if (vm.ResponseBytes > MaxResponseBytes) MaxResponseBytes = vm.ResponseBytes;
                }
            }
            return true;
        }
        catch { return false; }
    }

    // -------------------------------------------------------------------------
    // Mutation helpers (called from the UI)
    // -------------------------------------------------------------------------

    /// <summary>Deletes all entries from memory and truncates the log file.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _entries.Clear();
            _byCorrelation.Clear();
            MaxDurationMs    = 0;
            MaxRequestBytes  = 0;
            MaxResponseBytes = 0;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;
            File.WriteAllText(_path, string.Empty);
            _readPosition = 0;
        }
        catch { /* best-effort */ }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Removes the specified correlation IDs from memory and rewrites the log file
    /// without those entries.
    /// </summary>
    public void RemoveEntries(IEnumerable<Guid> ids)
    {
        var set = new HashSet<Guid>(ids);
        if (set.Count == 0) return;

        lock (_lock)
        {
            foreach (var id in set)
            {
                _byCorrelation.Remove(id);
            }
            _entries.RemoveAll(e => set.Contains(e.CorrelationId));
            RecalcMaxima();
        }

        RewriteFile(set);
        Changed?.Invoke();
    }

    private void RecalcMaxima()
    {
        MaxDurationMs    = 0;
        MaxRequestBytes  = 0;
        MaxResponseBytes = 0;
        foreach (var vm in _entries)
        {
            if (vm.DurationMs    > MaxDurationMs)    MaxDurationMs    = vm.DurationMs;
            if (vm.RequestBytes  > MaxRequestBytes)  MaxRequestBytes  = vm.RequestBytes;
            if (vm.ResponseBytes > MaxResponseBytes) MaxResponseBytes = vm.ResponseBytes;
        }
    }

    private void RewriteFile(HashSet<Guid> removedIds)
    {
        try
        {
            _watcher.EnableRaisingEvents = false;

            // Read current file lines, drop those whose correlationId is in removedIds.
            var kept = new List<string>();
            if (File.Exists(_path))
            {
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = System.Text.Json.JsonSerializer.Deserialize(
                            line, AiRouterJsonContext.Default.LogEntry);
                        if (entry is not null && removedIds.Contains(entry.CorrelationId))
                            continue; // drop it
                    }
                    catch { /* keep malformed lines */ }
                    kept.Add(line);
                }
            }

            File.WriteAllLines(_path, kept);
            _readPosition = new FileInfo(_path).Length;
        }
        catch { /* best-effort */ }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
