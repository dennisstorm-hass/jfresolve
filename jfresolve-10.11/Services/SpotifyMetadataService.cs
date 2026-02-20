using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Music;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

/// <summary>
/// Fetches music metadata from Spotify public web endpoints.
/// Uses browser-like headers; no API key. Base URL is configurable for proxy/token injection.
/// </summary>
public class SpotifyMetadataService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyMetadataService> _log;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SpotifyMetadataService(
        IHttpClientFactory httpClientFactory,
        ILogger<SpotifyMetadataService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    /// <summary>
    /// Search for tracks. Returns up to limit results.
    /// </summary>
    public async Task<List<SpotifyTrackMetadata>> SearchTracksAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.SpotifyMetadataEndpointBase))
        {
            _log.LogWarning("Jfresolve Music: Spotify endpoint not configured");
            return new List<SpotifyTrackMetadata>();
        }

        var baseUrl = config.SpotifyMetadataEndpointBase.TrimEnd('/');
        // Standard Spotify Web API search: /v1/search?type=track&q=...&limit=...
        var encoded = Uri.EscapeDataString(query);
        var url = $"{baseUrl}/search?type=track&q={encoded}&limit={Math.Clamp(limit, 1, 50)}";

        for (int attempt = 0; attempt <= Constants.MaxRetryAttempts; attempt++)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(Constants.SpotifyRequestTimeoutSeconds);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyBrowserHeaders(request);

                var response = await client.SendAsync(request, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var backoff = Constants.SpotifyRateLimitBackoffBase.Add(TimeSpan.FromSeconds(attempt * 5));
                    _log.LogWarning("Jfresolve Music: Spotify rate limit (429), backoff {Backoff}s", backoff.TotalSeconds);
                    await Task.Delay(backoff, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("Jfresolve Music: Spotify search returned {StatusCode}", response.StatusCode);
                    return new List<SpotifyTrackMetadata>();
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<SpotifySearchResponse>(json, JsonOptions);
                var items = result?.Tracks?.Items ?? new List<SpotifyTrackMetadata>();
                _log.LogInformation("Jfresolve Music: Spotify search '{Query}' returned {Count} tracks", query, items.Count);
                return items;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve Music: Spotify search attempt {Attempt} failed", attempt + 1);
                if (attempt == Constants.MaxRetryAttempts)
                    return new List<SpotifyTrackMetadata>();
                await Task.Delay(Constants.RetryDelays[Math.Min(attempt, Constants.RetryDelays.Length - 1)], ct);
            }
        }

        return new List<SpotifyTrackMetadata>();
    }

    /// <summary>
    /// Get a single track by Spotify ID (e.g. from track URI).
    /// </summary>
    public async Task<SpotifyTrackMetadata?> GetTrackAsync(string spotifyTrackId, CancellationToken ct = default)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.SpotifyMetadataEndpointBase) || string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return null;
        }

        var baseUrl = config.SpotifyMetadataEndpointBase.TrimEnd('/');
        var url = $"{baseUrl}/tracks/{Uri.EscapeDataString(spotifyTrackId)}";

        for (int attempt = 0; attempt <= Constants.MaxRetryAttempts; attempt++)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(Constants.SpotifyRequestTimeoutSeconds);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyBrowserHeaders(request);

                var response = await client.SendAsync(request, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var backoff = Constants.SpotifyRateLimitBackoffBase.Add(TimeSpan.FromSeconds(attempt * 5));
                    await Task.Delay(backoff, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("Jfresolve Music: Spotify track {Id} returned {StatusCode}", spotifyTrackId, response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<SpotifyTrackMetadata>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve Music: Get track {Id} attempt {Attempt} failed", spotifyTrackId, attempt + 1);
                if (attempt == Constants.MaxRetryAttempts) return null;
                await Task.Delay(Constants.RetryDelays[Math.Min(attempt, Constants.RetryDelays.Length - 1)], ct);
            }
        }

        return null;
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", Constants.SpotifyWebUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }
}
