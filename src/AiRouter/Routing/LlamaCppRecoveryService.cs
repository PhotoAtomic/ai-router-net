using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiRouter.Routing;

// LlamaCppRecoveryService – tries to recover from a 500 returned by llama.cpp
// when the per-model worker process has crashed but llama.cpp is still routing
// requests to it. The recovery sequence is:
//   1. GET  {baseUrl}/models                  – locate the model entry
//   2. POST {baseUrl}/models/unload {model}   – best effort (fire-and-forget if already unloaded)
//   3. POST {baseUrl}/models/load   {model}   – with exponential-backoff retry (3 tries)
//   4. wait the configured StartupDelay
// Result is reported via RecoveryOutcome; the caller is responsible for
// replaying the original /v1/messages request.
internal sealed class LlamaCppRecoveryService
{
    private readonly HttpClient _http;

    public LlamaCppRecoveryService(HttpClient http) => _http = http;

    public async Task<RecoveryOutcome> TryRecoverAsync(
        string baseUrl,
        string modelId,
        TimeSpan postLoadDelay,
        List<string> attemptsLog,
        CancellationToken ct)
    {
        baseUrl = baseUrl.TrimEnd('/');

        // 1. Look up the model in /models.
        // Returns null when not found, or the status string ("loaded", "unloaded", …) — possibly empty.
        bool found;
        string? status;
        try
        {
            var lookup = await GetModelStatusAsync(baseUrl, modelId, ct);
            found  = lookup.Found;
            status = lookup.Status;
        }
        catch (Exception ex)
        {
            attemptsLog.Add($"GET /models failed: {ex.Message}");
            return RecoveryOutcome.Aborted;
        }

        if (!found)
        {
            attemptsLog.Add($"Model '{modelId}' not present in /models — llama.cpp cannot manage it. Aborting recovery.");
            return RecoveryOutcome.Aborted;
        }

        attemptsLog.Add($"Model '{modelId}' found in /models with status='{status}'.");

        // 2. Unload (fire-and-forget if it fails because already unloaded).
        var unloadOk = await TryUnloadAsync(baseUrl, modelId, attemptsLog, ct);
        if (!unloadOk && string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            // It was supposedly loaded but unload failed: keep going, load may still work.
            attemptsLog.Add($"Unload reported failure even though status was 'loaded'. Proceeding with load anyway.");
        }

        // 3. Load with exponential backoff (3 attempts).
        var loaded = await RetryAsync(
            label: "load",
            attempts: 3,
            initialDelay: TimeSpan.FromSeconds(1),
            attemptsLog: attemptsLog,
            ct: ct,
            action: () => TryLoadAsync(baseUrl, modelId, ct));

        if (!loaded)
        {
            attemptsLog.Add($"All load attempts failed for '{modelId}'.");
            return RecoveryOutcome.Failed;
        }

        // 4. Give the freshly-spawned worker a chance to bind its port / load weights.
        attemptsLog.Add($"Load succeeded; waiting {postLoadDelay.TotalSeconds:F1}s for the model to warm up.");
        try { await Task.Delay(postLoadDelay, ct); } catch (OperationCanceledException) { }

        return RecoveryOutcome.Recovered;
    }

    // -------------------------------------------------------------------------

    private async Task<(bool Found, string? Status)> GetModelStatusAsync(string baseUrl, string modelId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{baseUrl}/models", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return (false, null);

        foreach (var item in data.EnumerateArray())
        {
            string? id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            bool match = string.Equals(id, modelId, StringComparison.OrdinalIgnoreCase);
            if (!match && item.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in aliases.EnumerateArray())
                {
                    if (string.Equals(a.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                    { match = true; break; }
                }
            }
            if (!match) continue;

            string? value = null;
            if (item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object
                && st.TryGetProperty("value", out var v))
            {
                value = v.GetString();
            }
            return (true, value);
        }

        return (false, null);
    }

    private async Task<bool> TryUnloadAsync(string baseUrl, string modelId, List<string> attemptsLog, CancellationToken ct)
    {
        try
        {
            var ok = await PostModelActionAsync(baseUrl, "/models/unload", modelId, ct);
            attemptsLog.Add(ok
                ? $"POST /models/unload '{modelId}' → success."
                : $"POST /models/unload '{modelId}' → reported not-success (probably already unloaded), continuing.");
            return ok;
        }
        catch (Exception ex)
        {
            attemptsLog.Add($"POST /models/unload '{modelId}' threw: {ex.Message} (ignored, continuing).");
            return false;
        }
    }

    private async Task<bool> TryLoadAsync(string baseUrl, string modelId, CancellationToken ct)
        => await PostModelActionAsync(baseUrl, "/models/load", modelId, ct);

    private async Task<bool> PostModelActionAsync(string baseUrl, string path, string modelId, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["model"] = modelId });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{baseUrl}{path}", content, ct);
        if (!resp.IsSuccessStatusCode) return false;

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("success", out var s)
                   && s.ValueKind == JsonValueKind.True;
        }
        catch
        {
            // Non-JSON 2xx: treat as success.
            return true;
        }
    }

    // Generic exponential-backoff retry wrapper used for /models/load.
    private static async Task<bool> RetryAsync(
        string label,
        int attempts,
        TimeSpan initialDelay,
        List<string> attemptsLog,
        CancellationToken ct,
        Func<Task<bool>> action)
    {
        var delay = initialDelay;
        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                if (await action())
                {
                    attemptsLog.Add($"{label} attempt {i}/{attempts} → success.");
                    return true;
                }
                attemptsLog.Add($"{label} attempt {i}/{attempts} → failure.");
            }
            catch (Exception ex)
            {
                attemptsLog.Add($"{label} attempt {i}/{attempts} threw: {ex.Message}");
            }

            if (i < attempts)
            {
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return false; }
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
        }
        return false;
    }
}

internal enum RecoveryOutcome
{
    // The model was reloaded, the original request can be replayed.
    Recovered,
    // The model is unknown to llama.cpp or /models itself failed; do not replay.
    Aborted,
    // Recovery sequence executed but did not succeed; do not replay.
    Failed,
}
