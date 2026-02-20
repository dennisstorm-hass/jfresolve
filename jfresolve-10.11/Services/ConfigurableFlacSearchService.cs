using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Music;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

/// <summary>
/// FLAC search implementation using an optional configurable HTTP endpoint.
/// Expects endpoint to accept query params (artist, track, album) and return JSON with "url" or "flacUrl".
/// </summary>
public class ConfigurableFlacSearchService : IFlacSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConfigurableFlacSearchService> _log;

    public ConfigurableFlacSearchService(
        IHttpClientFactory httpClientFactory,
        ILogger<ConfigurableFlacSearchService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<string?> FindFlacUrlAsync(SpotifyTrackMetadata metadata, CancellationToken ct = default)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.FlacSearchEndpointUrl))
        {
            _log.LogDebug("Jfresolve Music: FlacSearchEndpointUrl not configured, skipping FLAC lookup");
            return null;
        }

        var baseUrl = config.FlacSearchEndpointUrl.Trim();
        var artist = Uri.EscapeDataString(metadata.GetArtistName());
        var track = Uri.EscapeDataString(metadata.Name ?? string.Empty);
        var album = Uri.EscapeDataString(metadata.GetAlbumName());
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{separator}artist={artist}&track={track}&album={album}";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Jfresolve Music: FLAC endpoint returned {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("flacUrl", out var flacUrlEl))
            {
                var u = flacUrlEl.GetString();
                if (!string.IsNullOrWhiteSpace(u)) return u;
            }
            if (root.TryGetProperty("url", out var urlEl))
            {
                var u = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(u)) return u;
            }
            if (root.TryGetProperty("link", out var linkEl))
            {
                var u = linkEl.GetString();
                if (!string.IsNullOrWhiteSpace(u)) return u;
            }

            _log.LogDebug("Jfresolve Music: FLAC endpoint response had no url/flacUrl/link");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Jfresolve Music: FLAC lookup failed for {Artist} - {Track}", metadata.GetArtistName(), metadata.Name);
            return null;
        }
    }
}
