using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
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
    /// Gets streams from the Stremio addon
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
            streamHttpClient.Timeout = TimeSpan.FromMinutes(Constants.StreamRequestTimeoutMinutes);
            
            // Handle HTTP Range requests for seeking (required by FFmpeg)
            var rangeHeader = Request.Headers["Range"].ToString();
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
            
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                _logger.LogDebug("Jfresolve: Range request detected: {Range}", rangeHeader);
                requestMessage.Headers.Add("Range", rangeHeader);
            }
            
            // Stream the content directly to the response
            // HttpResponseMessage must be disposed to prevent resource leaks
            var initialResponse = await streamHttpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
            
            // Handle redirects (302, 301, etc.) - follow up to 5 redirects
            // FollowRedirectsAsync will dispose initialResponse if redirects are followed
            var streamResponse = await FollowRedirectsAsync(streamHttpClient, initialResponse, redirectUrl, 5);
            if (streamResponse == null)
            {
                initialResponse.Dispose(); // Dispose if redirects failed
                _logger.LogError("Jfresolve: Failed to follow redirects for {RedirectUrl}", redirectUrl);
                return StatusCode(502, "Failed to resolve stream URL after redirects");
            }
            
            // Use the final response (after following redirects)
            using var finalStreamResponse = streamResponse;
            finalStreamResponse.EnsureSuccessStatusCode();

            // Copy response headers
            CopyStreamResponseHeaders(finalStreamResponse);

            // Stream the content
            await StreamContentAsync(finalStreamResponse, type, id);
            
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
    private void CopyStreamResponseHeaders(HttpResponseMessage streamResponse)
    {
        // Copy status code (206 for partial content if range was requested)
        Response.StatusCode = (int)streamResponse.StatusCode;

        // Copy headers that might be important for streaming
        if (streamResponse.Content.Headers.ContentType != null)
        {
            Response.ContentType = streamResponse.Content.Headers.ContentType.ToString();
        }
        
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
        
        // Copy Accept-Ranges header to indicate we support range requests
        Response.Headers["Accept-Ranges"] = Constants.AcceptRangesBytes;
        
        // Only set Content-Length for range requests (206 Partial Content) where we know the exact range size
        // For full requests (200 OK), don't set Content-Length to avoid mismatch errors if connection resets
        // This allows graceful handling of connection resets without "Content-Length mismatch" errors
        if (streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent && 
            streamResponse.Content.Headers.ContentLength.HasValue)
        {
            // For 206 Partial Content, Content-Length represents the range size, which is safe to set
            Response.ContentLength = streamResponse.Content.Headers.ContentLength.Value;
        }
        // For 200 OK or other statuses, don't set Content-Length - let it stream without fixed length
        // This prevents "Content-Length mismatch" errors when connections are reset mid-stream
    }

    /// <summary>
    /// Streams content from the HTTP response to the client response body
    /// </summary>
    private async Task StreamContentAsync(HttpResponseMessage streamResponse, string type, string id)
    {
        // Optimized streaming: Use larger buffer and flush less frequently for better throughput
        // Larger buffer reduces overhead from frequent system calls
        // Periodic flushing (every N buffers) reduces flush overhead while maintaining responsiveness
        const int bufferSize = Constants.StreamBufferSize;
        const int flushInterval = Constants.StreamFlushInterval;
        var buffer = new byte[bufferSize];
        int bufferCount = 0;
        
        // ReadAsStreamAsync returns a stream that will be disposed when HttpResponseMessage is disposed
        // The stream must be fully read before HttpResponseMessage disposal completes
        using (var stream = await streamResponse.Content.ReadAsStreamAsync())
        {
            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await Response.Body.WriteAsync(buffer, 0, bytesRead);
                    bufferCount++;
                    
                    // Flush periodically to reduce overhead while maintaining low latency
                    // Flush immediately on first buffer for quick start, then every N buffers
                    if (bufferCount == 1 || bufferCount % flushInterval == 0)
                    {
                        await Response.Body.FlushAsync();
                    }
                }
                
                // Final flush to ensure all data is sent
                await Response.Body.FlushAsync();
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
        int maxRedirects)
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

            // Follow the redirect
            currentResponse = await httpClient.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead);
        }

        // If we followed redirects, dispose the original response
        // If no redirects were followed, currentResponse == response and caller will dispose it
        if (currentResponse != response && redirectCount > 0)
        {
            response.Dispose();
        }

        return currentResponse;
    }
}
