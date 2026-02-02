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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Api;

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

    // Failover cache: tracks recent playback attempts with time windows
    private static readonly ConcurrentDictionary<string, FailoverState> _failoverCache = new();
    private static DateTime _lastCacheCleanup = DateTime.UtcNow;

    // Stream metadata cache: caches Stremio addon responses (stream lists) with TTL
    // Key: stream URL, Value: (Serialized JSON, ExpiryTime)
    private static readonly ConcurrentDictionary<string, (string Json, DateTime Expiry)> _streamMetadataCache = new();
    private static DateTime _lastStreamCacheCleanup = DateTime.UtcNow;
    
    // Resolved URL cache: caches final resolved stream URLs (after following redirects) to speed up resume
    // Key: original redirect URL, Value: (Final URL after redirects, ExpiryTime)
    private static readonly ConcurrentDictionary<string, (string FinalUrl, DateTime Expiry)> _resolvedUrlCache = new();
    private static DateTime _lastResolvedUrlCacheCleanup = DateTime.UtcNow;

    public JfresolveApiController(
        ILogger<JfresolveApiController> logger,
        IHttpClientFactory httpClientFactory,
        StreamQualitySelector qualitySelector)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _qualitySelector = qualitySelector;
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
        // Input validation and sanitization
        if (string.IsNullOrWhiteSpace(type))
        {
            _logger.LogWarning("Jfresolve: Invalid request - type parameter is empty");
            return BadRequest("Type parameter is required");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Jfresolve: Invalid request - id parameter is empty");
            return BadRequest("Id parameter is required");
        }

        // Sanitize inputs - remove any potentially dangerous characters
        type = SanitizeInput(type);
        id = SanitizeInput(id);
        season = string.IsNullOrWhiteSpace(season) ? null : SanitizeInput(season);
        episode = string.IsNullOrWhiteSpace(episode) ? null : SanitizeInput(episode);
        quality = string.IsNullOrWhiteSpace(quality) ? null : SanitizeInput(quality);

        // Validate index is within reasonable bounds
        if (index.HasValue && (index.Value < 0 || index.Value > 100))
        {
            _logger.LogWarning("Jfresolve: Invalid request - index out of bounds: {Index}", index.Value);
            return BadRequest("Index must be between 0 and 100");
        }

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

        _logger.LogInformation(
            "Jfresolve: Resolving stream for {Type}/{Id} (Season: {Season}, Episode: {Episode})",
            type, id, season ?? "N/A", episode ?? "N/A"
        );

        // Check if addon manifest URL is configured
        if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
        {
            _logger.LogError("Jfresolve: Addon manifest URL not configured - cannot resolve stream");
            return NotFound("Addon manifest URL not configured. Please configure it in plugin settings.");
        }

        try
        {
            // Validate series parameters
            if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
                {
                    return BadRequest("Season and episode parameters are required for series type");
                }
            }

            // Get streams from addon (returns JsonDocument that must be kept alive)
            JsonDocument? streamsDoc = null;
            string? redirectUrl = null;
            try
            {
                streamsDoc = await GetStreamsFromAddonAsync(type, id, season, episode, config);
                if (streamsDoc == null || streamsDoc.RootElement.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Jfresolve: No streams found for {Type}/{Id}", type, id);
                    return NotFound($"No streams found for {id}");
                }

                // Select stream using quality selector and failover logic
                // Keep streamsDoc alive until we're done using the streams element
                redirectUrl = await SelectAndResolveStreamUrlAsync(type, id, season, episode, quality, index, streamsDoc.RootElement, config);
            }
            finally
            {
                // Dispose the JsonDocument after we're done with it
                streamsDoc?.Dispose();
            }
            
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
                _logger.LogError(ex, "Jfresolve: HTTP error resolving stream for {Type}/{Id}", type, id);
                return StatusCode(500, $"Error contacting addon: {ex.Message}");
            }
            else
            {
                _logger.LogWarning(ex, "Jfresolve: HTTP error during streaming for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
        }
        catch (JsonException ex)
        {
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: JSON parse error for {Type}/{Id}", type, id);
                return StatusCode(500, $"Invalid response from addon: {ex.Message}");
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
                return StatusCode(502, "Connection reset by peer");
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
                _logger.LogError(ex, "Jfresolve: Error resolving stream for {Type}/{Id}", type, id);
                return StatusCode(500, $"Error resolving stream: {ex.Message}");
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

        // Call the Stremio addon to get the stream
        var addonHttpClient = _httpClientFactory.CreateClient("Jfresolve.Addon");
        addonHttpClient.Timeout = TimeSpan.FromSeconds(Constants.AddonRequestTimeoutSeconds);
        addonHttpClient.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent);
        var response = await addonHttpClient.GetStringAsync(streamUrl);

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
    /// </summary>
    private string BuildStreamUrl(string manifestBase, string type, string id, string? season, string? episode)
    {
        if (type.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            return $"{manifestBase}/stream/movie/{id}.json";
        }
        else if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
            {
                return string.Empty; // Will be caught by caller
            }
            return $"{manifestBase}/stream/series/{id}:{season}:{episode}.json";
        }
        else
        {
            return $"{manifestBase}/stream/{type}/{id}.json";
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
            
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
            
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                requestMessage.Headers.Add("Range", rangeHeader);
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
                var cachedRequest = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    cachedRequest.Headers.Add("Range", rangeHeader);
                }
                streamResponse = await streamHttpClient.SendAsync(cachedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                // Follow redirects to get final URL (first time or cache expired)
                initialResponse = await streamHttpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
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
            finalStreamResponse.EnsureSuccessStatusCode();
            
            // Dispose initialResponse if it was created (not used when cache hit)
            initialResponse?.Dispose();

            // Copy response headers
            CopyStreamResponseHeaders(finalStreamResponse, rangeHeader, rangeStart);

            // Stream the content (with workaround for servers that don't support range requests)
            // Pass cancellation token so streaming stops immediately when client disconnects
            await StreamContentAsync(finalStreamResponse, type, id, rangeStart, cancellationToken);
            
            return new EmptyResult();
        }
        catch (HttpRequestException ex)
        {
            // Only return error if response hasn't started yet
            if (!Response.HasStarted)
            {
                _logger.LogError(ex, "Jfresolve: Error proxying stream from {RedirectUrl}", redirectUrl);
                return StatusCode(502, $"Error proxying stream: {ex.Message}");
            }
            else
            {
                // Response already started - log and let connection close
                _logger.LogWarning(ex, "Jfresolve: Error during streaming after response started for {RedirectUrl}", redirectUrl);
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
                return StatusCode(502, "Connection reset by peer");
            }
            else
            {
                _logger.LogInformation(ioEx, "Jfresolve: Connection reset during streaming for {RedirectUrl} (normal client disconnect)", redirectUrl);
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
                
                // Set Content-Length to the range size we'll actually send
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
            
            // Only set Content-Length for range requests (206 Partial Content) where we know the exact range size
            if (upstreamSupportsRange && 
                streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent && 
                streamResponse.Content.Headers.ContentLength.HasValue)
            {
                // For 206 Partial Content, Content-Length represents the range size, which is safe to set
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
    /// Streams content from the HTTP response to the client response body
    /// </summary>
    private async Task StreamContentAsync(HttpResponseMessage streamResponse, string type, string id, long? rangeStart, CancellationToken cancellationToken = default)
    {
        // Optimized streaming: Use larger buffer and flush less frequently for better throughput
        // Larger buffer reduces overhead from frequent system calls
        // Periodic flushing (every N buffers) reduces flush overhead while maintaining responsiveness
        const int bufferSize = Constants.StreamBufferSize;
        const int flushInterval = Constants.StreamFlushInterval;
        var buffer = new byte[bufferSize];
        int bufferCount = 0;
        
        // Workaround for servers that don't support range requests
        // If client requested a range but server returned 200 OK, skip bytes ourselves
        bool upstreamSupportsRange = streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent;
        bool needToSkipBytes = rangeStart.HasValue && !upstreamSupportsRange;
        long bytesToSkip = needToSkipBytes && rangeStart.HasValue ? rangeStart.Value : 0;
        long bytesSkipped = 0;
        
        // ReadAsStreamAsync returns a stream that will be disposed when HttpResponseMessage is disposed
        // The stream must be fully read before HttpResponseMessage disposal completes
        using (var stream = await streamResponse.Content.ReadAsStreamAsync())
        {
            try
            {
                // Skip bytes if upstream doesn't support range requests
                if (needToSkipBytes)
                {
                    while (bytesSkipped < bytesToSkip && !cancellationToken.IsCancellationRequested)
                    {
                        var remaining = bytesToSkip - bytesSkipped;
                        var skipBufferSize = (int)Math.Min(bufferSize, remaining);
                        var skipped = await stream.ReadAsync(buffer, 0, skipBufferSize, cancellationToken);
                        if (skipped == 0)
                        {
                            // Reached end of stream before skipping enough bytes
                            _logger.LogWarning("Jfresolve: Reached end of stream while skipping bytes (requested: {Requested}, skipped: {Skipped})", bytesToSkip, bytesSkipped);
                            break;
                        }
                        bytesSkipped += skipped;
                    }
                    _logger.LogDebug("Jfresolve: Skipped {Bytes} bytes for range request workaround", bytesSkipped);
                }
                
                int bytesRead;
                while (!cancellationToken.IsCancellationRequested && 
                       (bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await Response.Body.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    bufferCount++;
                    
                    // Flush periodically to reduce overhead while maintaining low latency
                    // Flush immediately on first buffer for quick start, then every N buffers
                    if (bufferCount == 1 || bufferCount % flushInterval == 0)
                    {
                        await Response.Body.FlushAsync(cancellationToken);
                    }
                }
                
                // Final flush to ensure all data is sent (if not cancelled)
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken.IsCancellationRequested)
            {
                // Client disconnected (RequestAborted) - this is normal when user stops playback
                // Log and exit gracefully - no need to throw or return error
                _logger.LogInformation(
                    "Jfresolve: Client disconnected (playback stopped) for {Type}/{Id} after ~{Bytes} bytes",
                    type, id, bufferCount * bufferSize);
                // Don't throw - just exit, connection will close naturally
            }
            catch (TaskCanceledException tce) when (tce.InnerException is TimeoutException || tce.InnerException == null)
            {
                // HttpClient timeout during streaming - HttpClient throws TaskCanceledException on timeout
                // This can happen if there's a long pause in data transfer (buffering, network hiccup)
                // Log the timeout but don't treat it as a fatal error if response has started
                if (Response.HasStarted)
                {
                    _logger.LogWarning(
                        tce,
                        "Jfresolve: HttpClient timeout during streaming for {Type}/{Id} after ~{Bytes} bytes (likely due to network pause or buffering). Connection may have been idle too long.",
                        type, id, bufferCount * bufferSize);
                }
                else
                {
                    _logger.LogError(
                        tce,
                        "Jfresolve: HttpClient timeout before streaming started for {Type}/{Id}",
                        type, id);
                    throw; // Re-throw if response hasn't started yet
                }
            }
            catch (TimeoutException te)
            {
                // Direct timeout exception (less common with HttpClient)
                if (Response.HasStarted)
                {
                    _logger.LogWarning(
                        te,
                        "Jfresolve: Timeout during streaming for {Type}/{Id} after ~{Bytes} bytes",
                        type, id, bufferCount * bufferSize);
                }
                else
                {
                    _logger.LogError(te, "Jfresolve: Timeout before streaming started for {Type}/{Id}", type, id);
                    throw;
                }
            }
            catch (IOException ioEx) when (ioEx.InnerException is System.Net.Sockets.SocketException socketEx && 
                                             (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                              socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown))
            {
                // Connection reset during streaming - this is normal when client pauses/disconnects
                // Don't try to send error response as response has already started
                // Just log and let the connection close naturally
                _logger.LogInformation(
                    "Jfresolve: Connection reset during streaming for {Type}/{Id} (client likely paused/disconnected). Bytes streamed: ~{Bytes}",
                    type, id, bufferCount * bufferSize);
                
                // Check if response has started before trying to flush
                if (!Response.HasStarted)
                {
                    await Response.Body.FlushAsync();
                }
            }
            catch (System.Net.Sockets.SocketException socketEx) when 
                (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                 socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown)
            {
                // Connection reset during streaming
                _logger.LogInformation(
                    "Jfresolve: Connection reset during streaming for {Type}/{Id} (client likely paused/disconnected). Bytes streamed: ~{Bytes}",
                    type, id, bufferCount * bufferSize);
                
                if (!Response.HasStarted)
                {
                    await Response.Body.FlushAsync();
                }
            }
        }
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
    private static string SanitizeInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Remove control characters and trim
        return new string(input.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    /// <summary>
    /// Validates that a URL is safe for streaming (prevents SSRF attacks)
    /// </summary>
    private static bool IsValidStreamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Must be a well-formed absolute URI
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Only allow HTTP and HTTPS protocols
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        // Block localhost and private IP ranges to prevent SSRF
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || 
            host == "127.0.0.1" || 
            host == "::1" ||
            host.StartsWith("192.168.") ||
            host.StartsWith("10.") ||
            (host.StartsWith("172.") && IsPrivateIPRange(host)) ||
            host == "0.0.0.0")
        {
            return false;
        }

        return true;
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
