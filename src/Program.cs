using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AiRouter.Dashboard;
using AiRouter.Logging;
using AiRouter.Process;
using AiRouter.Routing;

// This application targets Windows only (published as win-x64 self-contained).
[assembly: SupportedOSPlatform("windows")]

namespace AiRouter;
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

        var router = new Router(initialSnapshot, requestLogger);

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
        builder.WebHost.UseUrls(listenUrl);
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

