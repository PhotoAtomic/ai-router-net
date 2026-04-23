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

    // -------------------------------------------------------------------------
    // Private fields
    // -------------------------------------------------------------------------

    private readonly string            _path;
    private readonly List<LogEntryViewModel> _entries = [];
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

            var vm = LogEntryViewModel.FromLogEntry(entry);
            lock (_lock)
            {
                _entries.Insert(0, vm); // newest first
                if (vm.DurationMs    > MaxDurationMs)    MaxDurationMs    = vm.DurationMs;
                if (vm.RequestBytes  > MaxRequestBytes)  MaxRequestBytes  = vm.RequestBytes;
                if (vm.ResponseBytes > MaxResponseBytes) MaxResponseBytes = vm.ResponseBytes;
            }
            return true;
        }
        catch { return false; }
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
