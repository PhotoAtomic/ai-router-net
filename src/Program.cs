using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AiRouter.Dashboard;
using AiRouter.Logging;
using AiRouter.Process;
using AiRouter.Routing;
using Microsoft.Extensions.Hosting.WindowsServices;

// This application targets Windows only (published as win-x64 self-contained).
[assembly: SupportedOSPlatform("windows")]

namespace AiRouter;
class Program
{
    static async Task Main(string[] args)
    {
        if (await TryHandleServiceCommandAsync(args))
            return;

        var isWindowsService = WindowsServiceHelpers.IsWindowsService();

        // --- Configuration ---------------------------------------------------
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var host = config["Host"] ?? "http://127.0.0.1";
        var port = config["Port"] ?? "5000";
        var listenUrl = $"{host}:{port}";

        var registry = new ProcessRegistry();
        var initialSnapshot = BuildSnapshot(config, registry);

        // --- Parse --log [file] from command line ----------------------------
        RequestLogger?    requestLogger    = null;
        LogWatcherService? logWatcher      = null;
        var logIdx = Array.IndexOf(args, "--log");
        if (logIdx >= 0)
        {
            string logPath;
            if (logIdx + 1 < args.Length && !args[logIdx + 1].StartsWith('-'))
                logPath = args[logIdx + 1];
            else
                logPath = Path.Combine(
                    AppContext.BaseDirectory, "requests.jsonl");
            requestLogger = new RequestLogger(logPath);
            logWatcher    = new LogWatcherService(logPath);
        }

        using var shutdownCts = new CancellationTokenSource();
        var router = new Router(initialSnapshot, requestLogger);
        router.SetShutdownToken(shutdownCts.Token);

        // LlamaCpp monitor — always created; no-op until rules with IsLLamaCpp arrive
        var llamaMonitor = new LlamaCppMonitorService();
        llamaMonitor.UpdateRules(initialSnapshot.Rules);
        llamaMonitor.Start();

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
                    llamaMonitor.UpdateRules(newSnapshot.Rules);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[config] Reload failed, keeping previous rules: {ex.Message}");
                }
            });

        // --- Web host --------------------------------------------------------
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => options.ServiceName = "AiRouter");
        builder.WebHost.UseUrls(listenUrl);
        // Aggressive shutdown: abort in-flight requests quickly on Ctrl+C
        builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
        // Disable request body size limit so large prompts are not rejected
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

        // Blazor Server (dashboard) — only when logging is enabled
        if (logWatcher is not null)
        {
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleton(logWatcher);
            builder.Services.AddSingleton(llamaMonitor);
            builder.Services.AddSingleton<RuleSetupService>(_ => new RuleSetupService((IConfigurationRoot)config));
            builder.Services.AddSingleton<ProcessRegistry>(registry);
        }

        var app = builder.Build();

        // --- Middleware (must come before endpoint routing) ------------------
        if (logWatcher is not null)
        {
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAntiforgery();
        }

        // --- Routes ----------------------------------------------------------
        app.MapPost("/v1/messages", async (HttpContext ctx) =>
            await router.HandleMessagesAsync(ctx));

        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
            await router.HandleGenericAsync(ctx));

        // Generic handler for any /v1/* endpoint
        app.MapMethods("/v1/{**path}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" }, async (HttpContext ctx) =>
            await router.HandleGenericAsync(ctx));

        // Dashboard (Blazor Server) — only when --log is active
        if (logWatcher is not null)
        {
            app.MapGet("/", () => Results.Redirect("/dashboard"));
            app.MapRazorComponents<AiRouter.Dashboard.Components.App>()
               .AddInteractiveServerRenderMode();
            Console.WriteLine($"[dashboard] Live dashboard available at {listenUrl}/dashboard");
        }

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            router.Dispose();
            requestLogger?.Dispose();
            logWatcher?.Dispose();
            llamaMonitor.Dispose();
        });

        // --- Background keyboard listener ------------------------------------
        using var keyListenerCts = new CancellationTokenSource();
        Task keyListenerTask = Task.CompletedTask;
        if (!isWindowsService)
        {
            Console.WriteLine("[keys] Ctrl+K = kill managed processes  |  Ctrl+U = kill processes + shut down router");
            Console.WriteLine();
            keyListenerTask = Task.Run(() => KeyListenerAsync(registry, keyListenerCts.Token));
        }

        // Force exit if shutdown takes longer than 3 s (e.g. a request is stuck)
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the host initiate graceful shutdown
            shutdownCts.Cancel(); // immediately abort all in-flight requests
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Console.WriteLine("[shutdown] Forced exit after Ctrl+C timeout.");
                Environment.Exit(0);
            });
        };

        await app.RunAsync();

        // --- Shutdown: stop key listener -------------------------------------
        keyListenerCts.Cancel();
        try { await keyListenerTask; } catch { }

        // --- Ask user whether to terminate managed processes -----------------
        await AskAndKillAsync(registry, isWindowsService);
        registry.Dispose();
    }

    static async Task<bool> TryHandleServiceCommandAsync(string[] args)
    {
        if (args.Length == 0)
            return false;

        var command = args[0].ToLowerInvariant();
        if (command is not ("install-service" or "--install-service" or "uninstall-service" or "--uninstall-service"))
            return false;

        try
        {
            await HandleServiceCommandAsync(command, args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }

        return true;
    }

    static async Task HandleServiceCommandAsync(string command, string[] args)
    {
        var serviceName = GetOption(args, "--service-name") ?? "AiRouter";

        if (command is "install-service" or "--install-service")
        {
            EnsureAdministrator();

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                throw new InvalidOperationException("Impossibile determinare il percorso dell'eseguibile corrente.");

            var displayName = GetOption(args, "--display-name") ?? "AI Router";
            var startupType = GetOption(args, "--startup") ?? "auto";
            var serviceArgs = GetOption(args, "--service-args") ?? string.Empty;
            var binPath = string.IsNullOrWhiteSpace(serviceArgs)
                ? Quote(exePath)
                : $"{Quote(exePath)} {serviceArgs}";

            await RunScAsync("create", serviceName, "binPath=", binPath, "start=", startupType, "DisplayName=", displayName);
            await RunScAsync("description", serviceName, "AI model router service");

            if (HasSwitch(args, "--start"))
                await RunScAsync("start", serviceName);

            Console.WriteLine($"Servizio '{serviceName}' installato: {binPath}");
            return;
        }

        EnsureAdministrator();
        await RunScAsync(["stop", serviceName], [1060, 1062]);
        await RunScAsync(["delete", serviceName], [1060]);
        Console.WriteLine($"Servizio '{serviceName}' rimosso.");
    }

    static string? GetOption(string[] args, string name)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 1 >= args.Length)
            return null;

        return args[idx + 1];
    }

    static bool HasSwitch(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    static void EnsureAdministrator()
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Eseguire il comando da una console avviata come amministratore.");
    }

    static async Task RunScAsync(params string[] arguments) =>
        await RunScAsync(arguments, []);

    static async Task RunScAsync(string[] arguments, int[] ignoreExitCodes)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = global::System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossibile avviare sc.exe.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (!string.IsNullOrWhiteSpace(output))
            Console.Write(output);
        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.Write(error);

        if (process.ExitCode != 0 && !ignoreExitCodes.Contains(process.ExitCode))
            throw new InvalidOperationException($"sc.exe {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.");
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
    static async Task AskAndKillAsync(ProcessRegistry registry, bool isWindowsService)
    {
        if (!registry.AnyOwnedAlive) return;

        if (isWindowsService)
        {
            await registry.KillAllAsync();
            return;
        }

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
