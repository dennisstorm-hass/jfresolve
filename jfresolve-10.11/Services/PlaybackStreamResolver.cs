using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Configuration;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

public sealed record StreamResolveRequest(
    string Type,
    string Id,
    string? Season,
    string? Episode,
    string? Quality,
    int? Index,
    Guid? UserId,
    bool PreferHdrOverDolbyVision,
    bool ForceHls,
    bool PreferHlsForSeek);

/// <summary>
/// Resolves addon/TorBox playback URLs for Jellyfin media sources and the resolve API.
/// </summary>
public sealed class PlaybackStreamResolver
{
    private sealed class FailoverState
    {
        public int CurrentIndex { get; set; }
        public DateTime FirstAttempt { get; set; }
        public DateTime LastAttempt { get; set; }
        public int AttemptCount { get; set; }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StreamQualitySelector _qualitySelector;
    private readonly TorBoxStreamService _torBoxStreamService;
    private readonly CircuitBreaker _addonCircuitBreaker;
    private readonly ILogger<PlaybackStreamResolver> _logger;

    private readonly ConcurrentDictionary<string, FailoverState> _failoverCache = new();
    private readonly ConcurrentDictionary<string, (string Json, DateTime Expiry)> _streamMetadataCache = new();
    private readonly ConcurrentDictionary<string, (string RedirectUrl, DateTime Expiry)> _redirectUrlCache = new();
    private readonly ConcurrentDictionary<string, (string Url, string Container, DateTime Expiry)> _deliveryUrlCache = new();
    private static readonly TimeSpan DeliveryUrlCacheLifetime = TimeSpan.FromMinutes(45);
    private DateTime _lastStreamCacheCleanup = DateTime.UtcNow;
    private DateTime _lastRedirectUrlCacheCleanup = DateTime.UtcNow;

    public PlaybackStreamResolver(
        IHttpClientFactory httpClientFactory,
        StreamQualitySelector qualitySelector,
        TorBoxStreamService torBoxStreamService,
        CircuitBreakerFactory circuitBreakerFactory,
        ILogger<PlaybackStreamResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _qualitySelector = qualitySelector;
        _torBoxStreamService = torBoxStreamService;
        _logger = logger;
        _addonCircuitBreaker = circuitBreakerFactory.GetOrCreate("StremioAddon");
    }

    public async Task<string?> ResolveStreamUrlAsync(
        StreamResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Jfresolve: Plugin configuration is unavailable");
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
        {
            _logger.LogError("Jfresolve: Addon manifest URL not configured - cannot resolve stream");
            return null;
        }

        if (request.Type.Equals("series", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(request.Season) || string.IsNullOrWhiteSpace(request.Episode)))
        {
            _logger.LogWarning("Jfresolve: Missing season or episode for series");
            return null;
        }

        _logger.LogInformation(
            "Jfresolve: Resolving stream for {Type}/{Id} (Season: {Season}, Episode: {Episode}, Quality: {Quality}, Index: {Index})",
            request.Type,
            request.Id,
            request.Season ?? "N/A",
            request.Episode ?? "N/A",
            request.Quality ?? "default",
            request.Index?.ToString() ?? "0");

        var cacheKey = BuildDeliveryUrlCacheKey(request);
        var now = DateTime.UtcNow;

        if (TryGetCachedDeliveryUrl(cacheKey, now, out var cachedDelivery))
        {
            _logger.LogDebug(
                "Jfresolve: Using cached TorBox delivery URL for {Type}/{Id}",
                request.Type,
                request.Id);
            return cachedDelivery.Url;
        }

        var redirectCacheKey = BuildRedirectUrlCacheKey(request);

        CleanupRedirectUrlCacheIfNeeded();

        if (_redirectUrlCache.TryGetValue(redirectCacheKey, out var cachedRedirect) && cachedRedirect.Expiry > now)
        {
            _logger.LogDebug(
                "Jfresolve: Using cached addon redirect URL for {Type}/{Id}",
                request.Type,
                request.Id);
            return await NormalizeTorBoxUrlAsync(
                cachedRedirect.RedirectUrl,
                config.TorBoxApiKey,
                request,
                cancellationToken);
        }

        JsonDocument? streamsDoc = null;
        try
        {
            streamsDoc = await GetStreamsFromAddonWithDebridFallbackAsync(
                request.Type,
                request.Id,
                request.Season,
                request.Episode,
                config,
                cancellationToken);
            if (streamsDoc == null || streamsDoc.RootElement.GetArrayLength() == 0)
            {
                _logger.LogWarning("Jfresolve: No streams found for {Type}/{Id}", request.Type, request.Id);
                return null;
            }

            var redirectUrl = await SelectAndResolveStreamUrlWithFailoverAsync(
                request,
                streamsDoc.RootElement,
                config,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(redirectUrl))
                return null;

            if (TorBoxStreamService.ShouldCacheAddonRedirectUrl(redirectUrl))
            {
                var expiry = now.Add(Constants.RedirectUrlCacheExpiry);
                _redirectUrlCache.AddOrUpdate(
                    redirectCacheKey,
                    (redirectUrl, expiry),
                    (_, _) => (redirectUrl, expiry));
            }

            var deliveryUrl = await NormalizeTorBoxUrlAsync(
                redirectUrl,
                config.TorBoxApiKey,
                request,
                cancellationToken);
            CacheDeliveryUrl(cacheKey, deliveryUrl, redirectUrl);
            return deliveryUrl;
        }
        finally
        {
            streamsDoc?.Dispose();
        }
    }

    public async Task<DirectPlaybackTarget?> ResolveDirectTargetAsync(
        StreamResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildDeliveryUrlCacheKey(request);
        var now = DateTime.UtcNow;
        if (TryGetCachedDeliveryUrl(cacheKey, now, out var cached))
        {
            return new DirectPlaybackTarget(cached.Url, cached.Container, cached.Expiry);
        }

        var url = await ResolveStreamUrlAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (_deliveryUrlCache.TryGetValue(cacheKey, out cached) && cached.Expiry > now)
            return new DirectPlaybackTarget(cached.Url, cached.Container, cached.Expiry);

        var container = TorBoxStreamService.IsHlsUrl(url)
            ? "m3u8"
            : StreamContainerGuesser.FromUrl(url) ?? "mp4";
        return new DirectPlaybackTarget(url, container, now.Add(DeliveryUrlCacheLifetime));
    }

    private void CacheDeliveryUrl(string cacheKey, string? deliveryUrl, string? redirectUrl = null)
    {
        if (string.IsNullOrWhiteSpace(deliveryUrl) || !TorBoxStreamService.IsTorBoxDeliveryUrl(deliveryUrl))
            return;

        var container = TorBoxStreamService.IsHlsUrl(deliveryUrl)
            ? "m3u8"
            : StreamContainerGuesser.FromUrl(redirectUrl)
                ?? StreamContainerGuesser.FromUrl(deliveryUrl)
                ?? "mp4";
        _deliveryUrlCache[cacheKey] = (deliveryUrl, container, DateTime.UtcNow.Add(DeliveryUrlCacheLifetime));
    }

    public async Task<string?> NormalizeTorBoxUrlAsync(
        string redirectUrl,
        string? torBoxApiKey,
        StreamResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(redirectUrl))
            return redirectUrl;

        var preferHls = request.ForceHls || request.PreferHlsForSeek;
        if (request.ForceHls)
        {
            _logger.LogInformation("Jfresolve: Forcing TorBox HLS delivery for {Type}/{Id}", request.Type, request.Id);
        }

        var target = await _torBoxStreamService.TryResolveTorBoxStreamAsync(
            redirectUrl,
            torBoxApiKey,
            preferHls,
            request.ForceHls,
            cancellationToken);
        return target?.Url ?? redirectUrl;
    }

    private async Task<JsonDocument?> GetStreamsFromAddonWithDebridFallbackAsync(
        string type,
        string id,
        string? season,
        string? episode,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var hasTorBoxKey = !string.IsNullOrWhiteSpace(config.TorBoxApiKey);
        var hasRealDebridKey = !string.IsNullOrWhiteSpace(config.RealDebridApiKey);

        if (!hasTorBoxKey && !hasRealDebridKey)
        {
            var legacyManifest = UrlBuilder.NormalizeManifestUrl(config.AddonManifestUrl);
            return await GetStreamsFromAddonAsync(type, id, season, episode, legacyManifest, cancellationToken);
        }

        var baseManifest = UrlBuilder.NormalizeManifestUrl(UrlBuilder.StripDebridKeys(config.AddonManifestUrl));

        if (hasTorBoxKey)
        {
            var torBoxManifest = UrlBuilder.InjectDebridKey(
                baseManifest, Constants.TorBoxDebridParam, config.TorBoxApiKey);
            _logger.LogInformation("Jfresolve: Fetching streams via TorBox debrid provider");
            var torBoxStreams = await GetStreamsFromAddonAsync(type, id, season, episode, torBoxManifest, cancellationToken);
            if (torBoxStreams != null && torBoxStreams.RootElement.GetArrayLength() > 0)
                return torBoxStreams;

            torBoxStreams?.Dispose();
            _logger.LogWarning("Jfresolve: TorBox returned no streams for {Type}/{Id}, trying RealDebrid fallback", type, id);
        }

        if (hasRealDebridKey)
        {
            var realDebridManifest = UrlBuilder.InjectDebridKey(
                baseManifest, Constants.RealDebridDebridParam, config.RealDebridApiKey);
            _logger.LogInformation("Jfresolve: Fetching streams via RealDebrid debrid provider");
            return await GetStreamsFromAddonAsync(type, id, season, episode, realDebridManifest, cancellationToken);
        }

        return null;
    }

    private async Task<JsonDocument?> GetStreamsFromAddonAsync(
        string type,
        string id,
        string? season,
        string? episode,
        string manifestBase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestBase))
            return null;

        var streamUrl = BuildStreamUrl(manifestBase, type, id, season, episode);
        if (string.IsNullOrWhiteSpace(streamUrl))
            return null;

        var now = DateTime.UtcNow;
        if (_streamMetadataCache.TryGetValue(streamUrl, out var cached) && cached.Expiry > now)
        {
            try
            {
                var cachedDoc = JsonDocument.Parse(cached.Json);
                if (cachedDoc.RootElement.TryGetProperty("streams", out var cachedStreams)
                    && cachedStreams.GetArrayLength() > 0)
                {
                    var streamsJson = JsonSerializer.Serialize(cachedStreams);
                    cachedDoc.Dispose();
                    return JsonDocument.Parse(streamsJson);
                }

                cachedDoc.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jfresolve: Failed to parse cached stream metadata, fetching fresh");
            }
        }

        _logger.LogInformation("Jfresolve: Requesting stream from addon: {StreamUrl}", streamUrl);
        var addonHttpClient = _httpClientFactory.CreateClient("Jfresolve.Addon");
        addonHttpClient.Timeout = TimeSpan.FromSeconds(Constants.AddonRequestTimeoutSeconds);
        addonHttpClient.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent);
        var response = await _addonCircuitBreaker.ExecuteAsync(
            async () => await addonHttpClient.GetStringAsync(streamUrl, cancellationToken),
            async () =>
            {
                _logger.LogWarning("Circuit breaker open for Stremio addon, returning null");
                return (string?)null;
            });

        if (string.IsNullOrEmpty(response))
        {
            _logger.LogWarning("Jfresolve: No response from addon (circuit breaker may be open)");
            return null;
        }

        var json = JsonDocument.Parse(response);
        if (json.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var expiry = now.Add(Constants.StreamMetadataCacheExpiry);
            _streamMetadataCache.AddOrUpdate(streamUrl, (response, expiry), (_, _) => (response, expiry));
            CleanupStreamMetadataCacheIfNeeded();

            var streamsJson = JsonSerializer.Serialize(streams);
            json.Dispose();
            return JsonDocument.Parse(streamsJson);
        }

        json.Dispose();
        return null;
    }

    private async Task<string?> SelectAndResolveStreamUrlWithFailoverAsync(
        StreamResolveRequest request,
        JsonElement streams,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildFailoverCacheKey(request);
        var streamArray = streams.EnumerateArray().ToList();
        var maxAttempts = Math.Min(streamArray.Count, 5);
        var attemptedIndices = new HashSet<int>();
        var preferSeekableContainers = !string.IsNullOrWhiteSpace(config.TorBoxApiKey);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var effectiveIndex = DetermineFailoverIndex(request, cacheKey, streams, config);
            if (attemptedIndices.Contains(effectiveIndex))
                effectiveIndex = (effectiveIndex + 1) % streamArray.Count;
            attemptedIndices.Add(effectiveIndex);

            var selectedStream = _qualitySelector.SelectStreamByQuality(
                streams,
                config.PreferredQuality,
                request.Quality,
                effectiveIndex,
                request.PreferHdrOverDolbyVision,
                preferSeekableContainers);
            if (selectedStream == null)
                continue;

            if (!selectedStream.Value.TryGetProperty("url", out var urlProperty))
                continue;

            var redirectUrl = urlProperty.GetString();
            if (string.IsNullOrWhiteSpace(redirectUrl))
                continue;

            if (!StreamUrlValidation.IsValidStreamUrl(redirectUrl))
            {
                _logger.LogWarning(
                    "Jfresolve: Invalid or unsafe redirect URL at index {Index}: {RedirectUrl}",
                    effectiveIndex,
                    redirectUrl);
                continue;
            }

            if (attempt == 0 && streamArray.Count > 1 && !await TestStreamUrlAsync(redirectUrl, cancellationToken))
            {
                MarkStreamAsFailed(cacheKey, effectiveIndex);
                continue;
            }

            _logger.LogInformation(
                "Jfresolve: Resolved {Type}/{Id} to {RedirectUrl} (attempt {Attempt}, index {Index})",
                request.Type,
                request.Id,
                redirectUrl,
                attempt + 1,
                effectiveIndex);
            return redirectUrl;
        }

        _logger.LogError(
            "Jfresolve: Failed to find a valid stream after {Attempts} attempts for {Type}/{Id}",
            maxAttempts,
            request.Type,
            request.Id);
        return null;
    }

    private async Task<bool> TestStreamUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var testClient = _httpClientFactory.CreateClient("Jfresolve.Stream");
            testClient.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.Add("User-Agent", Constants.UserAgent);
            using var response = await testClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: Stream URL test failed for {Url}", url);
            return false;
        }
    }

    private void MarkStreamAsFailed(string cacheKey, int failedIndex)
    {
        if (_failoverCache.TryGetValue(cacheKey, out var state))
        {
            state.CurrentIndex = (failedIndex + 1) % 100;
            state.LastAttempt = DateTime.UtcNow;
            state.AttemptCount++;
        }
    }

    private int DetermineFailoverIndex(
        StreamResolveRequest request,
        string cacheKey,
        JsonElement streams,
        PluginConfiguration config)
    {
        var failoverEnabled = request.Type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? config.EnableMovieFailover
            : config.EnableShowFailover;
        if (!failoverEnabled)
            return request.Index ?? 0;

        var effectiveIndex = request.Index ?? 0;
        var streamArray = streams.EnumerateArray().ToList();
        var totalStreams = streamArray.Count;

        if (!string.IsNullOrEmpty(request.Quality))
        {
            var filteredStreams = _qualitySelector.FilterStreamsByQuality(streamArray, request.Quality);
            if (filteredStreams.Count > 0)
                totalStreams = filteredStreams.Count;
        }

        if (totalStreams <= 1)
            return effectiveIndex;

        var now = DateTime.UtcNow;
        var gracePeriod = TimeSpan.FromSeconds(config.FailoverGracePeriodSeconds);
        var resetWindow = TimeSpan.FromSeconds(config.FailoverWindowSeconds);

        if (_failoverCache.TryGetValue(cacheKey, out var state))
        {
            var timeSinceFirstAttempt = now - state.FirstAttempt;
            var timeSinceLastAttempt = now - state.LastAttempt;

            if (timeSinceLastAttempt > resetWindow)
            {
                _failoverCache.TryRemove(cacheKey, out _);
                _failoverCache[cacheKey] = new FailoverState
                {
                    CurrentIndex = effectiveIndex,
                    FirstAttempt = now,
                    LastAttempt = now,
                    AttemptCount = 1
                };
                return effectiveIndex;
            }

            if (timeSinceFirstAttempt < gracePeriod)
            {
                state.LastAttempt = now;
                state.AttemptCount++;
                return state.CurrentIndex;
            }

            effectiveIndex = state.CurrentIndex + 1;
            if (effectiveIndex >= totalStreams)
                effectiveIndex = 0;

            state.CurrentIndex = effectiveIndex;
            state.FirstAttempt = now;
            state.LastAttempt = now;
            state.AttemptCount++;
            return effectiveIndex;
        }

        _failoverCache[cacheKey] = new FailoverState
        {
            CurrentIndex = effectiveIndex,
            FirstAttempt = now,
            LastAttempt = now,
            AttemptCount = 1
        };
        return effectiveIndex;
    }

    private static string BuildStreamUrl(string manifestBase, string type, string id, string? season, string? episode)
    {
        manifestBase = UrlBuilder.IncreaseStreamLimit(manifestBase);
        type = StreamUrlValidation.SanitizeInput(type);
        id = StreamUrlValidation.SanitizeInput(id);
        season = string.IsNullOrWhiteSpace(season) ? null : StreamUrlValidation.SanitizeInput(season);
        episode = string.IsNullOrWhiteSpace(episode) ? null : StreamUrlValidation.SanitizeInput(episode);

        if (type.Equals("movie", StringComparison.OrdinalIgnoreCase))
            return $"{manifestBase}/stream/movie/{Uri.EscapeDataString(id)}.json";

        if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
                return string.Empty;
            return $"{manifestBase}/stream/series/{Uri.EscapeDataString(id)}:{Uri.EscapeDataString(season)}:{Uri.EscapeDataString(episode)}.json";
        }

        return $"{manifestBase}/stream/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json";
    }

    private static string BuildFailoverCacheKey(StreamResolveRequest request)
    {
        var key = $"{request.Type}:{request.Id}";
        if (!string.IsNullOrEmpty(request.Season) && !string.IsNullOrEmpty(request.Episode))
            key += $":{request.Season}:{request.Episode}";
        key += $":{request.Quality ?? "default"}";
        return key;
    }

    private static string BuildRedirectUrlCacheKey(StreamResolveRequest request)
    {
        var key = BuildDeliveryUrlCacheKey(request);
        if (request.UserId.HasValue)
            key += $":u{request.UserId.Value:N}";
        return key;
    }

    private static string BuildDeliveryUrlCacheKey(StreamResolveRequest request)
    {
        var key = BuildFailoverCacheKey(request);
        if (request.Index.HasValue)
            key += $":index{request.Index.Value}";
        key += request.PreferHdrOverDolbyVision ? ":hdr" : ":dv";
        key += request.ForceHls || request.PreferHlsForSeek ? ":hls" : ":direct";
        return key;
    }

    private bool TryGetCachedDeliveryUrl(
        string cacheKey,
        DateTime now,
        out (string Url, string Container, DateTime Expiry) cached)
    {
        if (_deliveryUrlCache.TryGetValue(cacheKey, out cached)
            && cached.Expiry > now
            && TorBoxStreamService.IsTorBoxDeliveryUrl(cached.Url))
        {
            return true;
        }

        cached = default;
        return false;
    }

    private void CleanupStreamMetadataCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastStreamCacheCleanup < Constants.StreamMetadataCacheCleanupInterval)
            return;

        _lastStreamCacheCleanup = now;
        foreach (var kvp in _streamMetadataCache.Where(kvp => kvp.Value.Expiry <= now).ToList())
            _streamMetadataCache.TryRemove(kvp.Key, out _);

        if (_streamMetadataCache.Count > Constants.StreamMetadataCacheMaxSize)
        {
            foreach (var kvp in _streamMetadataCache.OrderBy(x => x.Value.Expiry)
                         .Take(_streamMetadataCache.Count - Constants.StreamMetadataCacheMaxSize))
            {
                _streamMetadataCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void CleanupRedirectUrlCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastRedirectUrlCacheCleanup < Constants.RedirectUrlCacheCleanupInterval)
            return;

        _lastRedirectUrlCacheCleanup = now;
        foreach (var kvp in _redirectUrlCache.Where(kvp => kvp.Value.Expiry <= now).ToList())
            _redirectUrlCache.TryRemove(kvp.Key, out _);

        if (_redirectUrlCache.Count > Constants.RedirectUrlCacheMaxSize)
        {
            foreach (var kvp in _redirectUrlCache.OrderBy(x => x.Value.Expiry)
                         .Take(_redirectUrlCache.Count - Constants.RedirectUrlCacheMaxSize))
            {
                _redirectUrlCache.TryRemove(kvp.Key, out _);
            }
        }
    }
}
