using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace AiRouter.Process;

// ProcessManager – owns one Process instance per RoutingRule
class ProcessManager : IDisposable
{
    private readonly ProcessConfig _cfg;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private System.Diagnostics.Process? _process;
    // True when we attached to an already-running OS process rather than launching it.
    // A pre-existing process must never be killed by the router.
    // Default to true so that IsOwned is false until we have actually launched
    // the process ourselves. This prevents the dashboard from showing Terminate/
    // Logs buttons for processes that have not yet been scanned.
    private bool _wasPreExisting = true;

    private readonly List<string> _logLines = new();
    private readonly Lock _logLock = new();
    internal event Action? LogReceived;

    internal IReadOnlyList<string> GetLogs()
    {
        lock (_logLock) return _logLines.ToList();
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _logLines.Add(line);
            if (_logLines.Count > 2000)
                _logLines.RemoveAt(0);
        }
        LogReceived?.Invoke();
    }

    private string LogPrefix
    {
        get
        {
            try
            {
                if (_process is not null && !_process.HasExited)
                    return $"PID {_process.Id}";
            }
            catch { }
            return Path.GetFileName(_cfg.FileName);
        }
    }

    public ProcessManager(ProcessConfig cfg)
    {
        _cfg = cfg;
    }

    // Ensures the process is running, starting/restarting if necessary.
    // Must be called before forwarding each request to this rule's backend.
    [SupportedOSPlatform("windows")]
    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Console.WriteLine($"[proc:{LogPrefix}] EnsureRunningAsync entered. IsAlive={IsAlive}, hasHandle={_process is not null}, wasPreExisting={_wasPreExisting}.");
            if (IsAlive) { Console.WriteLine($"[proc:{LogPrefix}] Already alive (PID {_process!.Id}); skipping start."); return; }

            // Clean up any dead handle (only if we owned it)
            if (_process is not null)
            {
                if (_wasPreExisting)
                    Console.WriteLine($"[proc:{LogPrefix}] Pre-existing process exited — will try to find/start a new one.");
                else
                    Console.WriteLine($"[proc:{LogPrefix}] Process exited (code {SafeExitCode()}), restarting…");
                _process.Dispose();
                _process = null;
                _wasPreExisting = false;
            }

            // Check if the process is already running on the system.
            Console.WriteLine($"[proc:{LogPrefix}] Scanning OS for an existing process matching '{_cfg.FileName} {_cfg.Arguments}'…");
            // The WMI query can occasionally hang (slow/locked WMI service);
            // run it on a background task with a hard timeout so we never
            // block EnsureRunningAsync indefinitely. On timeout we proceed as
            // if no match was found and launch a fresh process.
            System.Diagnostics.Process? existing;
            var scanTask = Task.Run(FindExistingProcess);
            var scanTimeout = TimeSpan.FromSeconds(5);
            var completed = await Task.WhenAny(scanTask, Task.Delay(scanTimeout, ct));
            if (completed == scanTask)
            {
                existing = await scanTask;
            }
            else
            {
                Console.WriteLine($"[proc:{LogPrefix}] WMI scan exceeded {scanTimeout.TotalSeconds:F0}s — assuming no match and launching a new process. (Background scan still running, will be ignored.)");
                existing = null;
                _ = scanTask.ContinueWith(t =>
                {
                    if (t.Exception is not null)
                        Console.WriteLine($"[proc:{LogPrefix}] Late WMI scan failed: {t.Exception.GetBaseException().Message}");
                }, TaskScheduler.Default);
            }
            if (existing is not null)
            {
                _process = existing;
                _wasPreExisting = true;
                existing.EnableRaisingEvents = true;
                existing.Exited += (_, _) =>
                    Console.WriteLine($"[proc:{LogPrefix}] Pre-existing process (PID {existing.Id}) exited.");
                Console.WriteLine($"[proc:{LogPrefix}] Found pre-existing '{_cfg.FileName}' (PID {existing.Id}), attaching — will NOT terminate on router exit.");
                return;
            }

            // Not found: launch it ourselves.
            _wasPreExisting = false;
            Console.WriteLine($"[proc:{LogPrefix}] No matching pre-existing process found. Launching: {_cfg.FileName} {_cfg.Arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = _cfg.FileName,
                Arguments = _cfg.Arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };

            // Stream stdout / stderr to the router console
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    var line = $"[proc:{LogPrefix}] {e.Data}";
                    Console.WriteLine(line);
                    AppendLog(line);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    var line = $"[proc:{LogPrefix}] ERR {e.Data}";
                    Console.WriteLine(line);
                    AppendLog(line);
                }
            };
            _process.Exited += (_, _) =>
                Console.WriteLine($"[proc:{LogPrefix}] Process exited with code {SafeExitCode()}");

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Console.WriteLine($"[proc:{LogPrefix}] Started (PID {_process.Id}), waiting {_cfg.StartupDelaySeconds}s for readiness…");
            await Task.Delay(TimeSpan.FromSeconds(_cfg.StartupDelaySeconds), ct);
            Console.WriteLine($"[proc:{LogPrefix}] Ready.");
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
                Console.WriteLine($"[proc:{LogPrefix}] Skipping kill — process (PID {_process!.Id}) was pre-existing and not owned by the router.");
                return;
            }

            Console.WriteLine($"[proc:{LogPrefix}] Killing process (PID {_process!.Id})…");
            try
            {
                _process!.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                Console.WriteLine($"[proc:{LogPrefix}] Killed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[proc:{LogPrefix}] Kill failed: {ex.Message}");
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
    private System.Diagnostics.Process? FindExistingProcess()
    {
        var resolvedExe = ResolveExecutablePath(_cfg.FileName);

        // Restrict the WMI query to the executable name to keep it fast and
        // avoid enumerating every single process on the machine. We escape
        // backslashes/quotes for WQL.
        var exeName = Path.GetFileName(resolvedExe ?? _cfg.FileName);
        if (string.IsNullOrEmpty(exeName)) return null;
        // If the user passed "pwsh" without extension, WMI's Name column
        // includes the extension, so we try with .exe appended when missing.
        if (!exeName.Contains('.', StringComparison.Ordinal))
            exeName += ".exe";
        var wqlName = exeName.Replace("\\", "\\\\").Replace("'", "\\'");
        var query   = $"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='{wqlName}'";

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var searcher = new ManagementObjectSearcher(query);
            using var results  = searcher.Get();
            int scanned = 0;

            foreach (ManagementBaseObject moBase in results)
            {
                using var mo = (ManagementObject)moBase;
                scanned++;
                var pid     = Convert.ToInt32(mo["ProcessId"]);
                var exePath = mo["ExecutablePath"] as string ?? string.Empty;
                var cmdLine = mo["CommandLine"]    as string ?? string.Empty;

                if (!ExeMatches(exePath, resolvedExe)) continue;
                if (!ArgumentsMatch(cmdLine, exePath, _cfg.Arguments)) continue;

                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    Console.WriteLine($"[proc:{LogPrefix}] Found pre-existing process matching '{_cfg.FileName} {_cfg.Arguments}' (PID {pid}) after scanning {scanned} candidate(s) in {sw.ElapsedMilliseconds}ms.");
                    return proc;
                }
                catch { /* process exited between query and GetProcessById */ }
            }

            sw.Stop();
            Console.WriteLine($"[proc:{LogPrefix}] WMI scan complete: {scanned} '{exeName}' process(es) inspected, no match (took {sw.ElapsedMilliseconds}ms).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[proc:{LogPrefix}] Warning: WMI process scan failed: {ex.Message}");
        }

        return null;
    }

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

    [SupportedOSPlatform("windows")]
    private static bool ArgumentsMatch(string cmdLine, string exePath, string expectedArgs)
    {
        expectedArgs = expectedArgs.Trim();

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

    private static string? ResolveExecutablePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (Path.IsPathRooted(fileName)) return fileName;

        var extensions = new[] { ".exe", ".cmd", ".bat", "" };
        var pathDirs   = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir, fileName + ext);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return fileName;
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
