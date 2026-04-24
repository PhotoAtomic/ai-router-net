namespace AiRouter.Dashboard;

/// <summary>
/// Maps a session/device identity string to one of 8 neon-fluo colors using a
/// deterministic hash so the same identity always gets the same color across reloads.
/// </summary>
public static class SessionColorHelper
{
    // 8 visually distinct neon-fluo colors (dark-background friendly).
    public static readonly string[] NeonColors =
    [
        "#00f5ff",  // neon cyan
        "#39ff14",  // neon green
        "#ff073a",  // neon red
        "#ff6600",  // neon orange
        "#fe00fe",  // neon magenta
        "#ffff00",  // neon yellow
        "#bf5fff",  // neon purple
        "#00ffab",  // neon mint
    ];

    /// <summary>
    /// Returns a neon hex color for the given identity string.
    /// A null / empty identity returns a neutral gray.
    /// </summary>
    public static string ColorFor(string? identity)
    {
        if (string.IsNullOrEmpty(identity)) return "#4a5568";
        var hash = Fnv1a32(identity);
        return NeonColors[hash % (uint)NeonColors.Length];
    }

    // FNV-1a 32-bit — fast, good distribution, no dependencies.
    private static uint Fnv1a32(string s)
    {
        const uint OffsetBasis = 2166136261u;
        const uint Prime       = 16777619u;
        uint h = OffsetBasis;
        foreach (var c in s)
        {
            h ^= (byte)(c & 0xFF);
            h *= Prime;
            h ^= (byte)(c >> 8);
            h *= Prime;
        }
        return h;
    }
}
