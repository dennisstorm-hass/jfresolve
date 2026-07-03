using System;
using System.Collections.Generic;
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
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, EnsureRedirectTrue(streamUrl));

        if (!TryParseTorrentioTorBoxUrl(streamUrl, out var infoHash, out var fileIndex))
            return null;

        var torrentRef = await TryResolveTorrentRefFromMyListAsync(torBoxApiKey, infoHash, fileIndex, cancellationToken);
        if (torrentRef.HasValue)
        {
            var playback = await TryCreateStreamPlaybackAsync(
                torBoxApiKey, torrentRef.Value.TorrentId, torrentRef.Value.FileId, cancellationToken);
            if (playback.HasValue)
            {
                _logger.LogInformation(
                    "Jfresolve: Using TorBox createstream {Kind} for hash {Hash} torrent {TorrentId} file {FileId}",
                    playback.Value.Kind == TorBoxDeliveryKind.Hls ? "HLS" : "CDN",
                    infoHash, torrentRef.Value.TorrentId, torrentRef.Value.FileId);
                return playback.Value;
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

        if (IsTorBoxStreamCdnUrl(url))
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

    private async Task<TorBoxStreamTarget?> TryCreateStreamPlaybackAsync(
        string apiKey,
        string torrentId,
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            const int maxCreateAttempts = 4;
            for (var attempt = 1; attempt <= maxCreateAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var discovery = await CallCreateStreamAsync(
                    apiKey, torrentId, fileId, chosenAudioIndex: 0, chosenSubtitleIndex: null, cancellationToken);
                if (discovery == null)
                {
                    if (attempt < maxCreateAttempts)
                    {
                        _logger.LogInformation(
                            "Jfresolve: TorBox createstream attempt {Attempt}/{Max} failed for torrent {TorrentId}, retrying in 3s",
                            attempt, maxCreateAttempts, torrentId);
                        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                        continue;
                    }

                    return null;
                }

                var streamResult = discovery.Value;
                var audioIndex = streamResult.AudioIndex;
                var playback = streamResult.Playback;
                var presignedToken = streamResult.PresignedToken;

                if (!playback.HasValue || streamResult.NeedsTranscoding)
                {
                    var finalized = await CallCreateStreamAsync(
                        apiKey, torrentId, fileId, audioIndex, chosenSubtitleIndex: null, cancellationToken);
                    if (finalized != null)
                    {
                        streamResult = finalized.Value;
                        playback = streamResult.Playback ?? playback;
                        presignedToken = streamResult.PresignedToken ?? presignedToken;
                        audioIndex = streamResult.AudioIndex;
                    }
                }

                if (playback.HasValue)
                {
                    _logger.LogInformation(
                        "Jfresolve: TorBox createstream returned {Kind} URL for torrent {TorrentId} (attempt {Attempt})",
                        playback.Value.Kind == TorBoxDeliveryKind.Hls ? "HLS" : "CDN",
                        torrentId, attempt);
                    return playback.Value;
                }

                if (!string.IsNullOrWhiteSpace(presignedToken))
                {
                    playback = await PollGetStreamDataPlaybackAsync(apiKey, presignedToken, audioIndex, streamResult, cancellationToken);
                    if (playback.HasValue)
                    {
                        _logger.LogInformation(
                            "Jfresolve: TorBox getstreamdata returned {Kind} URL for torrent {TorrentId} (attempt {Attempt})",
                            playback.Value.Kind == TorBoxDeliveryKind.Hls ? "HLS" : "CDN",
                            torrentId, attempt);
                        return playback.Value;
                    }
                }

                if (attempt < maxCreateAttempts)
                {
                    _logger.LogInformation(
                        "Jfresolve: TorBox stream not ready for torrent {TorrentId}, retrying createstream in 3s ({Attempt}/{Max})",
                        torrentId, attempt, maxCreateAttempts);
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
        const int maxPolls = 20;
        var pollDelay = initial.NeedsTranscoding || initial.IsTranscoding
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(1);

        for (var poll = 0; poll < maxPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var playback = await FetchGetStreamDataPlaybackAsync(apiKey, presignedToken, audioIndex, cancellationToken);
            if (playback.HasValue)
                return playback;

            if (!initial.NeedsTranscoding && !initial.IsTranscoding)
                break;

            _logger.LogDebug(
                "Jfresolve: TorBox stream transcoding in progress for token {TokenPrefix}..., poll {Poll}/{Max}",
                presignedToken.Length > 8 ? presignedToken[..8] : presignedToken,
                poll + 1,
                maxPolls);

            await Task.Delay(pollDelay, cancellationToken);
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
        foreach (var url in CollectStreamUrls(data))
        {
            if (IsTorBoxStreamCdnUrl(url))
                return new TorBoxStreamTarget(TorBoxDeliveryKind.Direct, url);
        }

        var hlsUrl = GetJsonString(data, "hls_url");
        if (!string.IsNullOrWhiteSpace(hlsUrl))
            return new TorBoxStreamTarget(TorBoxDeliveryKind.Hls, hlsUrl);

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
