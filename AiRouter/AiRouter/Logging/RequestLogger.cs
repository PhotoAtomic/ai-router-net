using System.Text.Json;

namespace AiRouter.Logging;

// Appends one JSON object per line to a .jsonl log file
class RequestLogger : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RequestLogger(string path)
    {
        _path = path;
        Console.WriteLine($"[log] Logging requests to: {path}");
    }

    public async Task LogAsync(LogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _lock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_path, line);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
