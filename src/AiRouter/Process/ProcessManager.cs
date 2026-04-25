using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace AiRouter.Process;

// ProcessManager – owns one Process instance per RoutingRule
class ProcessManager : IDisposable
{
    private readonly ProcessConfig _cfg;
    private readonly string _label; // used in log output
    private readonly SemaphoreSlim _lock = new(1, 1);
    private System.Diagnostics.Process? _process;
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

            _process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };

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
    private System.Diagnostics.Process? FindExistingProcess()
    {
        var resolvedExe = ResolveExecutablePath(_cfg.FileName);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process");
            using var results = searcher.Get();

            foreach (ManagementObject mo in results)
            {
                var pid     = Convert.ToInt32(mo["ProcessId"]);
                var exePath = mo["ExecutablePath"] as string ?? string.Empty;
                var cmdLine = mo["CommandLine"]    as string ?? string.Empty;

                if (!ExeMatches(exePath, resolvedExe)) continue;
                if (!ArgumentsMatch(cmdLine, exePath, _cfg.Arguments)) continue;

                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
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
