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

    // Failover cache: tracks recent playback attempts with time windows
    private static readonly ConcurrentDictionary<string, FailoverState> _failoverCache = new();
    private static DateTime _lastCacheCleanup = DateTime.UtcNow;

    public JfresolveApiController(
        ILogger<JfresolveApiController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
            // Normalize the manifest URL (remove stremio://, convert to https://)
            var manifestBase = UrlBuilder.NormalizeManifestUrl(config.AddonManifestUrl);

            // Build the stream endpoint URL
            string streamUrl;
            if (type.Equals("movie", StringComparison.OrdinalIgnoreCase))
            {
                streamUrl = $"{manifestBase}/stream/movie/{id}.json";
            }
            else if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
                {
                    return BadRequest("Season and episode parameters are required for series type");
                }
                streamUrl = $"{manifestBase}/stream/series/{id}:{season}:{episode}.json";
            }
            else
            {
                streamUrl = $"{manifestBase}/stream/{type}/{id}.json";
            }

            _logger.LogInformation("Jfresolve: Requesting stream from addon: {StreamUrl}", streamUrl);

            // Call the Stremio addon to get the stream
            var addonHttpClient = _httpClientFactory.CreateClient("Jfresolve.Addon");
            addonHttpClient.Timeout = TimeSpan.FromSeconds(Constants.AddonRequestTimeoutSeconds);
            addonHttpClient.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent);
            var response = await addonHttpClient.GetStringAsync(streamUrl);

            // Parse the JSON response
            using var json = JsonDocument.Parse(response);
            if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                _logger.LogWarning("Jfresolve: No streams found for {Type}/{Id}", type, id);
                return NotFound($"No streams found for {id}");
            }

            // FAILOVER LOGIC: Determine effective index with time-window based retry for dead links
            var cacheKey = BuildFailoverCacheKey(type, id, season, episode, quality);
            int effectiveIndex = DetermineFailoverIndex(cacheKey, index, quality, streams, config.PreferredQuality, type);

            // Select the stream using failover-adjusted index
            var selectedStream = SelectStreamByQuality(streams, config.PreferredQuality, quality, effectiveIndex);
            if (selectedStream == null)
            {
                _logger.LogWarning("Jfresolve: Could not select a stream for {Type}/{Id}", type, id);
                return NotFound("No suitable stream found");
            }

            if (!selectedStream.Value.TryGetProperty("url", out var urlProperty))
            {
                _logger.LogWarning("Jfresolve: No URL property in stream response");
                return NotFound("No stream URL available in response");
            }

            var redirectUrl = urlProperty.GetString();
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                _logger.LogWarning("Jfresolve: Empty stream URL received");
                return NotFound("Empty stream URL received");
            }

            _logger.LogInformation("Jfresolve: Resolved {Type}/{Id} to {RedirectUrl}", type, id, redirectUrl);

            // Validate URL to prevent SSRF attacks
            if (!IsValidStreamUrl(redirectUrl))
            {
                _logger.LogWarning("Jfresolve: Invalid or unsafe redirect URL: {RedirectUrl}", redirectUrl);
                return BadRequest("Invalid or unsafe stream URL");
            }

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
                using var streamResponse = await streamHttpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
                streamResponse.EnsureSuccessStatusCode();

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
                
                if (streamResponse.Content.Headers.ContentLength.HasValue)
                {
                    Response.ContentLength = streamResponse.Content.Headers.ContentLength.Value;
                }

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
                        // Return empty result - connection will close naturally
                        return new EmptyResult();
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
                        return new EmptyResult();
                    }
                }
                // HttpResponseMessage will be disposed here automatically via using statement
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
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Jfresolve: HTTP error resolving stream for {Type}/{Id}", type, id);
            return StatusCode(500, $"Error contacting addon: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Jfresolve: JSON parse error for {Type}/{Id}", type, id);
            return StatusCode(500, $"Invalid response from addon: {ex.Message}");
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
    /// Selects the best stream from the available streams based on preferred quality
    /// </summary>
    private JsonElement? SelectStreamByQuality(JsonElement streams, string preferredQuality, string? requestedQuality = null, int? requestedIndex = null)
    {
        var streamArray = streams.EnumerateArray().ToList();
        if (streamArray.Count == 0)
            return null;

        // If a specific quality is requested (Virtual Versioning), filter and pick by index
        if (!string.IsNullOrEmpty(requestedQuality))
        {
            var filteredStreams = FilterStreamsByQuality(streamArray, requestedQuality);
            if (filteredStreams.Count > 0)
            {
                var idx = requestedIndex ?? 0;
                // Fallback to last available if index is too high
                if (idx >= filteredStreams.Count)
                {
                    _logger.LogWarning("Jfresolve: Requested index {Index} out of range for quality {Quality}. Falling back to index {FallbackIndex}.",
                        idx, requestedQuality, filteredStreams.Count - 1);
                    idx = filteredStreams.Count - 1;
                }
                _logger.LogInformation("Jfresolve: Selected quality {Quality} stream at index {Index}", requestedQuality, idx);
                return filteredStreams[idx];
            }

            _logger.LogWarning("Jfresolve: Specifically requested quality {Quality} not found, falling back to discovery logic", requestedQuality);
        }

        // Discovery logic (Discovery mode or fallback)
        if (preferredQuality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return SelectHighestQualityStream(streamArray);
        }

        // Try to find exact match for preferred quality
        var matchedStream = FindStreamByQuality(streamArray, preferredQuality);
        if (matchedStream != null)
        {
            _logger.LogInformation("Jfresolve: Selected {Quality} stream (discovery match)", preferredQuality);
            return matchedStream;
        }

        // Fallback: select highest quality if preferred not found
        _logger.LogInformation("Jfresolve: Preferred quality {Quality} not found, selecting highest available", preferredQuality);
        return SelectHighestQualityStream(streamArray);
    }

    /// <summary>
    /// Filters streams list to only those containing the specified quality indicators
    /// </summary>
    private System.Collections.Generic.List<JsonElement> FilterStreamsByQuality(System.Collections.Generic.List<JsonElement> streams, string quality)
    {
        var indicators = GetQualityIndicators(quality);
        var results = new System.Collections.Generic.List<JsonElement>();

        foreach (var stream in streams)
        {
            var text = GetStreamText(stream);
            if (indicators.Any(ind => text.Contains(ind, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(stream);
            }
        }

        return results;
    }

    /// <summary>
    /// Finds a stream matching the specified quality preference
    /// </summary>
    private JsonElement? FindStreamByQuality(System.Collections.Generic.List<JsonElement> streams, string quality)
    {
        var qualityIndicators = GetQualityIndicators(quality);

        foreach (var stream in streams)
        {
            var streamText = GetStreamText(stream);

            // Check if any quality indicator is present in the stream text
            foreach (var indicator in qualityIndicators)
            {
                if (streamText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                {
                    return stream;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Selects the highest quality stream from the available streams
    /// Priority order: 4K/2160p > 1440p > 1080p > 720p > 480p > first available
    /// </summary>
    private JsonElement SelectHighestQualityStream(System.Collections.Generic.List<JsonElement> streams)
    {
        // Try to find streams in order of quality preference
        var qualityPriority = Constants.QualityPriority;

        foreach (var quality in qualityPriority)
        {
            var stream = FindStreamByQuality(streams, quality);
            if (stream != null)
            {
                _logger.LogInformation("Jfresolve: Auto-selected {Quality} stream (highest available)", quality);
                return stream.Value;
            }
        }

        // Fallback to first stream if no quality indicators found
        _logger.LogInformation("Jfresolve: No quality indicators found, using first stream");
        return streams[0];
    }

    /// <summary>
    /// Gets quality indicators for a given quality preference
    /// Maps user-friendly names to various formats used by different addons
    /// </summary>
    private string[] GetQualityIndicators(string quality)
    {
        return quality.ToLowerInvariant() switch
        {
            "4k" => new[] { "4k", "2160p", "2160" },
            "1440p" => new[] { "1440p", "1440" },
            "1080p" => new[] { "1080p", "1080" },
            "720p" => new[] { "720p", "720" },
            "480p" => new[] { "480p", "480" },
            _ => new[] { quality.ToLowerInvariant() }
        };
    }

    /// <summary>
    /// Extracts searchable text from a stream object (name + title fields)
    /// </summary>
    private string GetStreamText(JsonElement stream)
    {
        var text = string.Empty;

        if (stream.TryGetProperty("name", out var name))
        {
            text += name.GetString() + " ";
        }

        if (stream.TryGetProperty("title", out var title))
        {
            text += title.GetString();
        }

        return text;
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
            var filteredStreams = FilterStreamsByQuality(streamArray, quality);
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
}
