using System;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Configuration;

namespace Jfresolve.Services;

/// <summary>
/// Resolves and caches TorBox /dld/ delivery targets per Jellyfin item.
/// </summary>
public sealed class DirectPlaybackResolver
{
    private readonly PlaybackStreamResolver _playbackStreamResolver;
    private readonly UserPreferencesService _userPreferencesService;

    public DirectPlaybackResolver(
        PlaybackStreamResolver playbackStreamResolver,
        UserPreferencesService userPreferencesService)
    {
        _playbackStreamResolver = playbackStreamResolver;
        _userPreferencesService = userPreferencesService;
    }

    public async Task<DirectPlaybackTarget?> GetOrResolveAsync(
        Guid itemId,
        string itemPath,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        if (TorBoxDirectPlaybackCache.TryGet(itemId, out var cached))
            return cached;

        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.TorBoxApiKey))
            return null;

        if (!ResolvePathParser.TryParse(itemPath, out var parsed))
            return null;

        var preferHdr = config.PreferHdrOverDolbyVision;
        if (userId.HasValue)
        {
            var userPrefs = _userPreferencesService.Get(userId.Value);
            preferHdr = userPrefs.PreferHdrOverDolbyVision ?? preferHdr;
        }

        var target = await _playbackStreamResolver.ResolveDirectTargetAsync(
            new StreamResolveRequest(
                parsed.Type,
                parsed.Id,
                parsed.Season,
                parsed.Episode,
                parsed.Quality,
                parsed.Index,
                userId,
                preferHdr,
                ForceHls: false,
                PreferHlsForSeek: false),
            cancellationToken).ConfigureAwait(false);

        if (target == null || string.IsNullOrWhiteSpace(target.Value.Url))
            return null;

        TorBoxDirectPlaybackCache.Set(itemId, target.Value.Url, target.Value.Container);
        return TorBoxDirectPlaybackCache.TryGet(itemId, out cached) ? cached : target;
    }
}
