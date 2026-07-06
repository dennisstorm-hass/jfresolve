using System;
using System.Collections.Concurrent;

namespace Jfresolve.Services;

/// <summary>
/// Per-title playback hints for TorBox delivery (runtime, etc.).
/// </summary>
public static class TorBoxPlaybackCache
{
    private static readonly ConcurrentDictionary<string, long> RuntimeTicks = new();
    private static readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> HlsUrls = new();
    private static readonly TimeSpan HlsUrlLifetime = TimeSpan.FromMinutes(45);

    public static string BuildKey(string type, string id) => $"{type}/{id}";

    public static string BuildPlaybackKey(string type, string id, string? season, string? episode)
    {
        var key = BuildKey(type, id);
        if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(episode))
            key += $":{season}:{episode}";
        return key;
    }

    public static void SetHlsUrl(string type, string id, string? season, string? episode, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !TorBoxStreamService.IsHlsUrl(url))
            return;

        HlsUrls[BuildPlaybackKey(type, id, season, episode)] = (url, DateTime.UtcNow.Add(HlsUrlLifetime));
    }

    public static bool TryGetHlsUrl(string type, string id, string? season, string? episode, out string url)
    {
        if (HlsUrls.TryGetValue(BuildPlaybackKey(type, id, season, episode), out var entry)
            && entry.Expiry > DateTime.UtcNow)
        {
            url = entry.Url;
            return true;
        }

        url = string.Empty;
        return false;
    }

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
