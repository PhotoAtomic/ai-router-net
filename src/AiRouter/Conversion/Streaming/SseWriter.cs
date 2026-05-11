using System.Text;

namespace AiRouter.Conversion.Streaming;

public static class SseWriter
{
    public static async Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<SseEvent> events,
        CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        await foreach (var ev in events.WithCancellation(ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(ev.EventName) && ev.EventName != "message")
            {
                await writer.WriteAsync($"event: {ev.EventName}\n").ConfigureAwait(false);
            }
            foreach (var line in ev.Data.Split('\n'))
            {
                await writer.WriteAsync($"data: {line}\n").ConfigureAwait(false);
            }
            await writer.WriteAsync("\n").ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
