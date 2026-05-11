using System.Runtime.CompilerServices;
using System.Text;

namespace AiRouter.Conversion.Streaming;

public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        Stream? rawCopy = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var copyWriter = rawCopy is not null ? new StreamWriter(rawCopy, Encoding.UTF8, leaveOpen: true) : null;
        string? currentEvent = null;
        StringBuilder? currentData = null;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (line is null) break;

            if (copyWriter is not null)
            {
                await copyWriter.WriteAsync(line).ConfigureAwait(false);
                await copyWriter.WriteAsync("\n").ConfigureAwait(false);
            }

            if (line.Length == 0)
            {
                if (currentEvent is not null || currentData is not null)
                {
                    yield return new SseEvent(
                        currentEvent ?? "message",
                        currentData?.ToString() ?? string.Empty);
                }
                currentEvent = null;
                currentData = null;
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue; // comment

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var chunk = line["data:".Length..];
                if (chunk.StartsWith(' ')) chunk = chunk[1..];
                if (currentData is null) currentData = new StringBuilder();
                else currentData.Append('\n');
                currentData.Append(chunk);
            }
            // id:, retry: ignored
        }

        // flush trailing event
        if (currentEvent is not null || currentData is not null)
        {
            yield return new SseEvent(
                currentEvent ?? "message",
                currentData?.ToString() ?? string.Empty);
        }

        if (copyWriter is not null)
        {
            await copyWriter.FlushAsync(ct).ConfigureAwait(false);
            await copyWriter.DisposeAsync().ConfigureAwait(false);
        }
    }
}
