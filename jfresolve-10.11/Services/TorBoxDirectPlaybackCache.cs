using System;
using System.Collections.Concurrent;

namespace Jfresolve.Services;

public readonly record struct DirectPlaybackTarget(string Url, string Container, DateTime Expiry);

/// <summary>
/// Caches resolved TorBox /dld/ URLs per Jellyfin item for stable playback and probing.
/// </summary>
public static class TorBoxDirectPlaybackCache
{
    private static readonly ConcurrentDictionary<Guid, DirectPlaybackTarget> _cache = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(45);

    public static bool TryGet(Guid itemId, out DirectPlaybackTarget target)
    {
        if (_cache.TryGetValue(itemId, out target) && target.Expiry > DateTime.UtcNow)
            return true;

        if (_cache.ContainsKey(itemId))
            _cache.TryRemove(itemId, out _);

        target = default;
        return false;
    }

    public static void Set(Guid itemId, string url, string? container)
    {
        if (string.IsNullOrWhiteSpace(url) || !TorBoxStreamService.IsTorBoxStreamCdnUrl(url))
            return;

        var resolvedContainer = container
            ?? StreamContainerGuesser.FromUrl(url)
            ?? "mp4";

        _cache[itemId] = new DirectPlaybackTarget(
            url,
            resolvedContainer,
            DateTime.UtcNow.Add(CacheLifetime));
    }

    public static void Clear(Guid itemId) => _cache.TryRemove(itemId, out _);
}
