using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Services;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Api;

/// <summary>Why the stream copy stopped.</summary>
internal enum StreamStopReason
{
    Completed,
    ClientDisconnect,
    UpstreamFailure
}

/// <summary>
/// API controller for Jfresolve plugin endpoints
/// Provides stream resolution for virtual items with automatic failover for dead links
/// </summary>
[ApiController]
[Route("Plugins/Jfresolve")]
[Route("Plugins/506f18b85dad4cd3b9a0f7ed933e9939")] // Alternative route using plugin GUID for image requests
public class JfresolveApiController : ControllerBase
{
    private readonly ILogger<JfresolveApiController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StreamQualitySelector _qualitySelector;
    private readonly Services.CircuitBreaker _addonCircuitBreaker;

    // Failover cache: tracks recent playback attempts with time windows
    private static readonly ConcurrentDictionary<string, FailoverState> _failoverCache = new();
    private static DateTime _lastCacheCleanup = DateTime.UtcNow;

    // Stream metadata cache: caches Stremio addon responses (stream lists) with TTL
    // Key: stream URL, Value: (Serialized JSON, ExpiryTime)
    private static readonly ConcurrentDictionary<string, (string Json, DateTime Expiry)> _streamMetadataCache = new();
    private static DateTime _lastStreamCacheCleanup = DateTime.UtcNow;
    
    // Redirect URL cache: caches resolved redirect URLs (from addon, before following redirects) to avoid re-resolving on every Range request
    // Key: cache key built from request parameters (type, id, season, episode, quality, index), Value: (Redirect URL, ExpiryTime)
    private static readonly ConcurrentDictionary<string, (string RedirectUrl, DateTime Expiry)> _redirectUrlCache = new();
    private static DateTime _lastRedirectUrlCacheCleanup = DateTime.UtcNow;
    
    // Resolved URL cache: caches final resolved stream URLs (after following redirects) to speed up resume
    // Key: original redirect URL, Value: (Final URL after redirects, ExpiryTime)
    private static readonly ConcurrentDictionary<string, (string FinalUrl, DateTime Expiry)> _resolvedUrlCache = new();
    private static DateTime _lastResolvedUrlCacheCleanup = DateTime.UtcNow;

    public JfresolveApiController(
        ILogger<JfresolveApiController> logger,
        IHttpClientFactory httpClientFactory,
        StreamQualitySelector qualitySelector,
        Services.CircuitBreakerFactory circuitBreakerFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _qualitySelector = qualitySelector;
        _addonCircuitBreaker = circuitBreakerFactory.GetOrCreate("StremioAddon");
    }

    /// <summary>
    /// Tracks failover state with time windows
    /// </summary>
    private class FailoverState
    {
        public int CurrentIndex { get; set; }
        public DateTime FirstAttempt { get; set; }
        public DateTime LastAttempt { get; set; }
        public int AttemptCount { get; set; }
    }

    /// <summary>
    /// Resolves a stream URL for a given movie or series
    /// Contacts the Stremio addon to get the real stream URL
    /// </summary>
    /// <param name="type">The content type (movie, series)</param>
    /// <param name="id">The IMDb or TMDB ID</param>
    /// <param name="season">Optional season number (for series)</param>
    /// <param name="episode">Optional episode number (for series)</param>
    /// <returns>Proxied stream or error</returns>
    [HttpGet("resolve/{type}/{id}")]
    [AllowAnonymous] // FFmpeg needs to access this endpoint without authentication
    public async Task<IActionResult> ResolveStream(
        string type,
        string id,
        [FromQuery] string? season = null,
        [FromQuery] string? episode = null,
        [FromQuery] string? quality = null,
        [FromQuery] int? index = null)
    {
        // Authorization check: Verify request is from trusted source (localhost or authenticated user)
        if (!IsRequestAuthorized())
        {
            _logger.LogWarning("Jfresolve: Unauthorized access attempt to ResolveStream from {RemoteIp}", 
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Unauthorized: Request must come from localhost or authenticated Jellyfin client");
        }

        // Validate and sanitize inputs
        var validationResult = ValidateAndSanitizeResolveStreamInputs(type, id, season, episode, quality, index);
        if (validationResult.ErrorResult != null)
        {
            return validationResult.ErrorResult;
        }

        // Use sanitized values
        type = validationResult.Type!;
        id = validationResult.Id!;
        season = validationResult.Season;
        episode = validationResult.Episode;
        quality = validationResult.Quality;
        index = validationResult.Index;

        _logger.LogInformation(
            "Jfresolve: ResolveStream called - Type: {Type}, Id: {Id}, Season: {Season}, Episode: {Episode}, Quality: {Quality}, Index: {Index}, RequestPath: {Path}, Range: {Range}",
            type, id, season ?? "N/A", episode ?? "N/A", quality ?? "N/A", index?.ToString() ?? "N/A",
            Request.Path, Request.Headers["Range"].ToString()
        );

        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Jfresolve: Plugin configuration is null");
            return BadRequest("Plugin not initialized");
        }

        // Check if addon manifest URL is configured
        if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
        {
            _logger.LogError("Jfresolve: Addon manifest URL not configured - cannot resolve stream");
            return NotFound("Addon manifest URL not configured. Please configure it in plugin settings.");
        }

        try
        {
            // Resolve the redirect URL (from cache or by fetching from addon)
            var redirectUrl = await ResolveRedirectUrlAsync(type, id, season, episode, quality, index, config);
            
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                return NotFound("No suitable stream found");
            }

            // Proxy the stream
            return await ProxyStreamAsync(redirectUrl, type, id);
        }
        catch (HttpRequestException ex)
        {
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Network error contacting addon for {Type}/{Id}", type, id);
                return StatusCode(502, "Network error: Unable to contact stream provider. Please try again later.");
            }
            else
            {
                _logger.LogWarning(ex, "Jfresolve: Network error during streaming for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
        }
        catch (TaskCanceledException ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Timeout (not user cancellation)
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Timeout contacting addon for {Type}/{Id}", type, id);
                return StatusCode(504, "Timeout: Stream provider did not respond in time. Please try again.");
            }
            else
            {
                _logger.LogWarning(ex, "Jfresolve: Timeout during streaming for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
        }
        catch (JsonException ex)
        {
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Invalid response format from addon for {Type}/{Id}", type, id);
                return StatusCode(502, "Invalid response: Stream provider returned invalid data. Please try again.");
            }
            else
            {
                _logger.LogWarning(ex, "Jfresolve: JSON parse error during streaming for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is System.Net.Sockets.SocketException socketEx && 
                                         (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                          socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown))
        {
            // Connection reset - check if response has started
            if (!Response.HasStarted)
            {
                _logger.LogWarning(ioEx, "Jfresolve: Connection reset for {Type}/{Id}", type, id);
                return StatusCode(502, "Connection error: Connection to stream provider was reset. Please try again.");
            }
            else
            {
                _logger.LogInformation(ioEx, "Jfresolve: Connection reset during streaming for {Type}/{Id} (normal client disconnect)", type, id);
                return new EmptyResult();
            }
        }
        catch (Exception ex)
        {
            // Only return error if response hasn't started
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Unexpected error resolving stream for {Type}/{Id}", type, id);
                return StatusCode(500, "Internal error: An unexpected error occurred. Please try again later.");
            }
            else
            {
                // Response already started - log and let connection close
                _logger.LogWarning(ex, "Jfresolve: Error during streaming after response started for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
        }
    }

    /// <summary>
    /// Result of input validation and sanitization
    /// </summary>
    private class ValidationResult
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public string? Season { get; set; }
        public string? Episode { get; set; }
        public string? Quality { get; set; }
        public int? Index { get; set; }
        public IActionResult? ErrorResult { get; set; }
    }

    /// <summary>
    /// Validates and sanitizes ResolveStream input parameters
    /// </summary>
    private ValidationResult ValidateAndSanitizeResolveStreamInputs(
        string type, string id, string? season, string? episode, string? quality, int? index)
    {
        var result = new ValidationResult();

        // Input validation and sanitization
        if (string.IsNullOrWhiteSpace(type))
        {
            _logger.LogWarning("Jfresolve: Invalid request - type parameter is empty");
            result.ErrorResult = BadRequest("Type parameter is required");
            return result;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Jfresolve: Invalid request - id parameter is empty");
            result.ErrorResult = BadRequest("Id parameter is required");
            return result;
        }

        // Sanitize inputs - remove any potentially dangerous characters
        result.Type = SanitizeInput(type);
        result.Id = SanitizeInput(id);
        result.Season = string.IsNullOrWhiteSpace(season) ? null : SanitizeInput(season);
        result.Episode = string.IsNullOrWhiteSpace(episode) ? null : SanitizeInput(episode);
        result.Quality = string.IsNullOrWhiteSpace(quality) ? null : SanitizeInput(quality);
        result.Index = index;

        // Validate type is one of the allowed values
        if (!result.Type.Equals("movie", StringComparison.OrdinalIgnoreCase) && 
            !result.Type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Jfresolve: Invalid request - unsupported type: {Type}", result.Type);
            result.ErrorResult = BadRequest("Type must be 'movie' or 'series'");
            return result;
        }

        // Validate IMDB ID format
        if (!IsValidImdbId(result.Id))
        {
            _logger.LogWarning("Jfresolve: Invalid request - invalid IMDB ID format: {Id}", result.Id);
            result.ErrorResult = BadRequest("Invalid IMDB ID format. Expected format: tt1234567");
            return result;
        }

        // Validate season and episode for series
        if (result.Type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(result.Season) || !IsValidSeasonOrEpisode(result.Season))
            {
                _logger.LogWarning("Jfresolve: Invalid request - invalid season: {Season}", result.Season);
                result.ErrorResult = BadRequest("Season must be a positive number between 1 and 999");
                return result;
            }
            if (string.IsNullOrWhiteSpace(result.Episode) || !IsValidSeasonOrEpisode(result.Episode))
            {
                _logger.LogWarning("Jfresolve: Invalid request - invalid episode: {Episode}", result.Episode);
                result.ErrorResult = BadRequest("Episode must be a positive number between 1 and 999");
                return result;
            }
        }

        // Validate index is within reasonable bounds
        if (result.Index.HasValue && (result.Index.Value < 0 || result.Index.Value > 100))
        {
            _logger.LogWarning("Jfresolve: Invalid request - index out of bounds: {Index}", result.Index.Value);
            result.ErrorResult = BadRequest("Index must be between 0 and 100");
            return result;
        }

        return result;
    }

    /// <summary>
    /// Resolves the redirect URL from cache or by fetching from addon
    /// </summary>
    private async Task<string?> ResolveRedirectUrlAsync(
        string type, string id, string? season, string? episode, string? quality, int? index,
        Configuration.PluginConfiguration config)
    {
        // Validate series parameters
        if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
            {
                _logger.LogWarning("Jfresolve: Missing season or episode for series");
                return null;
            }
        }

        _logger.LogInformation(
            "Jfresolve: Resolving stream for {Type}/{Id} (Season: {Season}, Episode: {Episode})",
            type, id, season ?? "N/A", episode ?? "N/A"
        );

        // Check cache for resolved redirect URL first (avoids re-resolving on every Range request)
        var redirectCacheKey = BuildRedirectUrlCacheKey(type, id, season, episode, quality, index);
        var now = DateTime.UtcNow;
        CleanupRedirectUrlCacheIfNeeded();
        
        if (_redirectUrlCache.TryGetValue(redirectCacheKey, out var cachedRedirect) && cachedRedirect.Expiry > now)
        {
            _logger.LogDebug("Jfresolve: Using cached redirect URL for {Type}/{Id} (Season: {Season}, Episode: {Episode})", 
                type, id, season ?? "N/A", episode ?? "N/A");
            return cachedRedirect.RedirectUrl;
        }

        // Get streams from addon (returns JsonDocument that must be kept alive)
        JsonDocument? streamsDoc = null;
        try
        {
            streamsDoc = await GetStreamsFromAddonAsync(type, id, season, episode, config);
            if (streamsDoc == null || streamsDoc.RootElement.GetArrayLength() == 0)
            {
                _logger.LogWarning("Jfresolve: No streams found for {Type}/{Id}", type, id);
                return null;
            }

            // Select stream using quality selector and failover logic with immediate failover on HTTP errors
            // Keep streamsDoc alive until we're done using the streams element
            var redirectUrl = await SelectAndResolveStreamUrlWithFailoverAsync(
                type, id, season, episode, quality, index, streamsDoc.RootElement, config);
            
            // Cache the resolved redirect URL for future Range requests
            if (!string.IsNullOrWhiteSpace(redirectUrl))
            {
                var expiry = now.Add(Constants.RedirectUrlCacheExpiry);
                _redirectUrlCache.AddOrUpdate(redirectCacheKey, (redirectUrl, expiry), (key, oldValue) => (redirectUrl, expiry));
                _logger.LogDebug("Jfresolve: Cached redirect URL for {Type}/{Id} (Season: {Season}, Episode: {Episode})", 
                    type, id, season ?? "N/A", episode ?? "N/A");
            }

            return redirectUrl;
        }
        finally
        {
            // Dispose the JsonDocument after we're done with it
            streamsDoc?.Dispose();
        }
    }

    /// <summary>
    /// Gets streams from the Stremio addon with caching
    /// Returns JsonDocument that must be disposed by the caller
    /// </summary>
    private async Task<JsonDocument?> GetStreamsFromAddonAsync(
        string type,
        string id,
        string? season,
        string? episode,
        Configuration.PluginConfiguration config)
        {
            // Normalize the manifest URL (remove stremio://, convert to https://)
            var manifestBase = UrlBuilder.NormalizeManifestUrl(config.AddonManifestUrl);

            // Build the stream endpoint URL
        string streamUrl = BuildStreamUrl(manifestBase, type, id, season, episode);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
        }

        // Check cache first
        var now = DateTime.UtcNow;
        if (_streamMetadataCache.TryGetValue(streamUrl, out var cached) && cached.Expiry > now)
        {
            _logger.LogDebug("Jfresolve: Using cached stream metadata for {StreamUrl}", streamUrl);
            try
            {
                // Parse cached JSON and extract streams array
                var cachedDoc = JsonDocument.Parse(cached.Json);
                if (cachedDoc.RootElement.TryGetProperty("streams", out var cachedStreams) && cachedStreams.GetArrayLength() > 0)
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
                // Fall through to fetch fresh data
            }
            }

            _logger.LogInformation("Jfresolve: Requesting stream from addon: {StreamUrl}", streamUrl);

            // Call the Stremio addon to get the stream (with circuit breaker protection)
        var addonHttpClient = _httpClientFactory.CreateClient("Jfresolve.Addon");
        addonHttpClient.Timeout = TimeSpan.FromSeconds(Constants.AddonRequestTimeoutSeconds);
        addonHttpClient.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent);
            var response = await _addonCircuitBreaker.ExecuteAsync(
                async () => await addonHttpClient.GetStringAsync(streamUrl),
                async () => { _logger.LogWarning("Circuit breaker open for Stremio addon, returning null"); return (string?)null; });
            
            if (string.IsNullOrEmpty(response))
            {
                _logger.LogWarning("Jfresolve: No response from addon (circuit breaker may be open)");
                return null;
            }

            // Parse the JSON response
        var json = JsonDocument.Parse(response);
        
        // Extract the streams array and create a new JsonDocument containing only the streams
        // This allows us to dispose the original document while keeping the streams data alive
        if (json.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
            // Cache the full response for future requests
            var expiry = now.Add(Constants.StreamMetadataCacheExpiry);
            _streamMetadataCache.AddOrUpdate(streamUrl, (response, expiry), (key, oldValue) => (response, expiry));
            
            // Cleanup old cache entries if needed
            CleanupStreamMetadataCacheIfNeeded();

            // Clone the streams array into a new JsonDocument
            // This creates an independent copy that can outlive the original document
            var streamsJson = JsonSerializer.Serialize(streams);
            json.Dispose(); // Dispose the original document
            return JsonDocument.Parse(streamsJson);
        }

        json.Dispose();
        return null;
    }

    /// <summary>
    /// Cleans up expired entries from the stream metadata cache
    /// </summary>
    private static void CleanupStreamMetadataCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        
        // Only run cleanup periodically to avoid performance impact
        if (now - _lastStreamCacheCleanup < Constants.StreamMetadataCacheCleanupInterval)
        {
            return;
        }

        _lastStreamCacheCleanup = now;
        var keysToRemove = new List<string>();

        // Remove expired entries
        foreach (var kvp in _streamMetadataCache)
        {
            if (kvp.Value.Expiry <= now)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _streamMetadataCache.TryRemove(key, out _);
        }

        // If still too large, remove oldest entries
        if (_streamMetadataCache.Count > Constants.StreamMetadataCacheMaxSize)
        {
            var entriesToRemove = _streamMetadataCache.Count - Constants.StreamMetadataCacheMaxSize;
            var sortedByExpiry = _streamMetadataCache.OrderBy(kvp => kvp.Value.Expiry).Take(entriesToRemove);
            
            foreach (var kvp in sortedByExpiry)
            {
                _streamMetadataCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Cleans up expired entries from the resolved URL cache
    /// </summary>
    private static void CleanupRedirectUrlCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        
        // Only run cleanup periodically to avoid performance impact
        if (now - _lastRedirectUrlCacheCleanup < Constants.RedirectUrlCacheCleanupInterval)
        {
            return;
        }

        _lastRedirectUrlCacheCleanup = now;
        var keysToRemove = new List<string>();

        // Remove expired entries
        foreach (var kvp in _redirectUrlCache)
        {
            if (kvp.Value.Expiry <= now)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _redirectUrlCache.TryRemove(key, out _);
        }

        // If cache is still too large, remove oldest entries
        if (_redirectUrlCache.Count > Constants.RedirectUrlCacheMaxSize)
        {
            var entriesToRemove = _redirectUrlCache.Count - Constants.RedirectUrlCacheMaxSize;
            var sortedByExpiry = _redirectUrlCache.OrderBy(kvp => kvp.Value.Expiry).Take(entriesToRemove);
            
            foreach (var kvp in sortedByExpiry)
            {
                _redirectUrlCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static void CleanupResolvedUrlCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        
        // Only run cleanup periodically to avoid performance impact
        if (now - _lastResolvedUrlCacheCleanup < Constants.ResolvedUrlCacheCleanupInterval)
        {
            return;
        }

        _lastResolvedUrlCacheCleanup = now;
        var keysToRemove = new List<string>();

        // Remove expired entries
        foreach (var kvp in _resolvedUrlCache)
        {
            if (kvp.Value.Expiry <= now)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _resolvedUrlCache.TryRemove(key, out _);
        }

        // If still too large, remove oldest entries
        if (_resolvedUrlCache.Count > Constants.ResolvedUrlCacheMaxSize)
        {
            var entriesToRemove = _resolvedUrlCache.Count - Constants.ResolvedUrlCacheMaxSize;
            var sortedByExpiry = _resolvedUrlCache.OrderBy(kvp => kvp.Value.Expiry).Take(entriesToRemove);
            
            foreach (var kvp in sortedByExpiry)
            {
                _resolvedUrlCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Builds the stream URL for the addon based on content type
    /// All inputs should already be sanitized before calling this method
    /// </summary>
    private string BuildStreamUrl(string manifestBase, string type, string id, string? season, string? episode)
    {
        // Ensure inputs are sanitized (defense in depth)
        type = SanitizeInput(type);
        id = SanitizeInput(id);
        season = string.IsNullOrWhiteSpace(season) ? null : SanitizeInput(season);
        episode = string.IsNullOrWhiteSpace(episode) ? null : SanitizeInput(episode);
        
        if (type.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            // Use Uri.EscapeDataString for additional safety in URL construction
            return $"{manifestBase}/stream/movie/{Uri.EscapeDataString(id)}.json";
        }
        else if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
            {
                return string.Empty; // Will be caught by caller
            }
            // Use Uri.EscapeDataString for additional safety in URL construction
            return $"{manifestBase}/stream/series/{Uri.EscapeDataString(id)}:{Uri.EscapeDataString(season)}:{Uri.EscapeDataString(episode)}.json";
        }
        else
        {
            // Use Uri.EscapeDataString for additional safety in URL construction
            return $"{manifestBase}/stream/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json";
            }
    }

    /// <summary>
    /// Selects a stream and resolves its URL using quality selector and failover logic
    /// </summary>
    private async Task<string?> SelectAndResolveStreamUrlAsync(
        string type,
        string id,
        string? season,
        string? episode,
        string? quality,
        int? index,
        JsonElement streams,
        Configuration.PluginConfiguration config)
    {
            // FAILOVER LOGIC: Determine effective index with time-window based retry for dead links
            var cacheKey = BuildFailoverCacheKey(type, id, season, episode, quality);
            int effectiveIndex = DetermineFailoverIndex(cacheKey, index, quality, streams, config.PreferredQuality, type);

            // Select the stream using failover-adjusted index
        var selectedStream = _qualitySelector.SelectStreamByQuality(streams, config.PreferredQuality, quality, effectiveIndex);
            if (selectedStream == null)
            {
                _logger.LogWarning("Jfresolve: Could not select a stream for {Type}/{Id}", type, id);
            return null;
            }

            if (!selectedStream.Value.TryGetProperty("url", out var urlProperty))
            {
                _logger.LogWarning("Jfresolve: No URL property in stream response");
            return null;
            }

            var redirectUrl = urlProperty.GetString();
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                _logger.LogWarning("Jfresolve: Empty stream URL received");
            return null;
            }

            _logger.LogInformation("Jfresolve: Resolved {Type}/{Id} to {RedirectUrl}", type, id, redirectUrl);

        // Validate URL to prevent SSRF attacks
        if (!IsValidStreamUrl(redirectUrl))
            {
            _logger.LogWarning("Jfresolve: Invalid or unsafe redirect URL: {RedirectUrl}", redirectUrl);
            return null;
        }

        return redirectUrl;
    }

    /// <summary>
    /// Selects a stream with immediate failover on HTTP errors (4xx, 5xx)
    /// Tries the next quality version if the current one fails
    /// </summary>
    private async Task<string?> SelectAndResolveStreamUrlWithFailoverAsync(
        string type,
        string id,
        string? season,
        string? episode,
        string? quality,
        int? index,
        JsonElement streams,
        Configuration.PluginConfiguration config)
    {
        var cacheKey = BuildFailoverCacheKey(type, id, season, episode, quality);
        var streamArray = streams.EnumerateArray().ToList();
        var maxAttempts = Math.Min(streamArray.Count, 5); // Limit to 5 attempts to avoid infinite loops
        var attemptedIndices = new HashSet<int>();
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Determine failover index
            int effectiveIndex = DetermineFailoverIndex(cacheKey, index, quality, streams, config.PreferredQuality, type);
            
            // Skip if we've already tried this index
            if (attemptedIndices.Contains(effectiveIndex))
            {
                // Try next index
                effectiveIndex = (effectiveIndex + 1) % streamArray.Count;
            }
            attemptedIndices.Add(effectiveIndex);
            
            // Select the stream
            var selectedStream = _qualitySelector.SelectStreamByQuality(streams, config.PreferredQuality, quality, effectiveIndex);
            if (selectedStream == null)
            {
                _logger.LogWarning("Jfresolve: Could not select stream at index {Index} for {Type}/{Id}", effectiveIndex, type, id);
                continue; // Try next stream
            }

            if (!selectedStream.Value.TryGetProperty("url", out var urlProperty))
            {
                _logger.LogWarning("Jfresolve: No URL property in stream response at index {Index}", effectiveIndex);
                continue; // Try next stream
            }

            var redirectUrl = urlProperty.GetString();
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                _logger.LogWarning("Jfresolve: Empty stream URL at index {Index}", effectiveIndex);
                continue; // Try next stream
            }

            // Validate URL to prevent SSRF attacks
            if (!IsValidStreamUrl(redirectUrl))
            {
                _logger.LogWarning("Jfresolve: Invalid or unsafe redirect URL at index {Index}: {RedirectUrl}", effectiveIndex, redirectUrl);
                continue; // Try next stream
            }

            // Test the stream URL with a HEAD request only if we have multiple streams and this is the first attempt
            // This helps avoid dead links but adds minimal overhead
            if (attempt == 0 && streamArray.Count > 1)
            {
                var isValid = await TestStreamUrlAsync(redirectUrl);
                if (!isValid)
                {
                    _logger.LogWarning("Jfresolve: Stream URL at index {Index} failed validation, trying next stream", effectiveIndex);
                    // Mark this index as failed in failover cache for future requests
                    MarkStreamAsFailed(cacheKey, effectiveIndex);
                    continue; // Try next stream
                }
            }

            _logger.LogInformation("Jfresolve: Resolved {Type}/{Id} to {RedirectUrl} (attempt {Attempt}, index {Index})", 
                type, id, redirectUrl, attempt + 1, effectiveIndex);
            return redirectUrl;
        }

        _logger.LogError("Jfresolve: Failed to find a valid stream after {Attempts} attempts for {Type}/{Id}", 
            maxAttempts, type, id);
        return null;
    }

    /// <summary>
    /// Tests if a stream URL is accessible by making a HEAD request
    /// Returns true if the URL is accessible (2xx status), false otherwise
    /// </summary>
    private async Task<bool> TestStreamUrlAsync(string url)
    {
        try
        {
            var testClient = _httpClientFactory.CreateClient("Jfresolve.Stream");
            testClient.Timeout = TimeSpan.FromSeconds(10); // Short timeout for testing
            
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.Add("User-Agent", Constants.UserAgent);
            
            var response = await testClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            // Consider 2xx and 3xx as valid (redirects are OK)
            var isValid = (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
            
            if (!isValid)
            {
                _logger.LogDebug("Jfresolve: Stream URL test failed with status {StatusCode}: {Url}", response.StatusCode, url);
            }
            
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: Stream URL test failed for {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Marks a stream index as failed in the failover cache to avoid retrying it immediately
    /// </summary>
    private void MarkStreamAsFailed(string cacheKey, int failedIndex)
    {
        if (_failoverCache.TryGetValue(cacheKey, out var state))
        {
            // Update the state to indicate this index failed
            state.CurrentIndex = (failedIndex + 1) % 100; // Move to next index (wrapped)
            state.LastAttempt = DateTime.UtcNow;
            state.AttemptCount++;
        }
    }

    /// <summary>
    /// Proxies the stream from the redirect URL to the client
    /// </summary>
    private async Task<IActionResult> ProxyStreamAsync(string redirectUrl, string type, string id)
    {
            // Jellyfin 10.11.6 compatibility: Proxy the stream instead of redirecting
            // FFmpeg in 10.11.6 doesn't properly follow HTTP redirects from plugin endpoints
            // By proxying, FFmpeg gets the stream directly without needing to follow redirects
            // IMPORTANT: Must support HTTP Range requests (206 Partial Content) for FFmpeg seeking
            try
            {
                _logger.LogInformation("Jfresolve: Proxying stream from {RedirectUrl}", redirectUrl);
                
            // Disable response buffering for optimal streaming performance
            Response.Headers["Cache-Control"] = Constants.CacheControlNoCache;
            Response.Headers["Pragma"] = Constants.PragmaNoCache;
            Response.Headers["Expires"] = Constants.ExpiresZero;
            
            var streamHttpClient = _httpClientFactory.CreateClient("Jfresolve.Stream");
            // Use a very long timeout (4 hours) to handle long movies/episodes without interruption
            // The timeout applies to the entire operation including all read operations
            streamHttpClient.Timeout = TimeSpan.FromHours(Constants.StreamRequestTimeoutHours);
                
                // Handle HTTP Range requests for seeking (required by FFmpeg)
                var rangeHeader = Request.Headers["Range"].ToString();
            long? rangeStart = null;
                
            // Parse range header to extract start position for workaround
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    _logger.LogDebug("Jfresolve: Range request detected: {Range}", rangeHeader);
                rangeStart = ParseRangeStart(rangeHeader);
            }
                
                // Stream the content directly to the response
            // Use RequestAborted cancellation token so upstream request is cancelled when client disconnects
            // This ensures immediate cleanup when user stops playback
            var cancellationToken = HttpContext.RequestAborted;
            
            // Check cache for final resolved URL (after redirects) to speed up resume
            string? finalUrl = null;
            var now = DateTime.UtcNow;
            CleanupResolvedUrlCacheIfNeeded();
            
            if (_resolvedUrlCache.TryGetValue(redirectUrl, out var cachedResolved) && cachedResolved.Expiry > now)
            {
                finalUrl = cachedResolved.FinalUrl;
                _logger.LogDebug("Jfresolve: Using cached resolved URL for {RedirectUrl} -> {FinalUrl}", redirectUrl, finalUrl);
            }
            
            HttpResponseMessage? streamResponse = null;
            HttpResponseMessage? initialResponse = null;
            
            if (finalUrl != null)
            {
                // Use cached final URL - skip redirect following for faster resume
                // Retry initial connection on transient failures
                streamResponse = await ExecuteStreamRequestWithRetryAsync(
                    streamHttpClient,
                    () =>
                    {
                        var cachedRequest = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                        if (!string.IsNullOrEmpty(rangeHeader))
                        {
                            cachedRequest.Headers.Add("Range", rangeHeader);
                        }
                        return cachedRequest;
                    },
                    $"cached stream URL {finalUrl}",
                    cancellationToken);
                
                if (streamResponse == null)
                {
                    _logger.LogError("Jfresolve: Failed to connect to cached stream URL after retries: {FinalUrl}", finalUrl);
                    return StatusCode(502, "Failed to connect to stream after retries");
                }
            }
            else
            {
                // Follow redirects to get final URL (first time or cache expired)
                // Retry initial connection on transient failures
                // Create a new request message for each retry attempt
                initialResponse = await ExecuteStreamRequestWithRetryAsync(
                    streamHttpClient,
                    () =>
                    {
                        var retryRequest = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                        if (!string.IsNullOrEmpty(rangeHeader))
                        {
                            retryRequest.Headers.Add("Range", rangeHeader);
                        }
                        return retryRequest;
                    },
                    $"initial redirect URL {redirectUrl}",
                    cancellationToken);
                
                if (initialResponse == null)
                {
                    _logger.LogError("Jfresolve: Failed to connect to redirect URL after retries: {RedirectUrl}", redirectUrl);
                    return StatusCode(502, "Failed to connect to stream URL after retries");
                }
                
                // Handle redirects (302, 301, etc.) - follow up to 5 redirects
                // FollowRedirectsAsync will dispose initialResponse if redirects are followed
                streamResponse = await FollowRedirectsAsync(streamHttpClient, initialResponse, redirectUrl, 5, cancellationToken);
                if (streamResponse == null)
                {
                    initialResponse?.Dispose(); // Dispose if redirects failed
                    _logger.LogError("Jfresolve: Failed to follow redirects for {RedirectUrl}", redirectUrl);
                    return StatusCode(502, "Failed to resolve stream URL after redirects");
                }
                
                // Cache the final resolved URL for faster resume
                finalUrl = streamResponse.RequestMessage?.RequestUri?.ToString();
                if (!string.IsNullOrEmpty(finalUrl) && finalUrl != redirectUrl)
                {
                    _resolvedUrlCache.TryAdd(redirectUrl, (finalUrl, now.Add(Constants.ResolvedUrlCacheExpiry)));
                    _logger.LogDebug("Jfresolve: Cached resolved URL for {RedirectUrl} -> {FinalUrl}", redirectUrl, finalUrl);
                }
            }
            
            // Use the final response (after following redirects or from cache)
            using var finalStreamResponse = streamResponse;
            
            // Check response status and handle errors appropriately
            if (!finalStreamResponse.IsSuccessStatusCode)
            {
                initialResponse?.Dispose();
                return HandleStreamError(finalStreamResponse, redirectUrl, type, id);
            }
            
            // Dispose initialResponse if it was created (not used when cache hit)
            initialResponse?.Dispose();

            // Copy response headers
            CopyStreamResponseHeaders(finalStreamResponse, rangeHeader, rangeStart);

            // Build delegate to reconnect from byte offset when upstream drops mid-stream.
            // Kodi/JellyCon (https://github.com/jellyfin/jellycon): JellyCon passes the play URL to Kodi via
            // list_item.setPath(playurl); Kodi then opens that URL and reads the stream. Playback can drop
            // every ~10 min on Kodi (upstream limit or Kodi closing the connection). Transparent reconnect
            // keeps the stream alive without the client seeing an error.
            string? urlForReconnect = finalUrl ?? redirectUrl;
            Func<long, Task<(Stream? stream, IDisposable? toDispose)>>? getStreamFromOffset = null;
            if (!string.IsNullOrEmpty(urlForReconnect))
            {
                getStreamFromOffset = async (offset) =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, urlForReconnect);
                    req.Headers.Add("Range", $"bytes={offset}-");
                    var resp = await streamHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (resp == null || !resp.IsSuccessStatusCode)
                    {
                        resp?.Dispose();
                        return (null, null);
                    }
                    var stream = await resp.Content.ReadAsStreamAsync();
                    return (stream, resp);
                };
            }

            // Stream the content (with transparent reconnect when upstream drops)
            var (_, stopReason) = await StreamContentAsync(finalStreamResponse, type, id, rangeStart, cancellationToken, getStreamFromOffset);
            if (stopReason == StreamStopReason.UpstreamFailure)
            {
                _logger.LogWarning("Jfresolve: Stream ended after upstream failure for {Type}/{Id} (reconnect exhausted or unavailable)", type, id);
            }
            
            return new EmptyResult();
        }
        catch (HttpRequestException ex)
        {
            // Only return error if response hasn't started yet
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Network error proxying stream from {RedirectUrl}", redirectUrl);
                return StatusCode(502, "Network error: Unable to connect to stream server");
            }
            else
            {
                // Response already started - log and let connection close
                _logger.LogWarning(ex, "Jfresolve: Network error during streaming after response started for {RedirectUrl}", redirectUrl);
                return new EmptyResult();
            }
        }
        catch (TaskCanceledException ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Timeout (not user cancellation)
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Timeout connecting to stream from {RedirectUrl}", redirectUrl);
                return StatusCode(504, "Gateway timeout: Stream server did not respond in time");
            }
            else
            {
                _logger.LogWarning(ex, "Jfresolve: Timeout during streaming for {RedirectUrl}", redirectUrl);
                return new EmptyResult();
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is System.Net.Sockets.SocketException socketEx && 
                                         (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                          socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown))
        {
            // Connection reset before or during streaming
            if (!Response.HasStarted)
            {
                _logger.LogWarning(ioEx, "Jfresolve: Connection reset before streaming started for {RedirectUrl}", redirectUrl);
                return StatusCode(502, "Connection reset: Stream server closed the connection");
            }
            else
            {
                _logger.LogInformation(ioEx, "Jfresolve: Connection reset during streaming for {RedirectUrl} (normal client disconnect)", redirectUrl);
                return new EmptyResult();
            }
        }
        catch (InvalidOperationException ioEx) when (ioEx.Message.Contains("Content-Length mismatch", StringComparison.OrdinalIgnoreCase))
        {
            // Client disconnected mid-stream after we had set Content-Length; Kestrel throws when we don't write all bytes.
            // Treat as normal disconnect so it doesn't surface as an unhandled exception.
            _logger.LogInformation(ioEx, "Jfresolve: Client disconnected during streaming for {RedirectUrl} (Content-Length mismatch)", redirectUrl);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            // Unexpected errors
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Unexpected error proxying stream from {RedirectUrl}", redirectUrl);
                return StatusCode(500, "Internal error: An unexpected error occurred while streaming");
            }
            else
            {
                _logger.LogError(ex, "Jfresolve: Unexpected error during streaming for {RedirectUrl}", redirectUrl);
                return new EmptyResult();
            }
        }
    }

    /// <summary>
    /// Copies response headers from the stream response to the client response
    /// </summary>
    private void CopyStreamResponseHeaders(HttpResponseMessage streamResponse, string? rangeHeader, long? rangeStart)
    {
        // If upstream server doesn't support range requests (returns 200 instead of 206),
        // but client requested a range, we need to handle it ourselves
        bool upstreamSupportsRange = streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent;
        bool clientRequestedRange = !string.IsNullOrEmpty(rangeHeader) && rangeStart.HasValue;
        
        if (clientRequestedRange && !upstreamSupportsRange)
        {
            // Upstream doesn't support range requests - we'll skip bytes ourselves
            // Return 206 Partial Content to client even though upstream returned 200
            Response.StatusCode = 206;
            
            // Calculate Content-Range header based on upstream Content-Length
            // Format: "bytes start-end/total" or "bytes start-*/total" if we don't know end
            long? totalLength = streamResponse.Content.Headers.ContentLength;
            if (totalLength.HasValue && rangeStart.HasValue)
            {
                long start = rangeStart.Value;
                long end = totalLength.Value - 1; // Content-Range end is inclusive
                long rangeLength = totalLength.Value - start;
                
                // Set Content-Range header: "bytes start-end/total"
                Response.Headers["Content-Range"] = $"bytes {start}-{end}/{totalLength.Value}";
                
                // Set Content-Length so clients (e.g. Kodi) can resume via Range requests. On client disconnect we abort the connection to avoid Kestrel's Content-Length mismatch.
                Response.ContentLength = rangeLength;
                
                _logger.LogDebug("Jfresolve: Upstream server doesn't support range requests, implementing workaround (Range: bytes {Start}-{End}/{Total}, Content-Length: {Length})", 
                    start, end, totalLength.Value, rangeLength);
            }
            else if (rangeStart.HasValue)
            {
                // Don't know total length - can't set proper Content-Range
                // This is okay, we'll still skip bytes and stream the rest
                _logger.LogDebug("Jfresolve: Upstream server doesn't support range requests, implementing workaround (skipping {Bytes} bytes, unknown total length)", rangeStart.Value);
                }
        }
        else
        {
            // Copy status code (206 for partial content if range was requested and supported)
            Response.StatusCode = (int)streamResponse.StatusCode;
                
                // Copy Content-Range header if present (for 206 Partial Content responses)
                // Content-Range can be in response headers or content headers depending on the server
                string? contentRangeValue = null;
                if (streamResponse.Headers.TryGetValues("Content-Range", out var responseContentRange))
                {
                    contentRangeValue = responseContentRange.FirstOrDefault();
                }
                else if (streamResponse.Content.Headers.TryGetValues("Content-Range", out var contentContentRange))
                {
                    contentRangeValue = contentContentRange.FirstOrDefault();
                }
                
                if (!string.IsNullOrEmpty(contentRangeValue))
                {
                    Response.Headers["Content-Range"] = contentRangeValue;
                }
                
            // Set Content-Length for 206 so clients can resume. On client disconnect we abort the connection to avoid Content-Length mismatch.
            if (upstreamSupportsRange && 
                streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent && 
                streamResponse.Content.Headers.ContentLength.HasValue)
            {
                Response.ContentLength = streamResponse.Content.Headers.ContentLength.Value;
            }
        }

        // Copy headers that might be important for streaming
        if (streamResponse.Content.Headers.ContentType != null)
        {
            Response.ContentType = streamResponse.Content.Headers.ContentType.ToString();
        }
        
        // Copy Accept-Ranges header to indicate we support range requests
        Response.Headers["Accept-Ranges"] = Constants.AcceptRangesBytes;
        
        // For 200 OK (when not implementing workaround), don't set Content-Length to avoid mismatch errors if connection resets
        // This allows graceful handling of connection resets without "Content-Length mismatch" errors
    }

    /// <summary>
    /// Aborts the current HTTP connection so Kestrel does not run Content-Length validation when the client disconnected mid-stream.
    /// </summary>
    private void AbortConnection()
    {
        try
        {
            HttpContext.Features.Get<IConnectionLifetimeFeature>()?.Abort();
        }
        catch
        {
            // Ignore: abort is best-effort to avoid Content-Length mismatch logging
        }
    }

    /// <summary>
    /// Streams content from the HTTP response to the client response body.
    /// When upstream drops (timeout, connection reset), reconnects transparently so playback continues.
    /// </summary>
    private async Task<(long bytesWritten, StreamStopReason reason)> StreamContentAsync(
        HttpResponseMessage streamResponse,
        string type,
        string id,
        long? rangeStart,
        CancellationToken cancellationToken,
        Func<long, Task<(Stream? stream, IDisposable? toDispose)>>? getStreamFromOffset = null)
    {
        const int bufferSize = Constants.StreamBufferSize;
        const int flushInterval = Constants.StreamFlushInterval;
        var buffer = new byte[bufferSize];
        long totalBytesWritten = 0;
        int reconnectCount = 0;
        IDisposable? reconnectResponseToDispose = null;
        try
        {
            bool upstreamSupportsRange = streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent;
            bool needToSkipBytes = rangeStart.HasValue && !upstreamSupportsRange;
            long bytesToSkip = needToSkipBytes && rangeStart.HasValue ? rangeStart.Value : 0;
            long bytesSkipped = 0;
            Stream? stream = await streamResponse.Content.ReadAsStreamAsync();

            while (true)
            {
                try
                {
                    if (needToSkipBytes && stream != null)
                    {
                        while (bytesSkipped < bytesToSkip && !cancellationToken.IsCancellationRequested)
                        {
                            var remaining = bytesToSkip - bytesSkipped;
                            var skipBufferSize = (int)Math.Min(bufferSize, remaining);
                            var skipped = await stream.ReadAsync(buffer.AsMemory(0, skipBufferSize), cancellationToken);
                            if (skipped == 0)
                            {
                                _logger.LogWarning("Jfresolve: Reached end of stream while skipping bytes (requested: {Requested}, skipped: {Skipped})", bytesToSkip, bytesSkipped);
                                break;
                            }
                            bytesSkipped += skipped;
                        }
                        _logger.LogDebug("Jfresolve: Skipped {Bytes} bytes for range request workaround", bytesSkipped);
                        needToSkipBytes = false;
                    }

                    int bufferCount = 0;
                    int bytesRead;
                    while (stream != null && !cancellationToken.IsCancellationRequested &&
                           (bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalBytesWritten += bytesRead;
                        bufferCount++;
                        if (bufferCount == 1 || bufferCount % flushInterval == 0)
                            await Response.Body.FlushAsync(cancellationToken);
                    }

                    if (!cancellationToken.IsCancellationRequested)
                        await Response.Body.FlushAsync(cancellationToken);
                    return (totalBytesWritten, StreamStopReason.Completed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Jfresolve: Client disconnected (playback stopped) for {Type}/{Id} after ~{Bytes} bytes", type, id, totalBytesWritten);
                    AbortConnection();
                    return (totalBytesWritten, StreamStopReason.ClientDisconnect);
                }
                catch (TaskCanceledException tce) when (tce.InnerException is TimeoutException || tce.InnerException == null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        AbortConnection();
                        return (totalBytesWritten, StreamStopReason.ClientDisconnect);
                    }
                    if (!Response.HasStarted)
                    {
                        _logger.LogError(tce, "Jfresolve: HttpClient timeout before streaming started for {Type}/{Id}", type, id);
                        throw;
                    }
                    _logger.LogWarning("Jfresolve: Upstream timeout during streaming for {Type}/{Id} after ~{Bytes} bytes, reconnecting", type, id, totalBytesWritten);
                    var (ok1, s1, d1, c1) = await TryReconnectAsync(stream, reconnectResponseToDispose, reconnectCount, rangeStart, totalBytesWritten, getStreamFromOffset);
                    if (!ok1) return (totalBytesWritten, StreamStopReason.UpstreamFailure);
                    stream = s1; reconnectResponseToDispose = d1; reconnectCount = c1;
                }
                catch (TimeoutException te)
                {
                    if (cancellationToken.IsCancellationRequested) { AbortConnection(); return (totalBytesWritten, StreamStopReason.ClientDisconnect); }
                    if (!Response.HasStarted) { _logger.LogError(te, "Jfresolve: Timeout before streaming started for {Type}/{Id}", type, id); throw; }
                    _logger.LogWarning("Jfresolve: Upstream timeout for {Type}/{Id} after ~{Bytes} bytes, reconnecting", type, id, totalBytesWritten);
                    var (ok2, s2, d2, c2) = await TryReconnectAsync(stream, reconnectResponseToDispose, reconnectCount, rangeStart, totalBytesWritten, getStreamFromOffset);
                    if (!ok2) return (totalBytesWritten, StreamStopReason.UpstreamFailure);
                    stream = s2; reconnectResponseToDispose = d2; reconnectCount = c2;
                }
                catch (IOException ioEx) when (ioEx.InnerException is System.Net.Sockets.SocketException socketEx &&
                    (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset || socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown))
                {
                    if (cancellationToken.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        _logger.LogInformation("Jfresolve: Client disconnected for {Type}/{Id} after ~{Bytes} bytes", type, id, totalBytesWritten);
                        AbortConnection();
                        return (totalBytesWritten, StreamStopReason.ClientDisconnect);
                    }
                    _logger.LogInformation("Jfresolve: Upstream connection reset for {Type}/{Id} after ~{Bytes} bytes, reconnecting", type, id, totalBytesWritten);
                    var (ok3, s3, d3, c3) = await TryReconnectAsync(stream, reconnectResponseToDispose, reconnectCount, rangeStart, totalBytesWritten, getStreamFromOffset);
                    if (!ok3) return (totalBytesWritten, StreamStopReason.UpstreamFailure);
                    stream = s3; reconnectResponseToDispose = d3; reconnectCount = c3;
                }
                catch (System.Net.Sockets.SocketException socketEx) when
                    (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset || socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown)
                {
                    if (cancellationToken.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        AbortConnection();
                        return (totalBytesWritten, StreamStopReason.ClientDisconnect);
                    }
                    _logger.LogInformation("Jfresolve: Upstream connection reset for {Type}/{Id} after ~{Bytes} bytes, reconnecting", type, id, totalBytesWritten);
                    var (ok4, s4, d4, c4) = await TryReconnectAsync(stream, reconnectResponseToDispose, reconnectCount, rangeStart, totalBytesWritten, getStreamFromOffset);
                    if (!ok4) return (totalBytesWritten, StreamStopReason.UpstreamFailure);
                    stream = s4; reconnectResponseToDispose = d4; reconnectCount = c4;
                }
            }
        }
        finally
        {
            reconnectResponseToDispose?.Dispose();
        }
    }

    /// <summary>Reconnect to upstream from the given offset. Returns (success, newStream, toDispose, newReconnectCount).</summary>
    private async Task<(bool success, Stream? newStream, IDisposable? toDispose, int newReconnectCount)> TryReconnectAsync(
        Stream? stream,
        IDisposable? reconnectResponseToDispose,
        int reconnectCount,
        long? rangeStart,
        long totalBytesWritten,
        Func<long, Task<(Stream? stream, IDisposable? toDispose)>>? getStreamFromOffset)
    {
        if (getStreamFromOffset == null || reconnectCount >= Constants.MaxStreamReconnectAttempts)
            return (false, null, null, reconnectCount);
        stream?.Dispose();
        reconnectResponseToDispose?.Dispose();
        var offset = (rangeStart ?? 0) + totalBytesWritten;
        var (newStream, toDispose) = await getStreamFromOffset(offset);
        if (newStream == null)
            return (false, null, null, reconnectCount);
        reconnectCount++;
        _logger.LogInformation("Jfresolve: Reconnected at byte {Offset} (reconnect {N}/{Max})", offset, reconnectCount, Constants.MaxStreamReconnectAttempts);
        return (true, newStream, toDispose, reconnectCount);
    }

    /// <summary>
    /// Serves the plugin image (jfresolve.png)
    /// Jellyfin requests this from /Plugins/{guid}/{version}/Image
    /// </summary>
    [HttpGet("Image")]
    [HttpGet("{version}/Image")] // Handle versioned requests: /Plugins/{guid}/{version}/Image
    [AllowAnonymous]
    public IActionResult GetPluginImage(string? version = null)
    {
        try
        {
            _logger.LogDebug("Jfresolve: Plugin image requested (version: {Version})", version ?? "none");
            
            var assembly = Assembly.GetExecutingAssembly();
            
            // Try different possible resource names
            var possibleNames = new[]
            {
                "Jfresolve.jfresolve.png",
                "jfresolve.jfresolve.png",
                "Jfresolve.jfresolve-10.11.jfresolve.png"
            };
            
            Stream? imageStream = null;
            string? foundResourceName = null;
            
            foreach (var resourceName in possibleNames)
            {
                imageStream = assembly.GetManifestResourceStream(resourceName);
                if (imageStream != null)
                {
                    foundResourceName = resourceName;
                    _logger.LogDebug("Jfresolve: Found plugin image resource: {ResourceName}", resourceName);
                    break;
                }
            }
            
            // If not found, list all resources for debugging
            if (imageStream == null)
            {
                var allResources = assembly.GetManifestResourceNames();
                _logger.LogWarning("Jfresolve: Plugin image resource not found. Available resources: {Resources}", 
                    string.Join(", ", allResources));
                return NotFound("Plugin image not found");
            }

            return File(imageStream, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jfresolve: Error serving plugin image");
            return StatusCode(500, "Error serving plugin image");
        }
    }

    /// <summary>
    /// Test endpoint to verify API controller is working
    /// </summary>
    [HttpGet("test")]
    [AllowAnonymous]
    public IActionResult Test()
    {
        return Ok(new
        {
            plugin = "Jfresolve",
            version = JfresolvePlugin.Instance?.Version?.ToString() ?? "Unknown",
            message = "API controller is working!",
            manifestConfigured = !string.IsNullOrWhiteSpace(JfresolvePlugin.Instance?.Configuration?.AddonManifestUrl)
        });
    }


    /// <summary>
    /// Builds a cache key for failover tracking
    /// Format: type:id[:season:episode]:quality
    /// </summary>
    private string BuildFailoverCacheKey(string type, string id, string? season, string? episode, string? quality)
    {
        var key = $"{type}:{id}";

        if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(episode))
        {
            key += $":{season}:{episode}";
        }

        key += $":{quality ?? "default"}";

        return key;
    }

    /// <summary>
    /// Builds a cache key for redirect URL caching (includes index for different stream versions)
    /// </summary>
    private string BuildRedirectUrlCacheKey(string type, string id, string? season, string? episode, string? quality, int? index)
    {
        var key = $"{type}:{id}";

        if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(episode))
        {
            key += $":{season}:{episode}";
        }

        key += $":{quality ?? "default"}";
        
        if (index.HasValue)
        {
            key += $":index{index.Value}";
        }

        return key;
    }

    /// <summary>
    /// Determines the effective stream index using time-window failover logic
    /// - Grace period (0-45s): Keep serving same link to allow buffering
    /// - Failover window (45s-2min): Try next link on new request
    /// - Reset (>2min): Assume success, reset to original index
    /// </summary>
    private int DetermineFailoverIndex(
        string cacheKey,
        int? requestedIndex,
        string? quality,
        JsonElement streams,
        string preferredQuality,
        string type)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            return requestedIndex ?? 0;
        }

        // Check if failover is enabled for this content type
        bool failoverEnabled = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? config.EnableMovieFailover
            : config.EnableShowFailover;

        if (!failoverEnabled)
        {
            _logger.LogDebug("Jfresolve FAILOVER: Disabled for {Type}, using requested index {Index}", type, requestedIndex ?? 0);
            return requestedIndex ?? 0;
        }

        int effectiveIndex = requestedIndex ?? 0;

        // Get total available streams for this quality
        var streamArray = streams.EnumerateArray().ToList();
        var totalStreams = streamArray.Count;

        // If quality is specified, count only matching streams
        if (!string.IsNullOrEmpty(quality))
        {
            var filteredStreams = _qualitySelector.FilterStreamsByQuality(streamArray, quality);
            totalStreams = filteredStreams.Count;

            if (totalStreams == 0)
            {
                _logger.LogWarning(
                    "Jfresolve FAILOVER: No streams found for quality {Quality}, falling back to discovery",
                    quality
                );
                totalStreams = streamArray.Count;
            }
        }

        // If only one stream available, no need for failover
        if (totalStreams <= 1)
        {
            _logger.LogDebug("Jfresolve FAILOVER: Only {Count} stream(s) available, no failover needed", totalStreams);
            return effectiveIndex;
        }

        var now = DateTime.UtcNow;
        var gracePeriod = TimeSpan.FromSeconds(config.FailoverGracePeriodSeconds);
        var resetWindow = TimeSpan.FromSeconds(config.FailoverWindowSeconds);

        // Check failover state
        if (_failoverCache.TryGetValue(cacheKey, out var state))
        {
            var timeSinceFirstAttempt = now - state.FirstAttempt;
            var timeSinceLastAttempt = now - state.LastAttempt;

            // Reset window: assume success, clear state
            if (timeSinceLastAttempt > resetWindow)
            {
                _logger.LogInformation(
                    "Jfresolve FAILOVER: Reset for {Key} - {Time:F1}s since last attempt (success assumed)",
                    cacheKey, timeSinceLastAttempt.TotalSeconds
                );
                _failoverCache.TryRemove(cacheKey, out _);
                effectiveIndex = requestedIndex ?? 0;

                // Create new state
                _failoverCache[cacheKey] = new FailoverState
                {
                    CurrentIndex = effectiveIndex,
                    FirstAttempt = now,
                    LastAttempt = now,
                    AttemptCount = 1
                };

                return effectiveIndex;
            }

            // Grace period: keep serving same link to allow buffering
            if (timeSinceFirstAttempt < gracePeriod)
            {
                _logger.LogDebug(
                    "Jfresolve FAILOVER: Grace period for {Key} - {Time:F1}s/{Grace}s elapsed, serving index {Index} (attempt #{Attempt})",
                    cacheKey, timeSinceFirstAttempt.TotalSeconds, gracePeriod.TotalSeconds, state.CurrentIndex, state.AttemptCount + 1
                );

                // Update last attempt time and count
                state.LastAttempt = now;
                state.AttemptCount++;

                return state.CurrentIndex;
            }

            // Failover window: try next link
            effectiveIndex = state.CurrentIndex + 1;

            // Wrap around if exhausted
            if (effectiveIndex >= totalStreams)
            {
                effectiveIndex = 0;
                _logger.LogWarning(
                    "Jfresolve FAILOVER: Exhausted all {Count} streams for {Key}, wrapping to index 0 (attempt #{Attempt})",
                    totalStreams, cacheKey, state.AttemptCount + 1
                );
            }
            else
            {
                _logger.LogWarning(
                    "Jfresolve FAILOVER: Grace period expired for {Key}. " +
                    "Switching from index {OldIndex} to {NewIndex}/{Total} (attempt #{Attempt})",
                    cacheKey, state.CurrentIndex, effectiveIndex, totalStreams, state.AttemptCount + 1
                );
            }

            // Update state - new first attempt for this index
            state.CurrentIndex = effectiveIndex;
            state.FirstAttempt = now;  // Reset first attempt for new link
            state.LastAttempt = now;
            state.AttemptCount++;

            return effectiveIndex;
        }
        else
        {
            // First attempt for this content/quality
            _logger.LogInformation(
                "Jfresolve FAILOVER: First attempt for {Key}, serving index {Index}",
                cacheKey, effectiveIndex
            );

            _failoverCache[cacheKey] = new FailoverState
            {
                CurrentIndex = effectiveIndex,
                FirstAttempt = now,
                LastAttempt = now,
                AttemptCount = 1
            };

            return effectiveIndex;
        }
    }

    /// <summary>
    /// Handles stream errors by distinguishing between different failure types and returning appropriate HTTP status codes
    /// </summary>
    private IActionResult HandleStreamError(HttpResponseMessage response, string redirectUrl, string type, string id)
    {
        var statusCode = (int)response.StatusCode;
        string errorMessage;
        int httpStatusCode;
        
        // Distinguish between different error types
        if (statusCode == 401 || statusCode == 403)
        {
            // Authentication/Authorization errors
            errorMessage = "Authentication failed: Stream server requires authentication or access is denied";
            httpStatusCode = 502; // Bad Gateway - upstream authentication issue
            _logger.LogWarning("Jfresolve: Authentication error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}", 
                statusCode, type, id, redirectUrl);
        }
        else if (statusCode == 404)
        {
            // Not found errors
            errorMessage = "Stream not found: The requested stream is no longer available";
            httpStatusCode = 404; // Not Found - pass through to client
            _logger.LogWarning("Jfresolve: Stream not found (404) for {Type}/{Id} from {RedirectUrl}", 
                type, id, redirectUrl);
        }
        else if (statusCode >= 500 && statusCode < 600)
        {
            // Server errors
            errorMessage = "Stream server error: The stream server is experiencing issues";
            httpStatusCode = 502; // Bad Gateway - upstream server error
            _logger.LogError("Jfresolve: Stream server error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}", 
                statusCode, type, id, redirectUrl);
        }
        else if (statusCode == 429)
        {
            // Rate limiting
            errorMessage = "Rate limit exceeded: Too many requests to stream server";
            httpStatusCode = 503; // Service Unavailable
            _logger.LogWarning("Jfresolve: Rate limit (429) for {Type}/{Id} from {RedirectUrl}", 
                type, id, redirectUrl);
        }
        else if (statusCode >= 400 && statusCode < 500)
        {
            // Other client errors
            errorMessage = $"Stream request error: The stream server rejected the request (HTTP {statusCode})";
            httpStatusCode = 502; // Bad Gateway
            _logger.LogWarning("Jfresolve: Client error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}", 
                statusCode, type, id, redirectUrl);
        }
        else
        {
            // Unknown errors
            errorMessage = $"Unexpected stream error: HTTP {statusCode}";
            httpStatusCode = 502; // Bad Gateway
            _logger.LogError("Jfresolve: Unexpected error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}", 
                statusCode, type, id, redirectUrl);
        }
        
        response.Dispose();
        return StatusCode(httpStatusCode, errorMessage);
    }

    /// <summary>
    /// Executes a stream HTTP request with retry logic for transient failures
    /// Only retries initial connection failures, not during streaming
    /// </summary>
    private async Task<HttpResponseMessage?> ExecuteStreamRequestWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        string operationName,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt < Constants.MaxStreamRetryAttempts; attempt++)
        {
            HttpRequestMessage? request = null;
            try
            {
                request = requestFactory();
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                // Return 2xx (success) and 3xx (redirect) responses immediately - these are valid
                if (response.IsSuccessStatusCode || ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400))
                {
                    // Request will be disposed when response is disposed
                    return response;
                }
                
                // Don't retry on 4xx errors (client errors) - these are permanent failures
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    _logger.LogWarning("Jfresolve: Stream {Operation} failed with client error {StatusCode}, not retrying", 
                        operationName, response.StatusCode);
                    // Request will be disposed when response is disposed
                    return response; // Return the error response
                }
                
                // Retry on 5xx errors (server errors)
                if ((int)response.StatusCode >= 500)
                {
                    if (attempt < Constants.MaxStreamRetryAttempts - 1)
                    {
                        // Retry on server errors
                        var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                        _logger.LogWarning(
                            "Jfresolve: Stream {Operation} failed with {StatusCode}, retrying in {Delay}ms (attempt {Attempt}/{Max})",
                            operationName, response.StatusCode, delay, attempt + 1, Constants.MaxStreamRetryAttempts);
                        response.Dispose();
                        request.Dispose();
                        request = null;
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                    
                    // Final attempt failed, return the error response
                    return response;
                }
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt < Constants.MaxStreamRetryAttempts - 1)
                {
                    // Retry on network errors
                    request?.Dispose();
                    var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        "Jfresolve: Stream {Operation} network error, retrying in {Delay}ms (attempt {Attempt}/{Max}): {Error}",
                        operationName, delay, attempt + 1, Constants.MaxStreamRetryAttempts, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (TaskCanceledException ex)
            {
                lastException = ex;
                // Retry on timeout (but only if not cancelled by user)
                if (cancellationToken.IsCancellationRequested)
                {
                    request?.Dispose();
                    throw; // User cancelled, don't retry
                }
                
                if (attempt < Constants.MaxStreamRetryAttempts - 1)
                {
                    request?.Dispose();
                    var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        "Jfresolve: Stream {Operation} timeout, retrying in {Delay}ms (attempt {Attempt}/{Max})",
                        operationName, delay, attempt + 1, Constants.MaxStreamRetryAttempts);
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
                if (attempt < Constants.MaxStreamRetryAttempts - 1)
                {
                    request?.Dispose();
                    var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        "Jfresolve: Stream {Operation} timeout exception, retrying in {Delay}ms (attempt {Attempt}/{Max}): {Error}",
                        operationName, delay, attempt + 1, Constants.MaxStreamRetryAttempts, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (IOException ex)
            {
                lastException = ex;
                if (attempt < Constants.MaxStreamRetryAttempts - 1)
                {
                    request?.Dispose();
                    var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        "Jfresolve: Stream {Operation} IO error, retrying in {Delay}ms (attempt {Attempt}/{Max}): {Error}",
                        operationName, delay, attempt + 1, Constants.MaxStreamRetryAttempts, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < Constants.MaxStreamRetryAttempts - 1)
                {
                    request?.Dispose();
                    var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        "Jfresolve: Stream {Operation} unexpected error, retrying in {Delay}ms (attempt {Attempt}/{Max}): {Error}",
                        operationName, delay, attempt + 1, Constants.MaxStreamRetryAttempts, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        if (lastException != null)
        {
            _logger.LogError(lastException, "Jfresolve: Stream {Operation} failed after {MaxAttempts} attempts. Last error: {Error}", 
                operationName, Constants.MaxStreamRetryAttempts, lastException.Message);
        }
        else
        {
            _logger.LogError("Jfresolve: Stream {Operation} failed after {MaxAttempts} attempts (no exception details available)", 
                operationName, Constants.MaxStreamRetryAttempts);
        }
        return null;
    }

    /// <summary>
    /// Cleans up old entries from the failover cache to prevent memory leaks
    /// Runs periodically (every hour) to remove entries older than 24 hours
    /// </summary>
    private static void CleanupFailoverCacheIfNeeded()
    {
        var now = DateTime.UtcNow;
        
        // Only run cleanup once per hour to avoid performance impact
        if (now - _lastCacheCleanup < Constants.FailoverCacheCleanupInterval)
        {
            return;
        }

        _lastCacheCleanup = now;
        var cutoffTime = now - Constants.FailoverCacheEntryMaxAge;
        var keysToRemove = new List<string>();

        foreach (var kvp in _failoverCache)
        {
            if (kvp.Value.LastAttempt < cutoffTime)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _failoverCache.TryRemove(key, out _);
        }

        if (keysToRemove.Count > 0)
        {
            // Use a simple logger if available, otherwise skip logging to avoid dependency issues
            // Logging would require injecting ILogger, which would break the static method pattern
            // This cleanup is silent to maintain performance
        }
    }

    /// <summary>
    /// Sanitizes user input by removing potentially dangerous characters
    /// </summary>
    /// <summary>
    /// Checks if the request is authorized to access the stream endpoint
    /// Allows requests from localhost, server's own IP (including Docker), or authenticated Jellyfin users
    /// </summary>
    private bool IsRequestAuthorized()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        var config = JfresolvePlugin.Instance?.Configuration;
        var requestHost = Request.Host.Host;
        
        // Check if request is from localhost (FFmpeg runs on same server/container)
        // This works for both bare-metal and Docker
        if (remoteIp != null)
        {
            // Allow localhost (works for both bare-metal and Docker)
            if (System.Net.IPAddress.IsLoopback(remoteIp) || 
                remoteIp.ToString() == "127.0.0.1" || 
                remoteIp.ToString() == "::1")
            {
                return true; // Localhost is trusted (FFmpeg)
            }
        }

        // Check if request Host header matches the server's configured URL
        // This is the primary check for Docker scenarios - if the request is to the server's own hostname,
        // it's likely an internal request (FFmpeg/ffprobe) even if the IP is from Docker network
        if (config != null && !string.IsNullOrWhiteSpace(config.JellyfinServerUrl))
        {
            try
            {
                var serverUri = new Uri(config.JellyfinServerUrl);
                var serverHost = serverUri.Host;
                
                // Allow if Host header matches server URL hostname (works for Docker and bare-metal)
                // This catches FFmpeg/ffprobe requests that use the server's hostname
                if (requestHost.Equals(serverHost, StringComparison.OrdinalIgnoreCase))
                {
                    // Additional security: only allow if it's from a private IP or localhost
                    // This prevents external requests from spoofing the Host header
                    if (remoteIp == null || 
                        System.Net.IPAddress.IsLoopback(remoteIp) ||
                        IsPrivateIPAddressForAuth(remoteIp))
                    {
                        return true; // Request to server's own hostname from internal IP
                    }
                }
            }
            catch
            {
                // If URL parsing fails, fall through to other checks
            }
        }

        // Allow requests from private IP ranges when Host matches (Docker scenario)
        // FFmpeg/ffprobe requests in Docker come from Docker network IPs (172.17.x.x, 192.168.x.x, etc.)
        if (remoteIp != null && IsPrivateIPAddressForAuth(remoteIp))
        {
            // If Host header matches server URL, trust it (Docker internal network)
            if (config != null && !string.IsNullOrWhiteSpace(config.JellyfinServerUrl))
            {
                try
                {
                    var serverUri = new Uri(config.JellyfinServerUrl);
                    var serverHost = serverUri.Host;
                    
                    if (requestHost.Equals(serverHost, StringComparison.OrdinalIgnoreCase))
                    {
                        return true; // Request from Docker network to server's hostname
                    }
                }
                catch
                {
                    // If URL parsing fails, fall through
                }
            }
        }

        // Check if user is authenticated (has valid Jellyfin session)
        // This allows authenticated Jellyfin clients to access streams
        if (HttpContext.User?.Identity?.IsAuthenticated == true)
        {
            return true; // Authenticated Jellyfin user
        }

        // Check for Referer header from Jellyfin (additional security layer)
        // This helps verify the request is coming from a Jellyfin client
        var referer = Request.Headers["Referer"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(referer))
        {
            var serverUrl = config?.JellyfinServerUrl ?? "http://localhost:8096";
            var normalizedServerUrl = serverUrl.TrimEnd('/');
            if (referer.StartsWith(normalizedServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                return true; // Request from Jellyfin server
            }
        }

        // Check for User-Agent header that indicates Jellyfin client
        var userAgent = Request.Headers["User-Agent"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(userAgent) && 
            (userAgent.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase) ||
             userAgent.Contains("Emby", StringComparison.OrdinalIgnoreCase)))
        {
            return true; // Request from Jellyfin/Emby client
        }

        return false; // Not authorized
    }

    /// <summary>
    /// Sanitizes user input to prevent injection attacks in URL construction
    /// Removes control characters, dangerous URL characters, and limits length
    /// </summary>
    private static string SanitizeInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Remove control characters
        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray()).Trim();
        
        // Remove dangerous characters that could be used for injection
        // Keep only alphanumeric, hyphens, underscores, colons, and dots (for IDs like tt1234567 or S01E01)
        var allowedChars = new HashSet<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_:.");
        sanitized = new string(sanitized.Where(c => allowedChars.Contains(c)).ToArray());
        
        // Limit length to prevent buffer overflow attacks
        const int maxLength = 100;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength);
        }
        
        return sanitized;
    }

    /// <summary>
    /// Validates IMDB ID format (should be like tt1234567)
    /// </summary>
    private static bool IsValidImdbId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        
        // IMDB IDs start with 'tt' followed by 7-8 digits
        return System.Text.RegularExpressions.Regex.IsMatch(id, @"^tt\d{7,8}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Validates season/episode format (should be numeric)
    /// </summary>
    private static bool IsValidSeasonOrEpisode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        
        // Should be a positive integer
        return int.TryParse(value, out int num) && num > 0 && num <= 999;
    }

    /// <summary>
    /// Validates that a URL is safe for streaming (prevents SSRF attacks)
    /// Blocks localhost, private IPs, and other dangerous URLs
    /// </summary>
    private static bool IsValidStreamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Must be a well-formed absolute URI
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Only allow HTTP and HTTPS protocols (block file://, ftp://, etc.)
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        // Block URLs with userinfo (username:password@host) to prevent credential injection
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            return false;

        var host = uri.Host.ToLowerInvariant();
        
        // Block localhost variations
        if (host == "localhost" || 
            host == "127.0.0.1" || 
            host == "::1" ||
            host == "0.0.0.0" ||
            host == "[::1]")
        {
            return false;
        }

        // Block private IP ranges (RFC 1918)
        // 10.0.0.0/8
        if (host.StartsWith("10."))
            return false;
        
        // 192.168.0.0/16
        if (host.StartsWith("192.168."))
            return false;
        
        // 172.16.0.0/12 (172.16.0.0 to 172.31.255.255)
        if (host.StartsWith("172.") && IsPrivateIPRange(host))
            return false;

        // Block link-local addresses (169.254.0.0/16)
        if (host.StartsWith("169.254."))
            return false;

        // Block multicast addresses (224.0.0.0/4)
        if (host.StartsWith("224.") || host.StartsWith("225.") || 
            host.StartsWith("226.") || host.StartsWith("227.") ||
            host.StartsWith("228.") || host.StartsWith("229.") ||
            host.StartsWith("230.") || host.StartsWith("231.") ||
            host.StartsWith("232.") || host.StartsWith("233.") ||
            host.StartsWith("234.") || host.StartsWith("235.") ||
            host.StartsWith("236.") || host.StartsWith("237.") ||
            host.StartsWith("238.") || host.StartsWith("239."))
        {
            return false;
        }

        // Block reserved/test addresses
        if (host == "0.0.0.0" || host.StartsWith("0."))
            return false;

        // Try to resolve hostname to IP and check if it's a private IP
        // This catches cases where hostname resolves to private IP
        try
        {
            var hostEntry = System.Net.Dns.GetHostEntry(host);
            foreach (var ip in hostEntry.AddressList)
            {
                if (IsPrivateIPAddress(ip))
                {
                    return false;
                }
            }
        }
        catch
        {
            // If DNS resolution fails, we'll allow it (might be a valid external host)
            // But we've already checked the hostname itself above
        }

        return true;
    }

    /// <summary>
    /// Checks if an IP address is in a private range (RFC 1918)
    /// Used for authorization to allow server's own IP
    /// </summary>
    private static bool IsPrivateIPAddressForAuth(System.Net.IPAddress ip)
    {
        if (ip == null)
            return false;
            
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local addresses (fe80::/10) are considered private for authorization
            var bytes = ip.GetAddressBytes();
            if (bytes.Length >= 2 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if an IP address is private (RFC 1918)
    /// Used for SSRF protection to block private IPs in URLs
    /// </summary>
    private static bool IsPrivateIPAddress(System.Net.IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // Block IPv6 localhost (::1)
            if (ip.ToString() == "::1" || ip.ToString().StartsWith("[::1]"))
                return true;
            
            // Block IPv6 link-local addresses (fe80::/10)
            var bytes = ip.GetAddressBytes();
            if (bytes.Length >= 2 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if an IP address is in the private 172.16-31 range
    /// </summary>
    private static bool IsPrivateIPRange(string host)
    {
        if (!host.StartsWith("172."))
            return false;

        var parts = host.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var secondOctet))
        {
            return secondOctet >= 16 && secondOctet <= 31;
        }

        return false;
    }

    /// <summary>
    /// Follows HTTP redirects (302, 301, etc.) up to a maximum number of redirects
    /// </summary>
    private async Task<HttpResponseMessage?> FollowRedirectsAsync(
        HttpClient httpClient,
        HttpResponseMessage response,
        string originalUrl,
        int maxRedirects,
        CancellationToken cancellationToken = default)
    {
        var currentResponse = response;
        var redirectCount = 0;

        while (redirectCount < maxRedirects && 
               (currentResponse.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                currentResponse.StatusCode == System.Net.HttpStatusCode.Found ||
                currentResponse.StatusCode == System.Net.HttpStatusCode.SeeOther ||
                currentResponse.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect ||
                currentResponse.StatusCode == System.Net.HttpStatusCode.PermanentRedirect))
        {
            // Get the redirect location
            var location = currentResponse.Headers.Location?.ToString() ?? 
                          currentResponse.Headers.GetValues("Location").FirstOrDefault();

            if (string.IsNullOrWhiteSpace(location))
            {
                _logger.LogWarning("Jfresolve: Redirect response has no Location header");
                if (currentResponse != response)
                {
                    currentResponse.Dispose();
                }
                return null;
            }

            // Handle relative URLs
            if (!Uri.TryCreate(location, UriKind.Absolute, out var redirectUri))
            {
                if (Uri.TryCreate(new Uri(originalUrl), location, out redirectUri))
                {
                    location = redirectUri.ToString();
                }
                else
                {
                    _logger.LogWarning("Jfresolve: Invalid redirect location: {Location}", location);
                    if (currentResponse != response)
                    {
                        currentResponse.Dispose();
                    }
                    return null;
                }
            }

            // Validate redirect URL to prevent SSRF
            if (!IsValidStreamUrl(location))
            {
                _logger.LogWarning("Jfresolve: Invalid or unsafe redirect URL: {Location}", location);
                if (currentResponse != response)
                {
                    currentResponse.Dispose();
                }
                return null;
            }

            redirectCount++;
            _logger.LogDebug("Jfresolve: Following redirect #{Count} to {Location}", redirectCount, location);

            // Dispose previous response if it's not the original
            if (currentResponse != response)
            {
                currentResponse.Dispose();
            }

            // Create new request for redirect
            var redirectRequest = new HttpRequestMessage(HttpMethod.Get, location);
            
            // Preserve Range header from original request if present
            // Note: We need to get this from the original request context
            // For now, we'll preserve it from the current HTTP context
            if (Request.Headers.ContainsKey("Range"))
            {
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    redirectRequest.Headers.Add("Range", rangeHeader);
                }
            }

            // Follow the redirect (with cancellation token to stop immediately if client disconnects)
            currentResponse = await httpClient.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        // If we followed redirects, dispose the original response
        // If no redirects were followed, currentResponse == response and caller will dispose it
        if (currentResponse != response && redirectCount > 0)
        {
            response.Dispose();
        }

        return currentResponse;
    }

    /// <summary>
    /// Parses the Range header to extract the start byte position
    /// Supports formats like "bytes=123-", "bytes=123-456", "bytes=-456"
    /// </summary>
    private static long? ParseRangeStart(string rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return null;

        // Range header format: "bytes=start-end" or "bytes=start-" or "bytes=-end"
        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;

        var rangeValue = rangeHeader.Substring(6).Trim(); // Remove "bytes="
        var parts = rangeValue.Split('-');
        
        if (parts.Length != 2)
            return null;

        // If start is specified, parse it
        if (!string.IsNullOrWhiteSpace(parts[0]) && long.TryParse(parts[0], out var start))
        {
            return start;
        }

        return null;
    }
}
