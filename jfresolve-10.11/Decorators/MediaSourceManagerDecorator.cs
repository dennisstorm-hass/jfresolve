#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Decorators;

/// <summary>
/// Decorator for IMediaSourceManager to support quality versioning for Jfresolve items
/// </summary>
public class MediaSourceManagerDecorator : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceManagerDecorator> _log;
    private readonly IItemRepository _repo;
    private readonly IDirectoryService _directoryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    
    // Track items that have been probed or are currently being probed
    // Key: item ID, Value: timestamp when probe was initiated
    private static readonly ConcurrentDictionary<Guid, DateTime> _probedItems = new();
    private static DateTime _lastProbeCacheCleanup = DateTime.UtcNow;
    private const int ProbeCacheCleanupIntervalMinutes = 60;
    private const int RecentlyAddedThresholdMinutes = 5; // Consider items added within last 5 minutes as "newly added"

    public MediaSourceManagerDecorator(
        IMediaSourceManager inner,
        ILogger<MediaSourceManagerDecorator> log,
        IItemRepository repo,
        IDirectoryService directoryService,
        IHttpContextAccessor httpContextAccessor)
    {
        _inner = inner;
        _log = log;
        _repo = repo;
        _directoryService = directoryService;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        if (!IsJfresolve(item))
        {
            return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken);
        }

        _log.LogDebug("Jfresolve: GetPlaybackMediaSources for {ItemId} ({Name})", item.Id, item.Name);

        BaseItem primaryItem = item;

        if (item.IsVirtualItem)
        {
            _log.LogDebug("Jfresolve: Item is virtual, finding primary item");
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { item.GetBaseItemKind() },
                HasAnyProviderId = item.ProviderIds,
                IsVirtualItem = false,
            };

            primaryItem = _repo.GetItemList(query).FirstOrDefault() ?? item;
            _log.LogDebug("Jfresolve: Using primary item {PrimaryId} ({PrimaryName})", primaryItem.Id, primaryItem.Name);
        }

        var sources = (await _inner.GetPlaybackMediaSources(primaryItem, user, allowMediaProbe, enablePathSubstitution, cancellationToken)).ToList();

        foreach (var info in sources)
        {
            ApplyTrick(info);
        }

        var primarySource = sources.FirstOrDefault();
        if (primarySource != null && NeedsProbe(primarySource, primaryItem))
        {
            _log.LogInformation("Jfresolve: Probing primary item {Name} to extract complete stream information (video, audio, subtitles)", primaryItem.Name);
            await ProbeItem(primaryItem, cancellationToken);

            // Get sources again after probing to ensure we have complete stream information
            sources = (await _inner.GetPlaybackMediaSources(primaryItem, user, allowMediaProbe, enablePathSubstitution, cancellationToken)).ToList();
            foreach (var info in sources)
            {
                ApplyTrick(info);
            }
            
            // CRITICAL: Ensure MediaStreams on MediaSourceInfo are populated from database
            // This prevents Jellyfin from doing additional probing ("Additional data" delay)
            // Get streams from database (which were populated by the probe)
            try
            {
                var dbStreams = _inner.GetMediaStreams(primaryItem.Id).ToList();
                if (dbStreams.Any())
                {
                    var updatedSource = sources.FirstOrDefault();
                    if (updatedSource != null)
                    {
                        // Merge streams intelligently to preserve indices for subtitle offset functionality
                        // This ensures subtitle information is immediately available without breaking subtitle sync
                        updatedSource.MediaStreams = MergeStreamsPreservingIndices(updatedSource.MediaStreams, dbStreams);
                        _log.LogInformation("Jfresolve: Merged MediaSourceInfo with {Count} streams from database (including {SubtitleCount} subtitles) for {Name}", 
                            dbStreams.Count, dbStreams.Count(s => s.Type == MediaStreamType.Subtitle), primaryItem.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - database access errors shouldn't break playback
                _log.LogWarning(ex, "Jfresolve: Error accessing database streams for {Name} after probing, continuing with existing streams", primaryItem.Name);
            }
            
            // Ensure media info is complete by using AddMediaInfoWithProbe on the primary source
            // This ensures subtitle information is available before playback starts, preventing stream restarts when subtitles are changed
            if (primarySource != null && !string.IsNullOrEmpty(primarySource.Path) && 
                primarySource.Path.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Use the updated source from the list after probing
                    var updatedSource = sources.FirstOrDefault();
                    if (updatedSource != null)
                    {
                        await _inner.AddMediaInfoWithProbe(updatedSource, isAudio: false, cacheKey: null, addProbeDelay: false, isLiveStream: false, cancellationToken);
                        
                        // Verify subtitle streams are now available
                        var finalStreams = _inner.GetMediaStreams(primaryItem.Id);
                        var subtitleCount = finalStreams.Count(s => s.Type == MediaStreamType.Subtitle);
                        _log.LogInformation("Jfresolve: Added complete media info for {Name} - {SubtitleCount} subtitle stream(s) available", primaryItem.Name, subtitleCount);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Jfresolve: Failed to add media info with probe for {Name}, continuing anyway", primaryItem.Name);
                }
            }
        }
        else if (primarySource != null)
        {
            // Even if we don't need to probe, ensure MediaStreams are populated from database
            // This prevents Jellyfin from doing additional probing for subtitle information ("Additional data" delay)
            try
            {
                var dbStreams = _inner.GetMediaStreams(primaryItem.Id).ToList();
                if (dbStreams.Any())
                {
                    var hasSubtitleStreams = dbStreams.Any(s => s.Type == MediaStreamType.Subtitle);
                    var sourceHasSubtitleStreams = primarySource.MediaStreams?.Any(s => s.Type == MediaStreamType.Subtitle) ?? false;
                    
                    // If database has subtitle streams but source doesn't, merge from database
                    // This prevents Jellyfin from doing additional probing to extract subtitle information
                    // Use merge to preserve stream indices for subtitle offset functionality
                    if (hasSubtitleStreams && !sourceHasSubtitleStreams)
                    {
                        primarySource.MediaStreams = MergeStreamsPreservingIndices(primarySource.MediaStreams, dbStreams);
                        _log.LogDebug("Jfresolve: Merged MediaSourceInfo with {Count} streams from database (including {SubtitleCount} subtitles) for {Name} to prevent additional probing", 
                            dbStreams.Count, dbStreams.Count(s => s.Type == MediaStreamType.Subtitle), primaryItem.Name);
                    }
                    // If source has no streams at all, use database streams
                    else if (primarySource.MediaStreams == null || !primarySource.MediaStreams.Any())
                    {
                        primarySource.MediaStreams = dbStreams;
                        _log.LogDebug("Jfresolve: Populated MediaSourceInfo with {Count} streams from database (including {SubtitleCount} subtitles) for {Name} without probing", 
                            dbStreams.Count, dbStreams.Count(s => s.Type == MediaStreamType.Subtitle), primaryItem.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - database access errors shouldn't break playback
                _log.LogWarning(ex, "Jfresolve: Error accessing database streams for {Name} in GetPlaybackMediaSources, continuing with existing streams", primaryItem.Name);
            }
        }

        var virtualQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { primaryItem.GetBaseItemKind() },
            HasAnyProviderId = primaryItem.ProviderIds,
            IsVirtualItem = true,
        };

        var virtualItems = _repo.GetItemList(virtualQuery)
            .OfType<Video>()
            .Where(v => IsJfresolve(v))
            .OrderBy(v => v.Name)
            .ToList();

        _log.LogDebug("Jfresolve: Found {Count} virtual quality items", virtualItems.Count);

        foreach (var virtualItem in virtualItems)
        {
            var virtualSources = (await _inner.GetPlaybackMediaSources(virtualItem, user, allowMediaProbe, enablePathSubstitution, cancellationToken)).ToList();
            var virtualSource = virtualSources.FirstOrDefault();

            if (virtualSource != null && NeedsProbe(virtualSource, virtualItem))
            {
                _log.LogInformation("Jfresolve: Probing virtual item {Name} to extract complete stream information (video, audio, subtitles)", virtualItem.Name);
                await ProbeItem(virtualItem, cancellationToken);
            }

            var qualityStreams = _inner.GetMediaStreams(virtualItem.Id);
            var qualityContainer = virtualItem.Container;

            var qualitySource = new MediaSourceInfo
            {
                Id = virtualItem.Id.ToString("N"),
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                MediaStreams = qualityStreams,
                MediaAttachments = _inner.GetMediaAttachments(virtualItem.Id),
                Name = virtualItem.Name,
                Path = virtualItem.Path,
                RunTimeTicks = virtualItem.RunTimeTicks,
                Container = qualityContainer,
                Size = virtualItem.Size,
                Type = MediaSourceType.Grouping,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
            };

            if (virtualItem is Video video)
            {
                qualitySource.VideoType = video.VideoType;
                qualitySource.IsoType = video.IsoType;
                qualitySource.Video3DFormat = video.Video3DFormat;
                qualitySource.Timestamp = video.Timestamp;
            }

            // Apply trick to ensure proper protocol and remote settings
            ApplyTrick(qualitySource);

            sources.Add(qualitySource);
        }

        if (sources.Count > 0)
        {
            sources[0].Type = MediaSourceType.Default;
        }

        // Append user id to resolve URLs so per-user preferences (e.g. prefer HDR over Dolby Vision) are applied
        foreach (var info in sources)
        {
            if (!string.IsNullOrEmpty(info.Path) && info.Path.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase))
            {
                var sep = info.Path.IndexOf('?') >= 0 ? "&" : "?";
                info.Path += $"{sep}userId={user.Id:N}";
            }
        }

        _log.LogDebug("Jfresolve: Returning {Count} total playback sources", sources.Count);
        return sources;
    }

    /// <summary>
    /// Check if a media source needs to be probed
    /// Probes if video streams are missing, runtime is too short, or if we haven't probed for all stream types yet
    /// This ensures subtitle information is available before playback starts, preventing stream restarts when subtitles are changed
    /// </summary>
    private bool NeedsProbe(MediaSourceInfo source, BaseItem item)
    {
        if (source == null) return false;

        var streams = source.MediaStreams ?? new List<MediaStream>();
        var hasVideoStreams = streams.Any(ms => ms.Type == MediaStreamType.Video);
        var noVideoStreams = !hasVideoStreams;
        var runtimeTooShort = (source.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;
        
        // Check if we have stream information from the database
        // If item has no media streams in database, we need to probe to get complete info (including subtitles)
        // This ensures subtitle information is available before playback starts, preventing stream restarts when subtitles are changed
        var dbStreams = _inner.GetMediaStreams(item.Id);
        var hasDbStreams = dbStreams.Any();
        
        // If we have video streams in the source but no database streams, we should probe
        // This means we haven't probed this item yet and don't have complete stream information (including subtitles)
        var needsProbeForCompleteInfo = hasVideoStreams && !hasDbStreams;

        var needsProbe = noVideoStreams || runtimeTooShort || needsProbeForCompleteInfo;
        
        if (needsProbe)
        {
            _log.LogDebug("Jfresolve: NeedsProbe=true for {Name} - NoVideoStreams: {NoVideo}, RuntimeTooShort: {Runtime}, NeedsProbeForCompleteInfo: {Complete}",
                item.Name, noVideoStreams, runtimeTooShort, needsProbeForCompleteInfo);
        }

        return needsProbe;
    }

    /// <summary>
    /// Probe an item to populate its MediaStreams
    /// </summary>
    private async Task ProbeItem(BaseItem item, CancellationToken cancellationToken)
    {
        var wasVirtual = item.IsVirtualItem;
        item.IsVirtualItem = false;

        try
        {
            _log.LogInformation("Jfresolve: Probing {Name} - Path: {Path}, IsVirtual: {IsVirtual}", item.Name, item.Path, item.IsVirtualItem);

            await item.RefreshMetadata(
                new MetadataRefreshOptions(_directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                },
                cancellationToken
            ).ConfigureAwait(false);

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            var streams = _inner.GetMediaStreams(item.Id);
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var audioStreams = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
            var subtitleStreams = streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
            
            if (videoStream != null)
            {
                _log.LogInformation("Jfresolve: Probed {Name} - Codec: {Codec}, Width: {Width}, Height: {Height}, Audio streams: {AudioCount}, Subtitle streams: {SubtitleCount}",
                    item.Name, videoStream.Codec, videoStream.Width, videoStream.Height, audioStreams.Count, subtitleStreams.Count);
            }
            else
            {
                _log.LogWarning("Jfresolve: Probed {Name} but NO video stream found! Audio streams: {AudioCount}, Subtitle streams: {SubtitleCount}",
                    item.Name, audioStreams.Count, subtitleStreams.Count);
            }
        }
        finally
        {
            item.IsVirtualItem = wasVirtual;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

        if (!IsJfresolve(item))
        {
            return sources;
        }

        _log.LogDebug("Jfresolve: GetStaticMediaSources for {ItemId} ({Name})", item.Id, item.Name);

        foreach (var info in sources)
        {
            ApplyTrick(info);
        }

        // Get streams from database to ensure subtitle information is available in UI
        // This ensures subtitle streams are shown even before first playback
        // If streams exist in database (from previous probing), use them
        // Otherwise, streams will be populated when GetPlaybackMediaSources is called (async probing)
        var dbStreams = _inner.GetMediaStreams(item.Id).ToList();
        var primaryStreams = dbStreams.Any() 
            ? dbStreams 
            : (sources.Count > 0 ? sources[0].MediaStreams?.ToList() ?? new List<MediaStream>() : new List<MediaStream>());
        var primaryContainer = sources.Count > 0 ? sources[0].Container : null;
        
        if (dbStreams.Any())
        {
            _log.LogDebug("Jfresolve: Using {Count} streams from database for {Name} (including {SubtitleCount} subtitles)", 
                dbStreams.Count, item.Name, dbStreams.Count(s => s.Type == MediaStreamType.Subtitle));
        }
        else if (sources.Count > 0 && sources[0] != null)
        {
            var source = sources[0];
            // Check if this is a Jfresolve item that needs probing
            if (!string.IsNullOrEmpty(source.Path) && source.Path.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase))
            {
                // Only probe if item was recently added (within threshold) and hasn't been probed yet
                var wasRecentlyAdded = IsRecentlyAdded(item);
                var alreadyProbed = _probedItems.ContainsKey(item.Id);
                
                if (wasRecentlyAdded && !alreadyProbed)
                {
                    _log.LogDebug("Jfresolve: Item {Name} was recently added and needs probing for subtitle preload", item.Name);
                    
                    // Mark as probed to prevent duplicate probes
                    _probedItems.TryAdd(item.Id, DateTime.UtcNow);
                    
                    // Cleanup old probe cache entries periodically
                    CleanupProbeCacheIfNeeded();
                    
                    // Trigger background probe to preload subtitle information
                    // This ensures subtitles are available in UI before first playback
                    // Fire-and-forget: don't await, don't block UI
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Small delay to avoid blocking UI thread
                            await Task.Delay(100);
                            
                            // Probe the item to extract stream information including subtitles
                            var wasVirtual = item.IsVirtualItem;
                            item.IsVirtualItem = false;
                            
                            try
                            {
                                await item.RefreshMetadata(
                                    new MetadataRefreshOptions(_directoryService)
                                    {
                                        EnableRemoteContentProbe = true,
                                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                    },
                                    CancellationToken.None
                                ).ConfigureAwait(false);
                                
                                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                                
                                var probedStreams = _inner.GetMediaStreams(item.Id);
                                var subtitleCount = probedStreams.Count(s => s.Type == MediaStreamType.Subtitle);
                                _log.LogInformation("Jfresolve: Background probe completed for {Name} - {SubtitleCount} subtitle stream(s) now available in UI", 
                                    item.Name, subtitleCount);
                            }
                            finally
                            {
                                item.IsVirtualItem = wasVirtual;
                                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Jfresolve: Background probe failed for {Name}, subtitles will be available after first playback", item.Name);
                            // Remove from cache on failure so we can retry later if needed
                            _probedItems.TryRemove(item.Id, out _);
                        }
                    });
                }
                else if (!wasRecentlyAdded)
                {
                    _log.LogDebug("Jfresolve: Item {Name} is not recently added, skipping background probe", item.Name);
                }
                else if (alreadyProbed)
                {
                    _log.LogDebug("Jfresolve: Item {Name} has already been probed, skipping duplicate probe", item.Name);
                }
            }
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { item.GetBaseItemKind() },
            HasAnyProviderId = item.ProviderIds,
            Recursive = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            IsVirtualItem = true,
        };

        var virtualItems = _repo.GetItemList(query)
            .OfType<Video>()
            .Where(v => IsJfresolve(v))
            .OrderBy(v => v.Name)
            .ToList();

        _log.LogDebug("Jfresolve: Found {Count} virtual quality items for {Name}", virtualItems.Count, item.Name);

        foreach (var virtualItem in virtualItems)
        {
            var qualitySource = new MediaSourceInfo
            {
                Id = virtualItem.Id.ToString("N"),
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                MediaStreams = primaryStreams,
                MediaAttachments = _inner.GetMediaAttachments(virtualItem.Id),
                Name = virtualItem.Name,
                Path = virtualItem.Path,
                RunTimeTicks = virtualItem.RunTimeTicks,
                Container = primaryContainer,
                Size = virtualItem.Size,
                Type = MediaSourceType.Grouping,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
            };

            if (virtualItem is Video video)
            {
                qualitySource.VideoType = video.VideoType;
                qualitySource.IsoType = video.IsoType;
                qualitySource.Video3DFormat = video.Video3DFormat;
                qualitySource.Timestamp = video.Timestamp;
            }

            // Apply trick to ensure proper protocol and remote settings
            ApplyTrick(qualitySource);

            sources.Add(qualitySource);
        }

        if (sources.Count > 0)
        {
            sources[0].Type = MediaSourceType.Default;
        }

        _log.LogDebug("Jfresolve: Returning {Count} total quality options for {Name}", sources.Count, item.Name);
        return sources;
    }

    private void ApplyTrick(MediaSourceInfo info)
    {
        if (string.IsNullOrEmpty(info.Path) || !info.Path.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Set protocol and remote flag - let Jellyfin decide on transcoding based on its own logic
        info.Protocol = MediaProtocol.Http;
        info.IsRemote = true;
        // Don't force transcoding - let Jellyfin decide based on codec compatibility, client capabilities, etc.
        // SupportsDirectPlay, SupportsDirectStream, and SupportsTranscoding will be determined by Jellyfin
    }

    private bool IsJfresolve(BaseItem item)
    {
        if (item == null) return false;
        return item.ProviderIds.ContainsKey("Jfresolve");
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        // Always return streams from database to ensure subtitle information is available in UI
        // This ensures subtitle streams are shown even before first playback
        var streams = _inner.GetMediaStreams(itemId);
        
        // If no streams in database, return empty list (streams will be populated after probing)
        // Probing happens in GetPlaybackMediaSources when playback starts
        return streams;
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

    public async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string? liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var source = await _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
        
        // Ensure MediaStreams are populated from database to prevent "retrieving additional data" hang
        // This is critical during playback initialization when Jellyfin calls GetMediaSource
        // Merge streams to keep subtitle sync, but preserve stream indices for subtitle offset control
        if (IsJfresolve(item))
        {
            try
            {
                var dbStreams = _inner.GetMediaStreams(item.Id).ToList();
                if (dbStreams.Any())
                {
                    // Merge streams intelligently - use database streams as source of truth for timing
                    // but preserve existing stream objects when they match by index/type to maintain client tracking
                    source.MediaStreams = MergeStreamsPreservingObjects(source.MediaStreams, dbStreams);
                    _log.LogDebug("Jfresolve: Merged GetMediaSource MediaStreams with {Count} streams from database (including {SubtitleCount} subtitles) for {Name}", 
                        dbStreams.Count, dbStreams.Count(s => s.Type == MediaStreamType.Subtitle), item.Name);
                }
                // If no database streams, keep existing streams (shouldn't happen after probing, but be safe)
                else if (source.MediaStreams == null || !source.MediaStreams.Any())
                {
                    _log.LogWarning("Jfresolve: No database streams found for {Name} in GetMediaSource - item may need probing", item.Name);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - database access errors shouldn't break playback
                _log.LogWarning(ex, "Jfresolve: Error accessing database streams for {Name} in GetMediaSource, using existing streams", item.Name);
            }
            
            ApplyTrick(source);
        }
        
        return source;
    }

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream? GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream? GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string? cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    
    /// <summary>
    /// Merge streams from database with existing streams, preserving existing stream objects when indices match
    /// CRITICAL: The client tracks subtitle streams by their Index property for offset control
    /// We must preserve existing stream objects when indices match to maintain client tracking
    /// Database streams are used only when indices don't match or streams are missing
    /// </summary>
    private IReadOnlyList<MediaStream> MergeStreamsPreservingObjects(IReadOnlyList<MediaStream>? existingStreams, IReadOnlyList<MediaStream> dbStreams)
    {
        if (existingStreams == null || !existingStreams.Any())
        {
            return dbStreams;
        }

        var existingList = existingStreams.ToList();
        var dbList = dbStreams.ToList();
        var merged = new List<MediaStream>();
        var usedIndices = new HashSet<int>();
        
        // CRITICAL: Preserve existing stream objects when indices match
        // The client tracks subtitle streams by Index for offset control
        // Replacing stream objects breaks this tracking even if indices are the same
        foreach (var existing in existingList)
        {
            // Check if database has a stream with matching index and type
            var matchingDbStream = dbList.FirstOrDefault(s => 
                s.Index == existing.Index && 
                s.Type == existing.Type);
            
            if (matchingDbStream != null)
            {
                // Index and type match - preserve existing stream object to maintain client tracking
                // The existing stream object is what the client is tracking for offset control
                merged.Add(existing);
                usedIndices.Add(existing.Index);
            }
            else
            {
                // No match in database - keep existing stream
                merged.Add(existing);
                usedIndices.Add(existing.Index);
            }
        }
        
        // Add any database streams that don't have matches in existing streams
        // These are new streams that need to be added
        foreach (var dbStream in dbList)
        {
            if (!usedIndices.Contains(dbStream.Index))
            {
                merged.Add(dbStream);
                usedIndices.Add(dbStream.Index);
            }
        }
        
        // Sort by index to ensure consistent ordering
        // This is critical - the client relies on consistent index ordering for subtitle offset control
        return merged.OrderBy(s => s.Index).ToList();
    }
    
    /// <summary>
    /// Merge streams from database with existing streams, preserving indices for subtitle offset functionality
    /// This ensures subtitle streams maintain consistent indices across pause/resume
    /// Database streams are used as the source of truth since they have correct timing and index information from probing
    /// </summary>
    private IReadOnlyList<MediaStream> MergeStreamsPreservingIndices(IReadOnlyList<MediaStream>? existingStreams, IReadOnlyList<MediaStream> dbStreams)
    {
        if (existingStreams == null || !existingStreams.Any())
        {
            return dbStreams;
        }

        // Use database streams as the authoritative source - they have correct timing and index information from probing
        // This is critical for subtitle sync and offset functionality during transcoding
        // Database streams maintain consistent indices that the client uses for subtitle offset control
        var dbList = dbStreams.ToList();
        var existingList = existingStreams.ToList();
        var merged = new List<MediaStream>();
        var usedIndices = new HashSet<int>();
        
        // First, add all database streams - they are the source of truth
        // Database streams have correct timing information and consistent indices
        foreach (var dbStream in dbList)
        {
            merged.Add(dbStream);
            usedIndices.Add(dbStream.Index);
        }
        
        // Then, add any existing streams that don't conflict with database streams
        // This handles edge cases where Jellyfin might have added streams dynamically
        foreach (var existing in existingList)
        {
            // Only add if index doesn't conflict and it's a stream type we might need
            if (!usedIndices.Contains(existing.Index))
            {
                // Check if we already have this stream type from database
                var hasDbStreamOfType = dbList.Any(s => s.Type == existing.Type);
                
                // Add existing stream if:
                // 1. We don't have that stream type in database, OR
                // 2. It's a video/audio stream (critical for playback)
                if (!hasDbStreamOfType || existing.Type == MediaStreamType.Video || existing.Type == MediaStreamType.Audio)
                {
                    merged.Add(existing);
                    usedIndices.Add(existing.Index);
                }
            }
        }
        
        // Sort by index to ensure consistent ordering for subtitle offset functionality
        return merged.OrderBy(s => s.Index).ToList();
    }
    
    /// <summary>
    /// Check if an item was recently added (within the threshold time)
    /// </summary>
    private bool IsRecentlyAdded(BaseItem item)
    {
        if (item.DateCreated == default)
        {
            // If DateCreated is not set, assume it's not recently added
            return false;
        }
        
        var timeSinceCreation = DateTime.UtcNow - item.DateCreated.ToUniversalTime();
        var isRecent = timeSinceCreation.TotalMinutes <= RecentlyAddedThresholdMinutes;
        
        if (isRecent)
        {
            _log.LogDebug("Jfresolve: Item {Name} was created {Minutes} minutes ago (threshold: {Threshold} minutes)", 
                item.Name, timeSinceCreation.TotalMinutes, RecentlyAddedThresholdMinutes);
        }
        
        return isRecent;
    }
    
    /// <summary>
    /// Cleanup old entries from the probe cache
    /// </summary>
    private void CleanupProbeCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProbeCacheCleanup).TotalMinutes < ProbeCacheCleanupIntervalMinutes)
        {
            return;
        }
        
        _lastProbeCacheCleanup = now;
        var cutoffTime = now.AddMinutes(-RecentlyAddedThresholdMinutes * 2); // Keep entries for 2x the threshold
        
        var keysToRemove = _probedItems
            .Where(kvp => kvp.Value < cutoffTime)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in keysToRemove)
        {
            _probedItems.TryRemove(key, out _);
        }
        
        if (keysToRemove.Count > 0)
        {
            _log.LogDebug("Jfresolve: Cleaned up {Count} old probe cache entries (remaining: {Remaining})", 
                keysToRemove.Count, _probedItems.Count);
        }
    }
}
