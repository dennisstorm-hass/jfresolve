using System;
using System.Collections.Concurrent;

namespace Jfresolve.Services;

/// <summary>
/// Per-title playback hints for TorBox delivery (runtime, etc.).
/// </summary>
public static class TorBoxPlaybackCache
{
    private static readonly ConcurrentDictionary<string, long> RuntimeTicks = new();

    public static string BuildKey(string type, string id) => $"{type}/{id}";

    public static void SetRuntimeTicks(string type, string id, long ticks)
    {
        if (ticks <= 0)
            return;

        RuntimeTicks[BuildKey(type, id)] = ticks;
    }

    public static long? TryGetRuntimeTicks(string type, string id)
    {
        return RuntimeTicks.TryGetValue(BuildKey(type, id), out var ticks) && ticks > 0
            ? ticks
            : null;
    }
}
