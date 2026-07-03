using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
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
/// Prefers createstream CDN (/dld/ on tb-cdn.io) for seekable playback, then HLS, then requestdl.
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

    public static bool IsTorBoxStreamCdnUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        !IsHlsUrl(url) &&
        (url.Contains("/dld/", StringComparison.OrdinalIgnoreCase)
         || url.Contains("tb-cdn.io", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves Torrentio TorBox links to createstream CDN/HLS (preferred) or requestdl permalink.
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

        if (IsTorBoxStreamCdnUrl(streamUrl))
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, streamUrl);

        if (IsTorBoxRequestDlPermalink(streamUrl))
        {
            TorrentRef? requestDlRef = null;
            if (TryParseRequestDlUrl(streamUrl, out var requestDlTorrentId, out var requestDlFileId))
                requestDlRef = new TorrentRef(requestDlTorrentId, requestDlFileId);

            return await ResolveTorBoxPlaybackAsync(
                torBoxApiKey, requestDlRef, infoHash: null, fileIndex: null, streamUrl, cancellationToken);
        }

        if (!TryParseTorrentioTorBoxUrl(streamUrl, out var infoHash, out var fileIndex))
            return null;

        var torrentRef = await TryResolveTorrentRefFromMyListAsync(torBoxApiKey, infoHash, fileIndex, cancellationToken);
        if (!torrentRef.HasValue)
        {
            var fromRedirect = await TryDiscoverRequestDlPermalinkAsync(streamUrl, cancellationToken);
            if (!string.IsNullOrEmpty(fromRedirect)
                && TryParseRequestDlUrl(fromRedirect, out var redirectTorrentId, out var redirectFileId))
            {
                torrentRef = new TorrentRef(redirectTorrentId, redirectFileId);
                _logger.LogInformation(
                    "Jfresolve: Resolved TorBox torrent {TorrentId} file {FileId} from Torrentio redirect for hash {Hash}",
                    redirectTorrentId, redirectFileId, infoHash);
            }
        }

        return await ResolveTorBoxPlaybackAsync(
            torBoxApiKey, torrentRef, infoHash, fileIndex, streamUrl, cancellationToken);
    }

    private async Task<TorBoxStreamTarget?> ResolveTorBoxPlaybackAsync(
        string torBoxApiKey,
        TorrentRef? torrentRef,
        string? infoHash,
        int? fileIndex,
        string fallbackStreamUrl,
        CancellationToken cancellationToken)
    {
        if (torrentRef.HasValue)
        {
            // /dld/ CDN from requestdl?redirect=false — single MKV file, works with Jellyfin FFmpeg (seek via -ss before -i).
            var directCdn = await TryGetDirectDownloadCdnUrlAsync(
                torBoxApiKey, torrentRef.Value.TorrentId, torrentRef.Value.FileId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(directCdn))
            {
                _logger.LogInformation(
                    "Jfresolve: Using TorBox /dld/ CDN for torrent {TorrentId} file {FileId}{HashSuffix} (host={Host})",
                    torrentRef.Value.TorrentId,
                    torrentRef.Value.FileId,
                    string.IsNullOrWhiteSpace(infoHash) ? string.Empty : $" hash {infoHash}",
                    Uri.TryCreate(directCdn, UriKind.Absolute, out var cdnUri) ? cdnUri.Host : "unknown");
                return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, directCdn);
            }

            var playback = await TryCreateStreamPlaybackAsync(
                torBoxApiKey, torrentRef.Value.TorrentId, torrentRef.Value.FileId, cancellationToken);
            if (playback.HasValue && playback.Value.Kind == TorBoxDeliveryKind.Hls)
            {
                _logger.LogInformation(
                    "Jfresolve: Using TorBox createstream HLS for torrent {TorrentId} file {FileId}{HashSuffix}",
                    torrentRef.Value.TorrentId,
                    torrentRef.Value.FileId,
                    string.IsNullOrWhiteSpace(infoHash) ? string.Empty : $" hash {infoHash}");
                return playback.Value;
            }

            var permalink = BuildRequestDlPermalink(
                torBoxApiKey, torrentRef.Value.TorrentId, torrentRef.Value.FileId);
            _logger.LogInformation(
                "Jfresolve: TorBox /dld/ and createstream unavailable for torrent {TorrentId} file {FileId}, using requestdl fallback{HashSuffix}",
                torrentRef.Value.TorrentId,
                torrentRef.Value.FileId,
                string.IsNullOrWhiteSpace(infoHash) ? string.Empty : $" (hash {infoHash})");
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, permalink);
        }

        if (IsTorBoxRequestDlPermalink(fallbackStreamUrl))
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, EnsureRedirectTrue(fallbackStreamUrl));

        _logger.LogInformation(
            "Jfresolve: Could not resolve TorBox torrent id for {Url}, using Torrentio resolve URL",
            fallbackStreamUrl);
        return null;
    }

    public static bool RequiresFreshRedirectPerRequest(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (IsHlsUrl(url))
            return false;

        if (IsTorBoxStreamCdnUrl(url))
            return false;

        return url.Contains("/resolve/torbox/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/resolve/realdebrid/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("api.torbox.app", StringComparison.OrdinalIgnoreCase)
            || url.Contains("real-debrid.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("torrentio.strem.fun/resolve/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHlsSegmentUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        !IsHlsUrl(url) &&
        url.Contains("tb-cdn.io", StringComparison.OrdinalIgnoreCase) &&
        (url.Contains(".ts", StringComparison.OrdinalIgnoreCase)
         || url.Contains(".m4s", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// TorBox flux CDN reports 2147483256 (2^31-8) as a bogus segment length when Range is used.
    /// </summary>
    public static bool IsSuspectUpstreamContentLength(long? length) =>
        length is 2147483256 or 2147483647 or > 600_000_000;

    public static bool IsTorBoxRequestDlPermalink(string url)
    {
        return url.Contains("api.torbox.app", StringComparison.OrdinalIgnoreCase)
            && url.Contains("/torrents/requestdl", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTorrentioTorBoxResolveUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("/resolve/torbox/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only cache raw addon resolve URLs. Normalized TorBox delivery URLs must be re-resolved each request.
    /// </summary>
    public static bool ShouldCacheAddonRedirectUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (IsHlsUrl(url) || IsTorBoxStreamCdnUrl(url) || IsTorBoxRequestDlPermalink(url))
            return false;

        return IsTorrentioTorBoxResolveUrl(url)
            || url.Contains("/resolve/realdebrid/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseRequestDlUrl(string url, out string torrentId, out string fileId)
    {
        torrentId = string.Empty;
        fileId = "0";

        if (!IsTorBoxRequestDlPermalink(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("torrent_id", out var torrentIdValues))
            return false;

        torrentId = torrentIdValues.ToString();
        if (string.IsNullOrWhiteSpace(torrentId))
            return false;

        if (query.TryGetValue("file_id", out var fileIdValues) && !string.IsNullOrWhiteSpace(fileIdValues.ToString()))
            fileId = fileIdValues.ToString();

        return true;
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
                _logger.LogInformation(
                    "Jfresolve: TorBox mylist returned HTTP {Status} for hash {Hash}",
                    (int)response.StatusCode, infoHash);
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
            _logger.LogInformation(ex, "Jfresolve: TorBox mylist lookup failed for hash {Hash}", infoHash);
        }

        _logger.LogInformation("Jfresolve: TorBox mylist has no entry for hash {Hash}", infoHash);
        return null;
    }

    /// <summary>
    /// TorBox createstream (verified against live API): returns data.hls_url on tb-cdn.io immediately,
    /// even when needs_transcoding is true. Optional presigned_token for getstreamdata polling.
    /// See https://api-docs.torbox.app/ — GET /v1/api/stream/createstream
    /// </summary>
    private async Task<TorBoxStreamTarget?> TryCreateStreamPlaybackAsync(
        string apiKey,
        string torrentId,
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var created = await CallCreateStreamAsync(
                    apiKey, torrentId, fileId, chosenAudioIndex: 0, chosenSubtitleIndex: null, cancellationToken);
                if (created == null)
                {
                    if (attempt < maxAttempts)
                    {
                        _logger.LogInformation(
                            "Jfresolve: TorBox createstream HTTP error for torrent {TorrentId}, retry {Attempt}/{Max} in 2s",
                            torrentId, attempt, maxAttempts);
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    }

                    continue;
                }

                var streamResult = created.Value;

                // Live API: hls_url is present on first createstream response — do not discard when needs_transcoding=true.
                if (streamResult.Playback.HasValue)
                {
                    LogPlaybackResolved("createstream", streamResult.Playback.Value, torrentId, fileId, attempt);
                    return streamResult.Playback.Value;
                }

                if (!string.IsNullOrWhiteSpace(streamResult.PresignedToken))
                {
                    var fromPoll = await PollGetStreamDataPlaybackAsync(
                        apiKey, streamResult.PresignedToken, streamResult.AudioIndex, streamResult, cancellationToken);
                    if (fromPoll.HasValue)
                    {
                        LogPlaybackResolved("getstreamdata", fromPoll.Value, torrentId, fileId, attempt);
                        return fromPoll.Value;
                    }
                }

                if (attempt < maxAttempts)
                {
                    _logger.LogInformation(
                        "Jfresolve: TorBox stream not ready for torrent {TorrentId} file {FileId}, retry {Attempt}/{Max} in 3s (needs_transcoding={NeedsTranscoding})",
                        torrentId, fileId, attempt, maxAttempts, streamResult.NeedsTranscoding);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(
                ex,
                "Jfresolve: TorBox createstream/getstreamdata failed for torrent {TorrentId} file {FileId}",
                torrentId, fileId);
        }

        return null;
    }

    private void LogPlaybackResolved(
        string step,
        TorBoxStreamTarget target,
        string torrentId,
        string fileId,
        int attempt)
    {
        var host = Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) ? uri.Host : "unknown";
        _logger.LogInformation(
            "Jfresolve: TorBox {Step} returned {Kind} playback for torrent {TorrentId} file {FileId} (host={Host}, attempt {Attempt})",
            step,
            target.Kind == TorBoxDeliveryKind.Hls ? "HLS" : "CDN",
            torrentId,
            fileId,
            host,
            attempt);
    }

    private readonly record struct CreateStreamResult(
        TorBoxStreamTarget? Playback,
        string? PresignedToken,
        bool NeedsTranscoding,
        bool IsTranscoding,
        int AudioIndex);

    private async Task<CreateStreamResult?> CallCreateStreamAsync(
        string apiKey,
        string torrentId,
        string fileId,
        int? chosenAudioIndex,
        int? chosenSubtitleIndex,
        CancellationToken cancellationToken)
    {
        var query = BuildCreateStreamQuery(torrentId, fileId, chosenAudioIndex, chosenSubtitleIndex);
        using var doc = await SendTorBoxApiGetAsync(
            $"{TorBoxStreamApi}/createstream?{query}",
            apiKey,
            cancellationToken,
            useBearerAuth: true);
        if (doc == null)
            return null;

        if (!TryGetTorBoxDataObject(doc, out var data))
        {
            LogTorBoxApiFailure("createstream", doc, torrentId, fileId);
            return null;
        }

        var presignedToken = GetJsonString(data, "presigned_token") ?? GetJsonString(data, "token");
        var needsTranscoding = data.TryGetProperty("needs_transcoding", out var nt) && nt.ValueKind == JsonValueKind.True;
        var isTranscoding = data.TryGetProperty("is_transcoding", out var it) && it.ValueKind == JsonValueKind.True;
        var audioIndex = ResolveRelativeAudioIndex(data) ?? chosenAudioIndex ?? 0;
        var playback = ExtractPlaybackTargetFromStreamData(data);

        return new CreateStreamResult(playback, presignedToken, needsTranscoding, isTranscoding, audioIndex);
    }

    private static string BuildCreateStreamQuery(
        string torrentId,
        string fileId,
        int? chosenAudioIndex,
        int? chosenSubtitleIndex)
    {
        var parts = new List<string>
        {
            $"id={Uri.EscapeDataString(torrentId)}",
            $"file_id={Uri.EscapeDataString(fileId)}",
            "type=torrent",
            "scrobbling_enabled=false",
        };

        if (chosenAudioIndex.HasValue)
            parts.Add($"chosen_audio_index={chosenAudioIndex.Value}");

        if (chosenSubtitleIndex.HasValue)
            parts.Add($"chosen_subtitle_index={chosenSubtitleIndex.Value}");

        return string.Join('&', parts);
    }

    private static int? ResolveRelativeAudioIndex(JsonElement data)
    {
        if (data.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("audios", out var audios) &&
            audios.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var audio in audios.EnumerateArray())
            {
                if (audio.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.True)
                    return index;
                index++;
            }

            if (index > 0)
                return 0;
        }

        if (data.TryGetProperty("audio_index", out var audioIndex))
        {
            return audioIndex.ValueKind == JsonValueKind.Number
                ? audioIndex.GetInt32()
                : int.TryParse(audioIndex.GetString(), out var parsed) ? parsed : null;
        }

        return 0;
    }

    private async Task<TorBoxStreamTarget?> PollGetStreamDataPlaybackAsync(
        string apiKey,
        string presignedToken,
        int audioIndex,
        CreateStreamResult initial,
        CancellationToken cancellationToken)
    {
        // Only poll when createstream returned a token but no hls_url yet.
        if (!initial.NeedsTranscoding && !initial.IsTranscoding)
        {
            return await FetchGetStreamDataPlaybackAsync(apiKey, presignedToken, audioIndex, cancellationToken);
        }

        const int maxPolls = 15;
        for (var poll = 0; poll < maxPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var playback = await FetchGetStreamDataPlaybackAsync(apiKey, presignedToken, audioIndex, cancellationToken);
            if (playback.HasValue)
                return playback;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }

    private async Task<TorBoxStreamTarget?> FetchGetStreamDataPlaybackAsync(
        string apiKey,
        string presignedToken,
        int audioIndex,
        CancellationToken cancellationToken)
    {
        var streamQuery =
            $"presigned_token={Uri.EscapeDataString(presignedToken)}" +
            $"&token={Uri.EscapeDataString(apiKey)}" +
            $"&chosen_audio_index={audioIndex}";

        using var streamDoc = await SendTorBoxApiGetAsync(
            $"{TorBoxStreamApi}/getstreamdata?{streamQuery}",
            apiKey,
            cancellationToken,
            useBearerAuth: false);
        if (streamDoc == null)
            return null;

        if (!TryGetTorBoxDataObject(streamDoc, out var streamData))
            return null;

        return ExtractPlaybackTargetFromStreamData(streamData);
    }

    private async Task<JsonDocument?> SendTorBoxApiGetAsync(
        string url,
        string apiKey,
        CancellationToken cancellationToken,
        bool useBearerAuth = true)
    {
        var client = _httpClientFactory.CreateClient("Jfresolve.TorBox");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (useBearerAuth)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = string.Empty;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                // ignore read failures
            }

            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            _logger.LogInformation(
                "Jfresolve: TorBox API {Path} returned HTTP {Status}: {Body}",
                path,
                (int)response.StatusCode,
                TruncateForLog(errorBody, 400));
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryGetTorBoxDataObject(JsonDocument doc, out JsonElement data)
    {
        data = default;
        var root = doc.RootElement;

        if (root.TryGetProperty("success", out var successProp) &&
            successProp.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (!root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
            return false;

        return true;
    }

    private void LogTorBoxApiFailure(string step, JsonDocument doc, string torrentId, string fileId)
    {
        var root = doc.RootElement;
        var detail = root.TryGetProperty("detail", out var detailProp)
            ? detailProp.GetString()
            : null;
        var error = root.TryGetProperty("error", out var errorProp)
            ? errorProp.GetString()
            : null;

        _logger.LogInformation(
            "Jfresolve: TorBox {Step} returned no playback URL for torrent {TorrentId} file {FileId} (error={Error}, detail={Detail})",
            step, torrentId, fileId, error ?? "none", detail ?? "none");
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null,
        };
    }

    private static TorBoxStreamTarget? ExtractPlaybackTargetFromStreamData(JsonElement data)
    {
        // Verified live API: createstream/getstreamdata always expose data.hls_url for seekable playback.
        var hlsUrl = GetJsonString(data, "hls_url");
        if (!string.IsNullOrWhiteSpace(hlsUrl))
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Hls, hlsUrl);

        foreach (var url in CollectStreamUrls(data))
        {
            if (url.Contains("/dld/", StringComparison.OrdinalIgnoreCase))
                return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, url);
        }

        foreach (var url in CollectStreamUrls(data))
        {
            if (IsHlsUrl(url))
                return new TorBoxStreamTarget(TorBoxDeliveryKind.Hls, url);
        }

        foreach (var url in CollectStreamUrls(data))
        {
            if (!string.IsNullOrWhiteSpace(url))
                return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, url);
        }

        return null;
    }

    private static IEnumerable<string> CollectStreamUrls(JsonElement data)
    {
        var topLevel = GetJsonString(data, "url")
            ?? GetJsonString(data, "stream_url")
            ?? GetJsonString(data, "download_url")
            ?? GetJsonString(data, "webdav_url");
        if (!string.IsNullOrWhiteSpace(topLevel))
            yield return topLevel;

        if (data.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var url in urls.EnumerateArray())
            {
                var value = url.ValueKind == JsonValueKind.String ? url.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
        }

        if (data.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var value = GetJsonString(stream, "url")
                    ?? GetJsonString(stream, "hls_url")
                    ?? GetJsonString(stream, "download_url");
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
        }
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
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

    /// <summary>
    /// GET /v1/api/torrents/requestdl?redirect=false returns a short-lived /dld/ CDN URL (verified live API).
    /// </summary>
    private async Task<string?> TryGetDirectDownloadCdnUrlAsync(
        string apiKey,
        string torrentId,
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            var query =
                $"token={Uri.EscapeDataString(apiKey)}" +
                $"&torrent_id={Uri.EscapeDataString(torrentId)}" +
                $"&file_id={Uri.EscapeDataString(fileId)}" +
                "&redirect=false";

            using var doc = await SendTorBoxApiGetAsync(
                $"{TorBoxTorrentsApi}/requestdl?{query}",
                apiKey,
                cancellationToken,
                useBearerAuth: false);
            if (doc == null)
                return null;

            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successProp) &&
                successProp.ValueKind == JsonValueKind.False)
            {
                return null;
            }

            if (!root.TryGetProperty("data", out var data))
                return null;

            var cdnUrl = data.ValueKind == JsonValueKind.String ? data.GetString() : null;
            if (string.IsNullOrWhiteSpace(cdnUrl) || !IsTorBoxStreamCdnUrl(cdnUrl))
                return null;

            return cdnUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(
                ex,
                "Jfresolve: TorBox requestdl redirect=false failed for torrent {TorrentId} file {FileId}",
                torrentId, fileId);
            return null;
        }
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
