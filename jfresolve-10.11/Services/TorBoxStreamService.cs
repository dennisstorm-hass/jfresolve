using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

public enum TorBoxDeliveryKind
{
    Direct,
    Hls,
}

public readonly record struct TorBoxStreamTarget(TorBoxDeliveryKind Kind, string Url);

/// <summary>
/// Resolves TorBox/Torrentio streams to TorBox-native delivery URLs.
/// Prefers createstream HLS (seekable) over requestdl MKV direct download.
/// </summary>
public class TorBoxStreamService
{
    private const string TorBoxApiBase = "https://api.torbox.app/v1/api";
    private const string TorBoxTorrentsApi = $"{TorBoxApiBase}/torrents";
    private const string TorBoxStreamApi = $"{TorBoxApiBase}/stream";

    private static readonly Regex TorrentioTorBoxResolveRegex = new(
        @"/resolve/torbox/[^/]+/(?<hash>[a-fA-F0-9]{40})/[^/]+/(?<fileIndex>\d+)/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TorBoxStreamService> _logger;

    public TorBoxStreamService(IHttpClientFactory httpClientFactory, ILogger<TorBoxStreamService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static bool IsHlsUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves Torrentio TorBox links to createstream HLS (preferred) or requestdl permalink.
    /// </summary>
    public async Task<TorBoxStreamTarget?> TryResolveTorBoxStreamAsync(
        string streamUrl,
        string? torBoxApiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl) || string.IsNullOrWhiteSpace(torBoxApiKey))
            return null;

        if (IsHlsUrl(streamUrl))
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Hls, streamUrl);

        if (IsTorBoxRequestDlPermalink(streamUrl))
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, EnsureRedirectTrue(streamUrl));

        if (!TryParseTorrentioTorBoxUrl(streamUrl, out var infoHash, out var fileIndex))
            return null;

        var torrentRef = await TryResolveTorrentRefFromMyListAsync(torBoxApiKey, infoHash, fileIndex, cancellationToken);
        if (torrentRef.HasValue)
        {
            var hlsUrl = await TryCreateStreamHlsUrlAsync(
                torBoxApiKey, torrentRef.Value.TorrentId, torrentRef.Value.FileId, cancellationToken);
            if (!string.IsNullOrEmpty(hlsUrl))
            {
                _logger.LogInformation(
                    "Jfresolve: Using TorBox createstream HLS for hash {Hash} torrent {TorrentId} file {FileId}",
                    infoHash, torrentRef.Value.TorrentId, torrentRef.Value.FileId);
                return new TorBoxStreamTarget(TorBoxDeliveryKind.Hls, hlsUrl);
            }

            var permalink = BuildRequestDlPermalink(torBoxApiKey, torrentRef.Value.TorrentId, torrentRef.Value.FileId);
            _logger.LogInformation(
                "Jfresolve: Using TorBox requestdl permalink for hash {Hash} file {FileIndex}",
                infoHash, fileIndex);
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, permalink);
        }

        var fromRedirect = await TryDiscoverRequestDlPermalinkAsync(streamUrl, cancellationToken);
        if (!string.IsNullOrEmpty(fromRedirect))
        {
            _logger.LogInformation(
                "Jfresolve: Discovered TorBox requestdl permalink via redirect chain for hash {Hash}",
                infoHash);
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, fromRedirect);
        }

        _logger.LogDebug(
            "Jfresolve: Could not resolve TorBox stream for {Url}, using Torrentio resolve URL",
            streamUrl);
        return null;
    }

    public static bool RequiresFreshRedirectPerRequest(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (IsHlsUrl(url))
            return false;

        return url.Contains("/resolve/torbox/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/resolve/realdebrid/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("api.torbox.app", StringComparison.OrdinalIgnoreCase)
            || url.Contains("real-debrid.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("torrentio.strem.fun/resolve/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTorBoxRequestDlPermalink(string url)
    {
        return url.Contains("api.torbox.app", StringComparison.OrdinalIgnoreCase)
            && url.Contains("/torrents/requestdl", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct TorrentRef(string TorrentId, string FileId);

    private static bool TryParseTorrentioTorBoxUrl(string url, out string infoHash, out int fileIndex)
    {
        infoHash = string.Empty;
        fileIndex = 0;

        var match = TorrentioTorBoxResolveRegex.Match(url);
        if (!match.Success)
            return false;

        infoHash = match.Groups["hash"].Value.ToLowerInvariant();
        return int.TryParse(match.Groups["fileIndex"].Value, out fileIndex);
    }

    private async Task<TorrentRef?> TryResolveTorrentRefFromMyListAsync(
        string apiKey,
        string infoHash,
        int fileIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Jfresolve.TorBox");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{TorBoxTorrentsApi}/mylist?bypass_cache=true");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jfresolve: TorBox mylist returned {Status}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var torrent in data.EnumerateArray())
            {
                if (!TryGetTorrentHash(torrent, out var hash) ||
                    !hash.Equals(infoHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!torrent.TryGetProperty("id", out var idProp))
                    continue;

                var torrentId = idProp.ValueKind == JsonValueKind.Number
                    ? idProp.GetInt64().ToString()
                    : idProp.GetString();
                if (string.IsNullOrWhiteSpace(torrentId))
                    continue;

                var fileId = ResolveFileId(torrent, fileIndex);
                return new TorrentRef(torrentId, fileId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: TorBox mylist lookup failed for hash {Hash}", infoHash);
        }

        return null;
    }

    private async Task<string?> TryCreateStreamHlsUrlAsync(
        string apiKey,
        string torrentId,
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            var query =
                $"id={Uri.EscapeDataString(torrentId)}" +
                $"&file_id={Uri.EscapeDataString(fileId)}" +
                "&type=torrent" +
                "&chosen_audio_index=0";

            var client = _httpClientFactory.CreateClient("Jfresolve.TorBox");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{TorBoxStreamApi}/createstream?{query}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Jfresolve: TorBox createstream returned {Status} for torrent {TorrentId} file {FileId}",
                    (int)response.StatusCode, torrentId, fileId);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!doc.RootElement.TryGetProperty("success", out var successProp) ||
                successProp.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            if (data.TryGetProperty("hls_url", out var hlsUrlProp))
            {
                var hlsUrl = hlsUrlProp.GetString();
                if (!string.IsNullOrWhiteSpace(hlsUrl))
                    return hlsUrl;
            }

            // Some responses nest stream URL under domain + path
            if (data.TryGetProperty("webdav_url", out var webDavProp))
            {
                var webDav = webDavProp.GetString();
                if (!string.IsNullOrWhiteSpace(webDav) && webDav.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    return webDav;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: TorBox createstream failed for torrent {TorrentId} file {FileId}", torrentId, fileId);
        }

        return null;
    }

    private static bool TryGetTorrentHash(JsonElement torrent, out string hash)
    {
        hash = string.Empty;
        if (torrent.TryGetProperty("hash", out var hashProp))
        {
            hash = hashProp.GetString() ?? string.Empty;
            if (!string.IsNullOrEmpty(hash))
                return true;
        }

        return false;
    }

    private static string ResolveFileId(JsonElement torrent, int fileIndex)
    {
        if (!torrent.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return fileIndex.ToString();

        foreach (var file in files.EnumerateArray())
        {
            if (file.TryGetProperty("id", out var idProp))
            {
                var id = idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : int.Parse(idProp.GetString() ?? "-1");
                if (id == fileIndex)
                    return id.ToString();
            }
        }

        var firstFile = files.EnumerateArray().FirstOrDefault();
        if (firstFile.ValueKind != JsonValueKind.Undefined && firstFile.TryGetProperty("id", out var firstId))
        {
            return firstId.ValueKind == JsonValueKind.Number
                ? firstId.GetInt32().ToString()
                : firstId.GetString() ?? fileIndex.ToString();
        }

        return fileIndex.ToString();
    }

    private static string BuildRequestDlPermalink(string apiKey, string torrentId, string fileId)
    {
        return $"{TorBoxTorrentsApi}/requestdl?token={Uri.EscapeDataString(apiKey)}&torrent_id={Uri.EscapeDataString(torrentId)}&file_id={Uri.EscapeDataString(fileId)}&redirect=true";
    }

    private static string EnsureRedirectTrue(string url)
    {
        if (url.Contains("redirect=true", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}redirect=true";
    }

    private async Task<string?> TryDiscoverRequestDlPermalinkAsync(string streamUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Jfresolve.Stream");
            using var initialRequest = new HttpRequestMessage(HttpMethod.Head, streamUrl);
            var response = await client.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            for (var i = 0; i < 8 && IsRedirectStatus(response.StatusCode); i++)
            {
                var location = response.Headers.Location?.ToString();
                response.Dispose();

                if (string.IsNullOrWhiteSpace(location))
                    break;

                if (!Uri.TryCreate(location, UriKind.Absolute, out var absolute))
                {
                    if (!Uri.TryCreate(new Uri(streamUrl), location, out absolute))
                        break;
                    location = absolute.ToString();
                }

                if (location.Contains("/torrents/requestdl", StringComparison.OrdinalIgnoreCase))
                    return EnsureRedirectTrue(location);

                using var nextRequest = new HttpRequestMessage(HttpMethod.Head, location);
                response = await client.SendAsync(nextRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            response.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jfresolve: Failed to discover TorBox requestdl permalink from {Url}", streamUrl);
        }

        return null;
    }

    private static bool IsRedirectStatus(System.Net.HttpStatusCode statusCode)
    {
        return statusCode == System.Net.HttpStatusCode.MovedPermanently
            || statusCode == System.Net.HttpStatusCode.Found
            || statusCode == System.Net.HttpStatusCode.SeeOther
            || statusCode == System.Net.HttpStatusCode.TemporaryRedirect
            || statusCode == System.Net.HttpStatusCode.PermanentRedirect;
    }
}
