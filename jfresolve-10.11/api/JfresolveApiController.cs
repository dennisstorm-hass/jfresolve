using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.WebUtilities;

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
    private readonly Services.TorBoxStreamService _torBoxStreamService;
    private readonly Services.PlaybackStreamResolver _playbackStreamResolver;

    // Resolved URL cache: caches final resolved stream URLs (after following redirects) to speed up resume
    // Key: original redirect URL, Value: (Final URL after redirects, ExpiryTime)
    private static readonly ConcurrentDictionary<string, (string FinalUrl, DateTime Expiry)> _resolvedUrlCache = new();
    private static DateTime _lastResolvedUrlCacheCleanup = DateTime.UtcNow;

    // Upstream total file size cache (needed for FFmpeg MKV byte seeks when headers omit Content-Length)
    private static readonly ConcurrentDictionary<string, (long ContentLength, DateTime Expiry)> _upstreamContentLengthCache = new();

    // Recent FFmpeg disconnects (seek restarts stop the prior download mid-stream).
    private static readonly ConcurrentDictionary<string, DateTime> _recentPlaybackDisconnects = new();
    // After HLS is used once, keep using it for subsequent seeks in the same title.
    private static readonly ConcurrentDictionary<string, DateTime> _activeHlsPlayback = new();
    private const long SeekDetectionMinBytes = 50_000_000;
    private static readonly TimeSpan SeekDetectionWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HlsPlaybackSessionWindow = TimeSpan.FromHours(2);

    public JfresolveApiController(
        IHttpClientFactory httpClientFactory,
        ILogger<JfresolveApiController> logger,
        Services.TorBoxStreamService torBoxStreamService,
        Services.PlaybackStreamResolver playbackStreamResolver)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _torBoxStreamService = torBoxStreamService;
        _playbackStreamResolver = playbackStreamResolver;
    }

    private Services.UserPreferencesService? GetUserPreferencesService()
    {
        return HttpContext?.RequestServices?.GetService<Services.UserPreferencesService>();
    }

    private Configuration.PluginConfiguration? GetPluginConfiguration()
    {
        var cfg = JfresolvePlugin.Instance?.Configuration;
        if (cfg != null)
            return cfg;

        // Fallback: load configuration from disk when the plugin instance hasn't been constructed yet.
        // This avoids relying on BasePlugin initialization timing during Jellyfin startup.
        var applicationPaths = HttpContext?.RequestServices?.GetService(typeof(MediaBrowser.Common.Configuration.IApplicationPaths)) as
            MediaBrowser.Common.Configuration.IApplicationPaths;

        if (applicationPaths == null)
            return null;

        var configDir = applicationPaths.PluginConfigurationsPath;
        if (string.IsNullOrWhiteSpace(configDir))
            return null;

        return TryLoadPluginConfigurationFromDisk(configDir);
    }

    private Configuration.PluginConfiguration? TryLoadPluginConfigurationFromDisk(string configDir)
    {
        // Jellyfin plugins typically store BasePlugin configuration as config.json inside their plugin config directory.
        // Try a couple of common filenames for resilience.
        var candidates = new[]
        {
            Path.Combine(configDir, "config.json"),
            Path.Combine(configDir, "Config.json"),
            Path.Combine(configDir, "plugin.json"),
            Path.Combine(configDir, "Plugin.json")
        };

        var filePath = candidates.FirstOrDefault(System.IO.File.Exists);
        if (filePath == null)
            return null;

        try
        {
            var json = System.IO.File.ReadAllText(filePath);
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            // Be tolerant if BasePlugin wraps the config in an outer object.
            using var doc = JsonDocument.Parse(json);
            JsonElement element = doc.RootElement;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var propName in new[] { "configuration", "config", "value", "Value", "Configuration" })
                {
                    if (element.TryGetProperty(propName, out var nested))
                    {
                        element = nested;
                        break;
                    }
                }
            }

            return JsonSerializer.Deserialize<Configuration.PluginConfiguration>(element.GetRawText(), options);
        }
        catch
        {
            // Best-effort only.
            return null;
        }
    }

    private string GetRequestHeaderValue(string name)
    {
        var headers = Request?.Headers;
        if (headers == null)
            return string.Empty;

        try
        {
            // Use non-generic enumeration to avoid runtime ABI issues with generic IDictionary methods.
            if (headers is System.Collections.IEnumerable enumerable)
            {
                foreach (var entry in enumerable)
                {
                    if (entry == null)
                        continue;

                    var entryType = entry.GetType();
                    var keyObj = entryType.GetProperty("Key")?.GetValue(entry);
                    if (keyObj is not string key || !key.Equals(name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var valueObj = entryType.GetProperty("Value")?.GetValue(entry);
                    return valueObj?.ToString() ?? string.Empty;
                }
            }
        }
        catch
        {
            // Best-effort only; header reads are optional.
        }

        return string.Empty;
    }

    private void SetResponseHeaderValue(string name, string value)
    {
        var headers = Response?.Headers;
        if (headers == null)
            return;

        try
        {
            var headersType = headers.GetType();

            var removeMethod = headersType.GetMethod("Remove", new[] { typeof(string) });
            removeMethod?.Invoke(headers, new object[] { name });

            // Prefer Append(string, string) if available.
            var appendMethod = headersType.GetMethod("Append", new[] { typeof(string), typeof(string) });
            if (appendMethod != null)
            {
                appendMethod.Invoke(headers, new object[] { name, value });
                return;
            }

            // Fallback to Add(string, string) if available.
            var addMethod = headersType.GetMethod("Add", new[] { typeof(string), typeof(string) });
            addMethod?.Invoke(headers, new object[] { name, value });
        }
        catch
        {
            // Best-effort only; missing optional headers should not break playback.
        }
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
    [HttpHead("resolve/{type}/{id}")]
    [HttpGet("resolve/{type}/{id}/stream.m3u8")]
    [HttpHead("resolve/{type}/{id}/stream.m3u8")]
    [AllowAnonymous] // FFmpeg needs to access this endpoint without authentication
    public async Task<IActionResult> ResolveStream(
        string type,
        string id,
        [FromQuery] string? season = null,
        [FromQuery] string? episode = null,
        [FromQuery] string? quality = null,
        [FromQuery] int? index = null,
        [FromQuery] string? hlsSeg = null,
        [FromQuery] Guid? userId = null)
    {
        var forceHls = Request.Path.Value?.Contains("/stream.m3u8", StringComparison.OrdinalIgnoreCase) == true;

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
            "Jfresolve: ResolveStream called - Type: {Type}, Id: {Id}, Season: {Season}, Episode: {Episode}, Quality: {Quality}, Index: {Index}, RequestPath: {Path}, Range: {Range}, HlsSeg: {HlsSeg}",
            type, id, season ?? "N/A", episode ?? "N/A", quality ?? "N/A", index?.ToString() ?? "N/A",
            Request.Path, GetRequestHeaderValue("Range"), string.IsNullOrWhiteSpace(hlsSeg) ? "N/A" : "yes"
        );

        if (!string.IsNullOrWhiteSpace(hlsSeg))
        {
            if (!TryDecodeHlsResourceUrl(hlsSeg, out var hlsResourceUrl))
            {
                return BadRequest("Invalid hlsSeg parameter");
            }

            try
            {
                var headOnly = HttpMethods.IsHead(Request.Method);
                _logger.LogInformation("Jfresolve: Proxying HLS sub-resource for {Type}/{Id}", type, id);
                if (TorBoxStreamService.IsHlsUrl(hlsResourceUrl))
                {
                    return await ProxyHlsPlaylistAsync(hlsResourceUrl, type, id, headOnly, userId, HttpContext.RequestAborted);
                }

                return await ProxyStreamAsync(hlsResourceUrl, type, id, headOnly, userId, allowHlsDispatch: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (!Response.HasStarted)
                    return StatusCode(502, "Failed to proxy HLS segment");
                return new EmptyResult();
            }
        }

        var config = GetPluginConfiguration();
        if (config == null)
        {
            _logger.LogError("Jfresolve: Plugin configuration is unavailable");
            return StatusCode(503, "Plugin configuration not ready");
        }

        // Check if addon manifest URL is configured
        if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
        {
            _logger.LogError("Jfresolve: Addon manifest URL not configured - cannot resolve stream");
            return NotFound("Addon manifest URL not configured. Please configure it in plugin settings.");
        }

        var isHlsPath = Request.Path.Value?.Contains("/stream.m3u8", StringComparison.OrdinalIgnoreCase) == true;
        var seekHls = !string.IsNullOrWhiteSpace(config.TorBoxApiKey) && DetectTorBoxSeekHls();
        var torBoxConfigured = !string.IsNullOrWhiteSpace(config.TorBoxApiKey);

        if (!seekHls)
        {
            SeekPositionCache.ClearSeekState();
            if (!isHlsPath)
                ClearActiveHlsPlayback(type, id);
        }

        if (torBoxConfigured)
        {
            forceHls = true;
            if (!isHlsPath && !seekHls)
            {
                _logger.LogInformation(
                    "Jfresolve: TorBox configured — using createstream HLS for {Type}/{Id} resolve request",
                    type, id);
            }
        }

        if (isHlsPath && torBoxConfigured)
        {
            _logger.LogInformation(
                "Jfresolve: stream.m3u8 request for {Type}/{Id} — using TorBox createstream HLS",
                type, id);
        }
        else if (isHlsPath && !seekHls)
        {
            _logger.LogInformation(
                "Jfresolve: stream.m3u8 request without TorBox for {Type}/{Id} — serving direct resolve",
                type, id);
            forceHls = false;
        }

        if (seekHls)
        {
            forceHls = true;
            _logger.LogInformation(
                "Jfresolve: Seek position pending for {Type}/{Id} — serving TorBox HLS at resolve path",
                type, id);
        }

        // Per-user preference: use user's setting when userId is present, otherwise global default
        var preferHdrOverDolbyVision = config.PreferHdrOverDolbyVision;
        if (userId.HasValue)
        {
            var userPrefsService = GetUserPreferencesService();
            if (userPrefsService != null)
            {
                var userPrefs = userPrefsService.Get(userId.Value);
                preferHdrOverDolbyVision = userPrefs.PreferHdrOverDolbyVision ?? config.PreferHdrOverDolbyVision;
            }
        }

        try
        {
            // Fast path: use cached TorBox HLS URL for stream.m3u8 (skip addon + createstream round-trip).
            if (isHlsPath
                && !string.IsNullOrWhiteSpace(config.TorBoxApiKey)
                && TorBoxPlaybackCache.TryGetHlsUrl(type, id, season, episode, out var cachedHlsUrl))
            {
                _logger.LogInformation(
                    "Jfresolve: Using cached TorBox HLS URL for {Type}/{Id} stream.m3u8",
                    type, id);
                var headOnlyCached = HttpMethods.IsHead(Request.Method);
                return await ProxyStreamAsync(cachedHlsUrl, type, id, headOnlyCached, userId);
            }

            // Resolve the redirect URL (from cache or by fetching from addon)
            var redirectUrl = await ResolveRedirectUrlAsync(type, id, season, episode, quality, index, config, preferHdrOverDolbyVision, userId, forceHls);
            
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                return NotFound("No suitable stream found");
            }

            // ResolveRedirectUrlAsync already normalizes TorBox URLs to /dld/ CDN or HLS.
            var headOnly = HttpMethods.IsHead(Request.Method);
            return await ProxyStreamAsync(redirectUrl, type, id, headOnly, userId);
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
        Configuration.PluginConfiguration config,
        bool preferHdrOverDolbyVision,
        Guid? userId = null,
        bool forceHls = false)
    {
        return await _playbackStreamResolver.ResolveStreamUrlAsync(
            new StreamResolveRequest(
                type,
                id,
                season,
                episode,
                quality,
                index,
                userId,
                preferHdrOverDolbyVision,
                forceHls,
                ShouldPreferHlsForSeek(type, id)),
            CancellationToken.None);
    }


    private static readonly ConcurrentDictionary<string, (DateTime Started, long Bytes)> _activeStreamTransfers = new();

    private static string BuildStreamSessionKey(string type, string id, string? season = null, string? episode = null)
    {
        var key = $"{type}/{id}";
        if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(episode))
            key += $":{season}:{episode}";
        return key;
    }

    private static void BeginStreamTransfer(string type, string id, string? season = null, string? episode = null)
    {
        _activeStreamTransfers[BuildStreamSessionKey(type, id, season, episode)] = (DateTime.UtcNow, 0);
    }

    private static void UpdateStreamTransfer(string type, string id, long bytesWritten)
    {
        var key = BuildStreamSessionKey(type, id);
        _activeStreamTransfers.AddOrUpdate(
            key,
            (DateTime.UtcNow, bytesWritten),
            (_, existing) => (existing.Started, bytesWritten));
    }

    private static void EndStreamTransfer(string type, string id)
    {
        _activeStreamTransfers.TryRemove(BuildStreamSessionKey(type, id), out _);
    }

    private static bool DetectTorBoxSeekHls() =>
        SeekPositionCache.TryPeekPending() is > 0;

    private static void ClearActiveHlsPlayback(string type, string id)
    {
        _activeHlsPlayback.TryRemove(BuildStreamSessionKey(type, id), out _);
    }

    private IActionResult RedirectToDirectResolvePath(string type, string id)
    {
        var path = $"/Plugins/Jfresolve/resolve/{type}/{id}";
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
        return Redirect(path + query);
    }

    private IActionResult RedirectToStreamM3u8Path(string type, string id)
    {
        var path = $"/Plugins/Jfresolve/resolve/{type}/{id}/stream.m3u8";
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
        var target = path + query;
        return Redirect(target);
    }

    private static void MarkRecentPlaybackDisconnect(string type, string id, long bytesWritten)
    {
        if (bytesWritten < SeekDetectionMinBytes)
            return;

        _recentPlaybackDisconnects[BuildStreamSessionKey(type, id)] = DateTime.UtcNow;
        SeekPositionCache.MarkSeekRestart();
    }

    private bool ShouldPreferHlsForSeek(string type, string id) =>
        DetectTorBoxSeekHls();

    private static void MarkActiveHlsPlayback(string type, string id)
    {
        _activeHlsPlayback[BuildStreamSessionKey(type, id)] = DateTime.UtcNow;
    }

    private static string EncodeHlsResourceUrl(string url)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeHlsResourceUrl(string encoded, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }

            var bytes = Convert.FromBase64String(base64);
            url = Encoding.UTF8.GetString(bytes);
            return IsValidStreamUrl(url);
        }
        catch
        {
            return false;
        }
    }

    private string BuildHlsProxyBaseUrl(Guid? userId)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        var query = QueryHelpers.ParseQuery(Request.QueryString.Value);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query)
        {
            if (pair.Key.Equals("hlsSeg", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(pair.Value))
                parameters[pair.Key] = pair.Value.ToString();
        }

        if (userId.HasValue)
            parameters["userId"] = userId.Value.ToString("N");

        return parameters.Count == 0 ? baseUrl : QueryHelpers.AddQueryString(baseUrl, parameters);
    }

    private static string BuildHlsProxyUrl(string proxyBaseUrl, string absoluteResourceUrl)
    {
        var encoded = EncodeHlsResourceUrl(absoluteResourceUrl);
        var separator = proxyBaseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{proxyBaseUrl}{separator}hlsSeg={encoded}";
    }

    private static bool IsTorBoxHlsPlaylistUrl(string playlistUrl) =>
        playlistUrl.Contains("tb-cdn.io", StringComparison.OrdinalIgnoreCase)
        && playlistUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string AppendTorBoxPlaylistQuery(string resourceUrl, Uri? playlistUri)
    {
        if (playlistUri == null || string.IsNullOrWhiteSpace(playlistUri.Query))
            return resourceUrl;

        if (resourceUrl.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return resourceUrl;

        var query = playlistUri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return resourceUrl;

        var separator = resourceUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{resourceUrl}{separator}{query}";
    }

    private sealed class HlsMediaSegment
    {
        public string Url { get; init; } = string.Empty;
        public double DurationSeconds { get; set; }
    }

    private static List<HlsMediaSegment> ParseHlsMediaSegments(string playlist)
    {
        var segments = new List<HlsMediaSegment>();
        double? pendingDuration = null;

        foreach (var line in playlist.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = trimmed.IndexOf(',');
                var durationPart = comma > 8 ? trimmed[8..comma] : trimmed[8..];
                if (double.TryParse(durationPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                    pendingDuration = duration;
                continue;
            }

            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                segments.Add(new HlsMediaSegment
                {
                    Url = trimmed,
                    DurationSeconds = pendingDuration ?? 0
                });
                pendingDuration = null;
            }
        }

        return segments;
    }

    private static string RewriteHlsPlaylist(
        string playlist,
        string playlistUrl,
        string proxyBaseUrl,
        bool useDirectSegmentUrls,
        long? totalRuntimeTicks = null,
        long? seekTicks = null)
    {
        if (!useDirectSegmentUrls)
        {
            return RewriteHlsPlaylistPassthrough(playlist, playlistUrl, proxyBaseUrl);
        }

        var segments = ParseHlsMediaSegments(playlist);
        if (segments.Count == 0)
            return playlist;

        var segmentSeconds = totalRuntimeTicks is > 0
            ? totalRuntimeTicks.Value / 10_000_000.0 / segments.Count
            : segments[0].DurationSeconds > 0
                ? segments[0].DurationSeconds
                : 5.0;

        var trimFrom = 0;
        if (seekTicks is > 0)
        {
            var seekSeconds = seekTicks.Value / 10_000_000.0;
            trimFrom = (int)Math.Floor(seekSeconds / segmentSeconds);
            if (trimFrom < 0)
                trimFrom = 0;
            if (trimFrom >= segments.Count)
                trimFrom = Math.Max(0, segments.Count - 1);
        }

        Uri.TryCreate(playlistUrl, UriKind.Absolute, out var playlistUri);
        var targetDuration = Math.Max(1, (int)Math.Ceiling(segmentSeconds));
        var sb = new StringBuilder(playlist.Length + 256);
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{targetDuration}");
        if (trimFrom > 0)
            sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{trimFrom}");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

        for (var i = trimFrom; i < segments.Count; i++)
        {
            var segmentUrl = segments[i].Url;
            string absoluteUrl;
            if (Uri.TryCreate(segmentUrl, UriKind.Absolute, out var absoluteUri))
            {
                absoluteUrl = absoluteUri.ToString();
            }
            else if (playlistUri != null && Uri.TryCreate(playlistUri, segmentUrl, out var relativeUri))
            {
                absoluteUrl = relativeUri.ToString();
            }
            else
            {
                absoluteUrl = segmentUrl;
            }

            absoluteUrl = AppendTorBoxPlaylistQuery(absoluteUrl, playlistUri);
            if (!IsValidStreamUrl(absoluteUrl))
                continue;

            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{segmentSeconds:F3},\n");
            sb.AppendLine(absoluteUrl);
        }

        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }

    /// <summary>
    /// Resolve relative segment URLs to absolute TorBox CDN URLs while preserving original EXTINF timing.
    /// </summary>
    private static string RewriteHlsPlaylistDirectPassthrough(string playlist, string playlistUrl)
    {
        Uri.TryCreate(playlistUrl, UriKind.Absolute, out var playlistUri);
        var sb = new StringBuilder(playlist.Length + 256);

        foreach (var line in playlist.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                sb.AppendLine(trimmed);
                continue;
            }

            string absoluteUrl;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            {
                absoluteUrl = absoluteUri.ToString();
            }
            else if (playlistUri != null && Uri.TryCreate(playlistUri, trimmed, out var relativeUri))
            {
                absoluteUrl = relativeUri.ToString();
            }
            else
            {
                sb.AppendLine(trimmed);
                continue;
            }

            absoluteUrl = AppendTorBoxPlaylistQuery(absoluteUrl, playlistUri);
            if (!IsValidStreamUrl(absoluteUrl))
            {
                sb.AppendLine(trimmed);
                continue;
            }

            sb.AppendLine(absoluteUrl);
        }

        return sb.ToString();
    }

    private static string RewriteHlsPlaylistPassthrough(
        string playlist,
        string playlistUrl,
        string proxyBaseUrl)
    {
        Uri.TryCreate(playlistUrl, UriKind.Absolute, out var playlistUri);
        var sb = new StringBuilder(playlist.Length + 256);

        foreach (var line in playlist.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                sb.AppendLine(trimmed);
                continue;
            }

            string absoluteUrl;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            {
                absoluteUrl = absoluteUri.ToString();
            }
            else if (playlistUri != null && Uri.TryCreate(playlistUri, trimmed, out var relativeUri))
            {
                absoluteUrl = relativeUri.ToString();
            }
            else
            {
                sb.AppendLine(trimmed);
                continue;
            }

            absoluteUrl = AppendTorBoxPlaylistQuery(absoluteUrl, playlistUri);

            if (!IsValidStreamUrl(absoluteUrl))
            {
                sb.AppendLine(trimmed);
                continue;
            }

            sb.AppendLine(BuildHlsProxyUrl(proxyBaseUrl, absoluteUrl));
        }

        return sb.ToString();
    }

    private async Task<IActionResult> ProxyHlsPlaylistAsync(
        string playlistUrl,
        string type,
        string id,
        bool headOnly,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Jfresolve: Proxying TorBox HLS playlist from {Url}", playlistUrl);

        SetResponseHeaderValue("Cache-Control", Constants.CacheControlNoCache);
        SetResponseHeaderValue("Pragma", Constants.PragmaNoCache);
        SetResponseHeaderValue("Expires", Constants.ExpiresZero);
        Response.ContentType = "application/vnd.apple.mpegurl";
        SetResponseHeaderValue("Content-Disposition", "inline; filename=\"stream.m3u8\"");

        if (headOnly)
        {
            return new EmptyResult();
        }

        var client = _httpClientFactory.CreateClient("Jfresolve.Stream");
        using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Jfresolve: Failed to fetch HLS playlist {Status} from {Url}",
                (int)response.StatusCode, playlistUrl);
            return StatusCode((int)response.StatusCode, "Failed to fetch HLS playlist");
        }

        var playlist = await response.Content.ReadAsStringAsync(cancellationToken);
        var useDirectSegmentUrls = IsTorBoxHlsPlaylistUrl(playlistUrl);
        var season = Request.Query["season"].FirstOrDefault();
        var episode = Request.Query["episode"].FirstOrDefault();
        long? seekTicks = SeekPositionCache.TryPeekPending();
        if (useDirectSegmentUrls)
        {
            MarkActiveHlsPlayback(type, id);
            if (seekTicks.HasValue)
            {
                _logger.LogInformation(
                    "Jfresolve: TorBox HLS seek target {Seconds:F1}s for {Type}/{Id}",
                    seekTicks.Value / 10_000_000.0, type, id);
            }

            _logger.LogInformation(
                "Jfresolve: Passing through TorBox HLS playlist with direct CDN segment URLs for {Type}/{Id}",
                type, id);
        }

        string rewritten = useDirectSegmentUrls
            ? RewriteHlsPlaylistDirectPassthrough(playlist, playlistUrl)
            : RewriteHlsPlaylistPassthrough(playlist, playlistUrl, BuildHlsProxyBaseUrl(userId));
        SeekPositionCache.TryConsumePending();
        return Content(rewritten, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Proxies the stream from the redirect URL to the client
    /// </summary>
    private async Task<IActionResult> ProxyStreamAsync(
        string redirectUrl,
        string type,
        string id,
        bool headOnly = false,
        Guid? userId = null,
        bool allowHlsDispatch = true)
    {
        if (allowHlsDispatch && TorBoxStreamService.IsHlsUrl(redirectUrl))
        {
            return await ProxyHlsPlaylistAsync(redirectUrl, type, id, headOnly, userId, HttpContext.RequestAborted);
        }

            // Jellyfin 10.11.6 compatibility: Proxy the stream instead of redirecting
            // FFmpeg in 10.11.6 doesn't properly follow HTTP redirects from plugin endpoints
            // By proxying, FFmpeg gets the stream directly without needing to follow redirects
            // IMPORTANT: Must support HTTP Range requests (206 Partial Content) for FFmpeg seeking
            try
            {
                _logger.LogInformation("Jfresolve: Proxying stream from {RedirectUrl}", redirectUrl);

                if (!headOnly)
                    BeginStreamTransfer(type, id);
                
            // Disable response buffering for optimal streaming performance
            SetResponseHeaderValue("Cache-Control", Constants.CacheControlNoCache);
            SetResponseHeaderValue("Pragma", Constants.PragmaNoCache);
            SetResponseHeaderValue("Expires", Constants.ExpiresZero);
            
            var streamHttpClient = _httpClientFactory.CreateClient("Jfresolve.Stream");
            // Use a very long timeout (4 hours) to handle long movies/episodes without interruption
            // The timeout applies to the entire operation including all read operations
            streamHttpClient.Timeout = TimeSpan.FromHours(Constants.StreamRequestTimeoutHours);

            var cancellationToken = HttpContext.RequestAborted;
            var upstreamMethod = headOnly ? HttpMethod.Head : HttpMethod.Get;
                
                // Handle HTTP Range requests for seeking (required by FFmpeg)
                var rangeHeader = GetRequestHeaderValue("Range");
                var rangeInfo = ParseRangeInfo(rangeHeader);
                long? rangeStart = rangeInfo.Start;

                // TorBox HLS segments return bogus 2GB Content-Length when Range is forwarded.
                if (TorBoxStreamService.IsHlsSegmentUrl(redirectUrl))
                {
                    rangeHeader = null;
                    rangeStart = null;
                    rangeInfo = default;
                }

                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    _logger.LogDebug("Jfresolve: Range request detected: {Range} (suffix={Suffix}, start={Start})",
                        rangeHeader, rangeInfo.IsSuffixOnly, rangeStart);
                }

                // MKV cue loading uses suffix ranges (bytes=-N). Convert to absolute once total size is known.
                if (rangeInfo.IsSuffixOnly)
                {
                    var suffixProbeLength = TryGetCachedUpstreamContentLength(redirectUrl)
                        ?? await ProbeUpstreamContentLengthAsync(streamHttpClient, redirectUrl, cancellationToken);
                    if (suffixProbeLength.HasValue)
                    {
                        CacheUpstreamContentLength(redirectUrl, suffixProbeLength.Value);
                        rangeHeader = NormalizeRangeHeader(rangeHeader, suffixProbeLength.Value);
                        rangeInfo = ParseRangeInfo(rangeHeader);
                        rangeStart = rangeInfo.Start;
                        _logger.LogInformation(
                            "Jfresolve: Normalized suffix Range to {Range} (total={Total})",
                            rangeHeader, suffixProbeLength.Value);
                    }
                }
                
                // Stream the content directly to the response
            // Use RequestAborted cancellation token so upstream request is cancelled when client disconnects
            // This ensures immediate cleanup when user stops playback
            
            // Check cache for final resolved URL (after redirects) to speed up resume
            string? finalUrl = null;

            // TorBox/debrid CDNs are short-lived — always follow the permalink/redirect chain per request.
            // Never reuse a cached CDN URL (stale links break MKV range/cue reads on seek).
            HttpResponseMessage? streamResponse = null;
            HttpResponseMessage? initialResponse = null;
            
            // Follow redirects to get final URL (every request)
            initialResponse = await ExecuteStreamRequestWithRetryAsync(
                streamHttpClient,
                () =>
                {
                    var retryRequest = new HttpRequestMessage(upstreamMethod, redirectUrl);
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
            streamResponse = await FollowRedirectsAsync(streamHttpClient, initialResponse, redirectUrl, 5, upstreamMethod, cancellationToken);
            if (streamResponse == null)
            {
                initialResponse?.Dispose();
                _logger.LogError("Jfresolve: Failed to follow redirects for {RedirectUrl}", redirectUrl);
                return StatusCode(502, "Failed to resolve stream URL after redirects");
            }

            finalUrl = streamResponse.RequestMessage?.RequestUri?.ToString();
            if (!string.IsNullOrEmpty(finalUrl) && finalUrl != redirectUrl)
            {
                _logger.LogDebug("Jfresolve: Resolved {RedirectUrl} -> {FinalUrl}", redirectUrl, finalUrl);
            }

            // Use the final response (after following redirects)
            HttpResponseMessage? activeStreamResponse = streamResponse;

            try
            {
                if (activeStreamResponse == null || !activeStreamResponse.IsSuccessStatusCode)
                {
                    DisposeIfDifferent(initialResponse, activeStreamResponse);
                    return activeStreamResponse == null
                        ? StatusCode(502, "Failed to connect to stream")
                        : HandleStreamError(activeStreamResponse, redirectUrl, type, id);
                }

                DisposeIfDifferent(initialResponse, activeStreamResponse);

                var useRangeWorkaround = RequiresClientSideRangeWorkaround(activeStreamResponse, rangeStart, rangeHeader);

                // Upstream returned 206 but wrong offset — retry once through the original redirect URL with Range
                // before falling back to client-side skipping (which only works for full 200 responses).
                if (useRangeWorkaround &&
                    activeStreamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                    !string.IsNullOrEmpty(rangeHeader))
                {
                    _logger.LogInformation(
                        "Jfresolve: Upstream 206 Content-Range mismatch for seek (requested bytes={Start}), re-resolving with Range via redirect URL",
                        rangeStart);

                    activeStreamResponse.Dispose();
                    activeStreamResponse = null;

                    var rangeRetryResponse = await ExecuteStreamRequestWithRetryAsync(
                        streamHttpClient,
                        () =>
                        {
                        var retryRequest = new HttpRequestMessage(upstreamMethod, redirectUrl);
                        retryRequest.Headers.Add("Range", rangeHeader);
                        return retryRequest;
                    },
                    $"range seek retry via redirect URL {redirectUrl}",
                    cancellationToken);

                if (rangeRetryResponse == null)
                {
                    _logger.LogError("Jfresolve: Range seek retry failed for {RedirectUrl}", redirectUrl);
                    return StatusCode(502, "Failed to seek in stream after retries");
                }

                activeStreamResponse = await FollowRedirectsAsync(streamHttpClient, rangeRetryResponse, redirectUrl, 5, upstreamMethod, cancellationToken);
                    DisposeIfDifferent(rangeRetryResponse, activeStreamResponse);

                    if (activeStreamResponse == null || !activeStreamResponse.IsSuccessStatusCode)
                    {
                        activeStreamResponse?.Dispose();
                        _logger.LogError("Jfresolve: Failed to follow redirects during range seek retry for {RedirectUrl}", redirectUrl);
                        return StatusCode(502, "Failed to resolve stream URL after seek retry");
                    }

                    finalUrl = activeStreamResponse.RequestMessage?.RequestUri?.ToString();
                    useRangeWorkaround = RequiresClientSideRangeWorkaround(activeStreamResponse, rangeStart, rangeHeader);
                }

                if (useRangeWorkaround && rangeStart.HasValue)
                {
                    if (activeStreamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
                    {
                        _logger.LogWarning(
                            "Jfresolve: Upstream still returned mismatched 206 after seek retry (requested bytes={Start}); seek may fail",
                            rangeStart.Value);
                        useRangeWorkaround = false;
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Jfresolve: Using client-side range workaround for seek (requested bytes={Start}, upstream status={Status})",
                            rangeStart.Value, (int)activeStreamResponse.StatusCode);
                    }
                }

                var cacheKey = finalUrl ?? redirectUrl;
                var knownTotalLength = GetUpstreamTotalContentLength(activeStreamResponse)
                    ?? TryGetCachedUpstreamContentLength(cacheKey)
                    ?? TryGetCachedUpstreamContentLength(redirectUrl);

                if (!knownTotalLength.HasValue)
                {
                    var probedLength = await ProbeUpstreamContentLengthAsync(streamHttpClient, cacheKey, cancellationToken)
                        ?? await ProbeUpstreamContentLengthAsync(streamHttpClient, redirectUrl, cancellationToken);
                    if (probedLength.HasValue)
                    {
                        knownTotalLength = probedLength.Value;
                        CacheUpstreamContentLength(cacheKey, probedLength.Value);
                        CacheUpstreamContentLength(redirectUrl, probedLength.Value);
                        _logger.LogInformation(
                            "Jfresolve: Probed upstream Content-Length {Length} for {Url}",
                            probedLength.Value, cacheKey);
                    }
                }

                if (useRangeWorkaround && rangeStart.HasValue &&
                    !knownTotalLength.HasValue)
                {
                    var probedLength = await ProbeUpstreamContentLengthAsync(streamHttpClient, cacheKey, cancellationToken);
                    if (probedLength.HasValue)
                    {
                        knownTotalLength = probedLength.Value;
                        CacheUpstreamContentLength(cacheKey, probedLength.Value);
                        _logger.LogInformation(
                            "Jfresolve: Probed upstream Content-Length {Length} for seek workaround on {Url}",
                            probedLength.Value, cacheKey);
                    }
                }

                CopyStreamResponseHeaders(activeStreamResponse, rangeStart, useRangeWorkaround, cacheKey, knownTotalLength);

                if (headOnly)
                {
                    _logger.LogDebug("Jfresolve: HEAD probe complete for {RedirectUrl} (status {Status}, length {Length})",
                        redirectUrl, Response.StatusCode, Response.ContentLength);
                    return new EmptyResult();
                }

                // Build delegate to reconnect from byte offset when upstream drops mid-stream.
                // Kodi/JellyCon (https://github.com/jellyfin/jellycon): JellyCon passes the play URL to Kodi via
                // list_item.setPath(playurl); Kodi then opens that URL and reads the stream. Playback can drop
                // every ~10 min on Kodi (upstream limit or Kodi closing the connection). Transparent reconnect
                // keeps the stream alive without the client seeing an error.
                string? urlForReconnect = redirectUrl;
                Func<long, Task<(Stream? stream, IDisposable? toDispose)>>? getStreamFromOffset = null;
                if (!string.IsNullOrEmpty(urlForReconnect))
                {
                    getStreamFromOffset = async (offset) =>
                    {
                        // Always re-resolve through the permalink/redirect URL so TorBox/debrid CDNs negotiate Range on a fresh redirect chain.
                        var reconnectUrl = redirectUrl;
                        var req = new HttpRequestMessage(HttpMethod.Get, reconnectUrl);
                        req.Headers.Add("Range", $"bytes={offset}-");
                        var resp = await streamHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (resp == null || !resp.IsSuccessStatusCode)
                        {
                            resp?.Dispose();
                            return (null, null);
                        }

                        var redirected = await FollowRedirectsAsync(streamHttpClient, resp, redirectUrl, 5, HttpMethod.Get, cancellationToken);
                        DisposeIfDifferent(resp, redirected);
                        if (redirected == null || !redirected.IsSuccessStatusCode)
                        {
                            redirected?.Dispose();
                            return (null, null);
                        }

                        var redirectedStream = await redirected.Content.ReadAsStreamAsync();
                        return (redirectedStream, redirected);
                    };
                }

                var (_, stopReason) = await StreamContentAsync(activeStreamResponse, type, id, rangeStart, cancellationToken, getStreamFromOffset, useRangeWorkaround);
                if (stopReason == StreamStopReason.UpstreamFailure)
                {
                    _logger.LogWarning("Jfresolve: Stream ended after upstream failure for {Type}/{Id} (reconnect exhausted or unavailable)", type, id);
                }
            }
            finally
            {
                activeStreamResponse?.Dispose();
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
    private void CopyStreamResponseHeaders(
        HttpResponseMessage streamResponse,
        long? rangeStart,
        bool useRangeWorkaround,
        string cacheKey,
        long? knownTotalLength = null)
    {
        knownTotalLength ??= GetUpstreamTotalContentLength(streamResponse)
            ?? TryGetCachedUpstreamContentLength(cacheKey);

        if (useRangeWorkaround && rangeStart.HasValue)
        {
            // Upstream ignored or mishandled the Range request — skip bytes ourselves and synthesize 206 headers.
            Response.StatusCode = 206;

            long? totalLength = knownTotalLength;
            if (!totalLength.HasValue)
            {
                totalLength = GetUpstreamTotalContentLength(streamResponse)
                    ?? TryGetCachedUpstreamContentLength(cacheKey);
            }

            if (totalLength.HasValue)
            {
                long start = rangeStart.Value;
                long end = totalLength.Value - 1;
                long rangeLength = totalLength.Value - start;

                SetResponseHeaderValue("Content-Range", $"bytes {start}-{end}/{totalLength.Value}");
                Response.ContentLength = rangeLength;
                CacheUpstreamContentLength(cacheKey, totalLength.Value);

                _logger.LogInformation(
                    "Jfresolve: Client-side range workaround (Range: bytes {Start}-{End}/{Total}, Content-Length: {Length})",
                    start, end, totalLength.Value, rangeLength);
            }
            else
            {
                _logger.LogWarning(
                    "Jfresolve: Client-side range workaround without known total length (skipping {Bytes} bytes)",
                    rangeStart.Value);
            }
        }
        else
        {
            Response.StatusCode = (int)streamResponse.StatusCode;

            var contentRangeValue = GetContentRangeHeader(streamResponse);
            if (!string.IsNullOrEmpty(contentRangeValue))
            {
                if (knownTotalLength.HasValue)
                {
                    var (rangeStartPos, rangeEndPos, _) = ParseContentRangeDetails(contentRangeValue);
                    if (rangeStartPos.HasValue && rangeEndPos.HasValue)
                    {
                        contentRangeValue = $"bytes {rangeStartPos.Value}-{rangeEndPos.Value}/{knownTotalLength.Value}";
                    }
                }

                SetResponseHeaderValue("Content-Range", contentRangeValue);
            }

            if (streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                streamResponse.Content.Headers.ContentLength.HasValue)
            {
                var upstreamLength = streamResponse.Content.Headers.ContentLength.Value;
                if (!TorBoxStreamService.IsSuspectUpstreamContentLength(upstreamLength))
                {
                    Response.ContentLength = upstreamLength;
                }
                else
                {
                    _logger.LogDebug(
                        "Jfresolve: Omitting suspect upstream Content-Length {Length} for segment proxy",
                        upstreamLength);
                }
            }
            else if (streamResponse.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // FFmpeg MKV seeks need total file size (Content-Length) to calculate HTTP byte offsets.
                if (knownTotalLength.HasValue)
                {
                    Response.ContentLength = knownTotalLength.Value;
                    CacheUpstreamContentLength(cacheKey, knownTotalLength.Value);
                    _logger.LogDebug("Jfresolve: Forwarding Content-Length {Length} for {CacheKey}", knownTotalLength.Value, cacheKey);
                }
            }
            else if (streamResponse.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                     knownTotalLength.HasValue &&
                     !Response.ContentLength.HasValue)
            {
                var contentRangeValue2 = GetContentRangeHeader(streamResponse);
                var (startPos, endPos, _) = ParseContentRangeDetails(contentRangeValue2 ?? string.Empty);
                if (startPos.HasValue && endPos.HasValue)
                {
                    Response.ContentLength = endPos.Value - startPos.Value + 1;
                }
            }
        }

        if (knownTotalLength.HasValue)
        {
            CacheUpstreamContentLength(cacheKey, knownTotalLength.Value);
            if (!Response.ContentLength.HasValue &&
                !useRangeWorkaround &&
                Response.StatusCode == StatusCodes.Status200OK)
            {
                Response.ContentLength = knownTotalLength.Value;
            }
        }

        if (Request.Path.Value?.Contains("/stream.m3u8", StringComparison.OrdinalIgnoreCase) == true)
        {
            Response.ContentType = "video/x-matroska";
        }
        else if (streamResponse.Content.Headers.ContentType != null)
        {
            Response.ContentType = streamResponse.Content.Headers.ContentType.ToString();
        }

        SetResponseHeaderValue("Accept-Ranges", Constants.AcceptRangesBytes);
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
        Func<long, Task<(Stream? stream, IDisposable? toDispose)>>? getStreamFromOffset = null,
        bool useRangeWorkaround = false)
    {
        const int bufferSize = Constants.StreamBufferSize;
        const int flushInterval = Constants.StreamFlushInterval;
        var buffer = new byte[bufferSize];
        long totalBytesWritten = 0;
        int reconnectCount = 0;
        IDisposable? reconnectResponseToDispose = null;
        try
        {
            bool needToSkipBytes = useRangeWorkaround && rangeStart.HasValue;
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
                        if (totalBytesWritten % (10 * 1024 * 1024) < bytesRead)
                            UpdateStreamTransfer(type, id, totalBytesWritten);
                        bufferCount++;
                        bool inPostSeekWindow = rangeStart.HasValue && totalBytesWritten < Constants.StreamFlushEveryBufferUntilBytesAfterSeek;
                        if (inPostSeekWindow || bufferCount == 1 || bufferCount % flushInterval == 0)
                            await Response.Body.FlushAsync(cancellationToken);
                    }

                    if (!cancellationToken.IsCancellationRequested)
                        await Response.Body.FlushAsync(cancellationToken);
                    return (totalBytesWritten, StreamStopReason.Completed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Jfresolve: Client disconnected (playback stopped) for {Type}/{Id} after ~{Bytes} bytes", type, id, totalBytesWritten);
                    MarkRecentPlaybackDisconnect(type, id, totalBytesWritten);
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
            UpdateStreamTransfer(type, id, totalBytesWritten);
            EndStreamTransfer(type, id);
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
        var config = GetPluginConfiguration();
        var torBoxConfigured = !string.IsNullOrWhiteSpace(config?.TorBoxApiKey);
        var realDebridConfigured = !string.IsNullOrWhiteSpace(config?.RealDebridApiKey);

        return Ok(new
        {
            plugin = "Jfresolve",
            version = JfresolvePlugin.Instance?.Version?.ToString() ?? "Unknown",
            message = "API controller is working!",
            manifestConfigured = !string.IsNullOrWhiteSpace(config?.AddonManifestUrl),
            torBoxConfigured,
            realDebridConfigured,
            debridFallbackEnabled = torBoxConfigured && realDebridConfigured
        });
    }

    /// <summary>
    /// Serves a standalone HTML page for user playback preferences.
    /// Non-admin users can open this URL directly (no Dashboard needed). Requires login.
    /// </summary>
    [HttpGet("user-settings/page")]
    public async Task<IActionResult> GetUserSettingsPage()
    {
        var userId = await TryGetCurrentUserIdAsync();
        if (userId == null)
            return Unauthorized("Must be logged in to open this page");

        var html = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Jfresolve – Playback preferences</title>
    <style>
        body { font-family: system-ui, sans-serif; background: #1a1a1a; color: #eee; margin: 0; padding: 24px; }
        .card { background: #252525; border-radius: 12px; padding: 24px; max-width: 520px; margin: 0 auto; border: 1px solid #333; }
        h1 { margin: 0 0 20px; font-size: 1.5rem; color: #00a4dc; }
        label { display: flex; align-items: center; gap: 10px; cursor: pointer; margin-bottom: 8px; }
        input[type="checkbox"] { width: 18px; height: 18px; }
        .hint { color: #aaa; font-size: 0.9em; margin: 8px 0 20px; line-height: 1.4; }
        button { background: #00a4dc; color: #fff; border: none; padding: 10px 24px; border-radius: 8px; font-size: 1rem; cursor: pointer; }
        button:hover { background: #0090c0; }
        .msg { margin-top: 16px; font-size: 0.9em; }
        .msg.ok { color: #4ade80; }
        .msg.err { color: #f87171; }
    </style>
</head>
<body>
    <div class="card">
        <h1>Jfresolve – Playback preferences</h1>
        <form id="form">
            <label>
                <input type="checkbox" id="preferHdr" />
                <span>Prefer HDR over Dolby Vision</span>
            </label>
            <p class="hint">When enabled, at the same resolution the plugin picks HDR (e.g. HDR10) instead of Dolby Vision. Use if your player does not support Dolby Vision.</p>
            <button type="submit">Save</button>
        </form>
        <p id="msg" class="msg" aria-live="polite"></p>
    </div>
    <script>
        (function() {
            var apiBase = location.origin + location.pathname.replace(/\/page\/?$/, '').replace(/\/$/, '');
            var form = document.getElementById('form');
            var preferHdr = document.getElementById('preferHdr');
            var msg = document.getElementById('msg');
            function show(m, ok) { msg.textContent = m; msg.className = 'msg ' + (ok ? 'ok' : 'err'); }
            fetch(apiBase, { credentials: 'include' })
                .then(function(r) { if (!r.ok) throw new Error(r.status); return r.json(); })
                .then(function(d) { preferHdr.checked = d.preferHdrOverDolbyVision !== false; })
                .catch(function() { preferHdr.checked = true; });
            form.onsubmit = function(e) {
                e.preventDefault();
                msg.textContent = '';
                fetch(apiBase, {
                    method: 'POST',
                    credentials: 'include',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ preferHdrOverDolbyVision: preferHdr.checked })
                }).then(function(r) {
                    if (!r.ok) throw new Error(r.status);
                    show('Settings saved.', true);
                }).catch(function() { show('Failed to save. Are you logged in?', false); });
                return false;
            };
        })();
    </script>
</body>
</html>
""";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Gets the current user's playback preferences (per-user settings).
    /// Requires authentication.
    /// </summary>
    [HttpGet("user-settings")]
    public async Task<IActionResult> GetUserSettings()
    {
        var userId = await TryGetCurrentUserIdAsync();
        if (userId == null)
            return Unauthorized("Must be logged in to view user settings");

        var config = GetPluginConfiguration();
        var userPrefsService = GetUserPreferencesService();
        var prefs = userPrefsService?.Get(userId.Value) ?? new Configuration.UserPlaybackPrefs();
        return Ok(new
        {
            preferHdrOverDolbyVision = prefs.PreferHdrOverDolbyVision ?? config?.PreferHdrOverDolbyVision ?? true
        });
    }

    /// <summary>
    /// Saves the current user's playback preferences (per-user settings).
    /// Requires authentication.
    /// </summary>
    [HttpPost("user-settings")]
    public async Task<IActionResult> PostUserSettings([FromBody] UserSettingsDto dto)
    {
        var userId = await TryGetCurrentUserIdAsync();
        if (userId == null)
            return Unauthorized("Must be logged in to save user settings");

        var userPrefsService = GetUserPreferencesService();
        if (userPrefsService == null)
            return StatusCode(503, "User preferences service is unavailable");

        var prefs = userPrefsService.Get(userId.Value);
        if (dto.PreferHdrOverDolbyVision.HasValue)
            prefs.PreferHdrOverDolbyVision = dto.PreferHdrOverDolbyVision.Value;
        userPrefsService.Set(userId.Value, prefs);
        return Ok(new { saved = true });
    }

    private async Task<Guid?> TryGetCurrentUserIdAsync()
    {
        var httpContext = HttpContext;
        if (httpContext == null)
            return null;

        try
        {
            var authContext = httpContext.RequestServices.GetService<IAuthorizationContext>();
            if (authContext != null)
            {
                var authInfo = await authContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
                if (authInfo != null && authInfo.IsAuthenticated)
                {
                    if (authInfo.UserId != Guid.Empty)
                        return authInfo.UserId;
                    if (authInfo.User != null)
                        return authInfo.User.Id;
                }
            }
        }
        catch
        {
            // Fall through to Claims
        }
        var claim = httpContext.User?.Claims?.FirstOrDefault(c =>
            c.Type is "sub" or "UserId" or "Jellyfin-UserId");
        if (claim != null && Guid.TryParse(claim.Value, out var guid))
            return guid;
        return null;
    }

    private IActionResult HandleStreamError(HttpResponseMessage response, string redirectUrl, string type, string id)
    {
        var statusCode = (int)response.StatusCode;
        string errorMessage;
        int httpStatusCode;

        if (statusCode is 401 or 403)
        {
            errorMessage = "Authentication failed: Stream server requires authentication or access is denied";
            httpStatusCode = 502;
            _logger.LogWarning("Jfresolve: Authentication error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}",
                statusCode, type, id, redirectUrl);
        }
        else if (statusCode == 404)
        {
            errorMessage = "Stream not found: The requested stream is no longer available";
            httpStatusCode = 404;
            _logger.LogWarning("Jfresolve: Stream not found (404) for {Type}/{Id} from {RedirectUrl}",
                type, id, redirectUrl);
        }
        else if (statusCode >= 500 && statusCode < 600)
        {
            errorMessage = "Stream server error: The stream server is experiencing issues";
            httpStatusCode = 502;
            _logger.LogError("Jfresolve: Stream server error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}",
                statusCode, type, id, redirectUrl);
        }
        else if (statusCode == 429)
        {
            errorMessage = "Rate limit exceeded: Too many requests to stream server";
            httpStatusCode = 503;
            _logger.LogWarning("Jfresolve: Rate limit (429) for {Type}/{Id} from {RedirectUrl}",
                type, id, redirectUrl);
        }
        else if (statusCode >= 400 && statusCode < 500)
        {
            errorMessage = $"Stream request error: The stream server rejected the request (HTTP {statusCode})";
            httpStatusCode = 502;
            _logger.LogWarning("Jfresolve: Client error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}",
                statusCode, type, id, redirectUrl);
        }
        else
        {
            errorMessage = $"Unexpected stream error: HTTP {statusCode}";
            httpStatusCode = 502;
            _logger.LogError("Jfresolve: Unexpected error ({StatusCode}) for {Type}/{Id} from {RedirectUrl}",
                statusCode, type, id, redirectUrl);
        }

        response.Dispose();
        return StatusCode(httpStatusCode, errorMessage);
    }

    private async Task<HttpResponseMessage?> ExecuteStreamRequestWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        string operationName,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < Constants.MaxStreamRetryAttempts; attempt++)
        {
            HttpRequestMessage? request = null;
            try
            {
                request = requestFactory();
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.IsSuccessStatusCode || ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400))
                    return response;

                if ((int)response.StatusCode is >= 400 and < 500)
                    return response;

                if ((int)response.StatusCode >= 500 && attempt < Constants.MaxStreamRetryAttempts - 1)
                {
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

                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException)
            {
                lastException = ex;
                if (cancellationToken.IsCancellationRequested)
                {
                    request?.Dispose();
                    throw;
                }

                if (attempt < Constants.MaxStreamRetryAttempts - 1)
                {
                    request?.Dispose();
                    var delay = Constants.StreamRetryDelays[Math.Min(attempt, Constants.StreamRetryDelays.Length - 1)];
                    _logger.LogWarning(
                        ex,
                        "Jfresolve: Stream {Operation} failed, retrying in {Delay}ms (attempt {Attempt}/{Max})",
                        operationName, delay, attempt + 1, Constants.MaxStreamRetryAttempts);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        if (lastException != null)
        {
            _logger.LogError(lastException,
                "Jfresolve: Stream {Operation} failed after {MaxAttempts} attempts",
                operationName, Constants.MaxStreamRetryAttempts);
        }

        return null;
    }

    /// <summary>
    /// Checks if the request is authorized to access the stream endpoint
    /// Allows requests from localhost, server's own IP (including Docker), or authenticated Jellyfin users
    /// </summary>
    private bool IsRequestAuthorized()
    {
        // Kept for compatibility with older code paths; resolve endpoint no longer enforces this check.
        return true;

        /*
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
        var referer = GetRequestHeaderValue("Referer");
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
        var userAgent = GetRequestHeaderValue("User-Agent");
        if (!string.IsNullOrWhiteSpace(userAgent) && 
            (userAgent.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase) ||
             userAgent.Contains("Emby", StringComparison.OrdinalIgnoreCase)))
        {
            return true; // Request from Jellyfin/Emby client
        }

        return false; // Not authorized
        */
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

    private static void DisposeIfDifferent(HttpResponseMessage? toDispose, HttpResponseMessage? keepAlive)
    {
        if (toDispose != null && !ReferenceEquals(toDispose, keepAlive))
        {
            toDispose.Dispose();
        }
    }

    /// <summary>
    /// Follows HTTP redirects (302, 301, etc.) up to a maximum number of redirects
    /// </summary>
    private async Task<HttpResponseMessage?> FollowRedirectsAsync(
        HttpClient httpClient,
        HttpResponseMessage response,
        string originalUrl,
        int maxRedirects,
        HttpMethod method,
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
            var redirectRequest = new HttpRequestMessage(method, location);
            
            // Preserve Range header from original request if present
            // Note: We need to get this from the original request context
            // For now, we'll preserve it from the current HTTP context
            var rangeHeader = GetRequestHeaderValue("Range");
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                redirectRequest.Headers.Add("Range", rangeHeader);
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

    private static long? GetUpstreamTotalContentLength(HttpResponseMessage response)
    {
        var contentRange = GetContentRangeHeader(response);
        if (!string.IsNullOrEmpty(contentRange))
        {
            var (_, total) = ParseContentRange(contentRange);
            if (total.HasValue)
                return total.Value;
        }

        if (response.Content.Headers.ContentLength.HasValue)
            return response.Content.Headers.ContentLength.Value;

        return null;
    }

    private static void CacheUpstreamContentLength(string cacheKey, long contentLength)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || contentLength <= 0)
            return;

        _upstreamContentLengthCache.AddOrUpdate(
            cacheKey,
            (contentLength, DateTime.UtcNow.Add(Constants.ResolvedUrlCacheExpiry)),
            (_, _) => (contentLength, DateTime.UtcNow.Add(Constants.ResolvedUrlCacheExpiry)));
    }

    private static long? TryGetCachedUpstreamContentLength(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return null;

        if (_upstreamContentLengthCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.ContentLength;

        return null;
    }

    private async Task<long?> ProbeUpstreamContentLengthAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (headResponse.IsSuccessStatusCode)
            {
                var length = GetUpstreamTotalContentLength(headResponse);
                if (length.HasValue)
                    return length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: HEAD probe failed for {Url}", url);
        }

        try
        {
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Add("Range", "bytes=0-0");
            using var rangeResponse = await httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (rangeResponse.IsSuccessStatusCode)
                return GetUpstreamTotalContentLength(rangeResponse);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: Range probe failed for {Url}", url);
        }

        return null;
    }

    /// <summary>
    /// Returns true when the client requested a byte range but upstream did not honor it
    /// (returns 200, missing Content-Range, or 206 with a mismatched start offset).
    /// TorBox/CDN and some debrid hosts may advertise range support but still send data from byte 0.
    /// </summary>
    private static bool RequiresClientSideRangeWorkaround(
        HttpResponseMessage response,
        long? requestedRangeStart,
        string? rangeHeader)
    {
        if (string.IsNullOrEmpty(rangeHeader) || !requestedRangeStart.HasValue)
            return false;

        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            return true;

        var contentRange = GetContentRangeHeader(response);
        if (string.IsNullOrEmpty(contentRange))
            return true;

        var (upstreamStart, _) = ParseContentRange(contentRange);
        if (!upstreamStart.HasValue)
            return true;

        return upstreamStart.Value != requestedRangeStart.Value;
    }

    private static string? GetContentRangeHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Content-Range", out var responseContentRange))
            return responseContentRange.FirstOrDefault();

        if (response.Content.Headers.TryGetValues("Content-Range", out var contentContentRange))
            return contentContentRange.FirstOrDefault();

        return null;
    }

    /// <summary>
    /// Parses a Content-Range header (e.g. "bytes 0-1023/5000") into start offset and total size.
    /// </summary>
    private static (long? start, long? total) ParseContentRange(string contentRange)
    {
        var (start, end, total) = ParseContentRangeDetails(contentRange);
        return (start, total);
    }

    private static (long? start, long? end, long? total) ParseContentRangeDetails(string contentRange)
    {
        if (string.IsNullOrWhiteSpace(contentRange))
            return (null, null, null);

        if (!contentRange.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
            return (null, null, null);

        var slashIndex = contentRange.IndexOf('/');
        if (slashIndex < 0)
            return (null, null, null);

        var rangePart = contentRange.Substring(6, slashIndex - 6).Trim();
        var totalPart = contentRange.Substring(slashIndex + 1).Trim();

        long? total = null;
        if (!totalPart.Equals("*", StringComparison.Ordinal) && long.TryParse(totalPart, out var parsedTotal))
            total = parsedTotal;

        var dashIndex = rangePart.IndexOf('-');
        if (dashIndex < 0)
            return (null, null, total);

        long? start = null;
        if (!string.IsNullOrWhiteSpace(rangePart[..dashIndex]) &&
            long.TryParse(rangePart[..dashIndex], out var parsedStart))
        {
            start = parsedStart;
        }

        long? end = null;
        if (!string.IsNullOrWhiteSpace(rangePart[(dashIndex + 1)..]) &&
            long.TryParse(rangePart[(dashIndex + 1)..], out var parsedEnd))
        {
            end = parsedEnd;
        }

        return (start, end, total);
    }

    private readonly struct RangeInfo
    {
        public long? Start { get; init; }
        public long? End { get; init; }
        public long? SuffixLength { get; init; }
        public bool IsSuffixOnly => !Start.HasValue && SuffixLength.HasValue;
    }

    /// <summary>
    /// Parses the Range header into start/end/suffix components.
    /// Supports "bytes=123-", "bytes=123-456", and "bytes=-456" (suffix / cue reads).
    /// </summary>
    private static RangeInfo ParseRangeInfo(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return default;

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return default;

        var rangeValue = rangeHeader.Substring(6).Trim();
        var parts = rangeValue.Split('-');
        if (parts.Length != 2)
            return default;

        long? start = null;
        long? end = null;
        long? suffix = null;

        if (!string.IsNullOrWhiteSpace(parts[0]) && long.TryParse(parts[0], out var parsedStart))
            start = parsedStart;

        if (!string.IsNullOrWhiteSpace(parts[1]) && long.TryParse(parts[1], out var parsedEnd))
            end = parsedEnd;
        else if (string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]) &&
                 long.TryParse(parts[1], out var parsedSuffix))
            suffix = parsedSuffix;

        return new RangeInfo
        {
            Start = start,
            End = end,
            SuffixLength = suffix,
        };
    }

    /// <summary>
    /// Converts suffix ranges (bytes=-N) to absolute byte ranges once total file size is known.
    /// FFmpeg uses suffix ranges to read MKV Cues/index from the end of the file.
    /// </summary>
    private static string? NormalizeRangeHeader(string? rangeHeader, long totalLength)
    {
        var info = ParseRangeInfo(rangeHeader);
        if (!info.IsSuffixOnly || !info.SuffixLength.HasValue || totalLength <= 0)
            return rangeHeader;

        var suffix = info.SuffixLength.Value;
        var start = Math.Max(0, totalLength - suffix);
        var end = totalLength - 1;
        return $"bytes={start}-{end}";
    }
}

/// <summary>DTO for user playback settings (POST body).</summary>
public class UserSettingsDto
{
    public bool? PreferHdrOverDolbyVision { get; set; }
}
