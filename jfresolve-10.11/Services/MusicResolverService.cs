using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Music;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

/// <summary>
/// Resolves music items: finds FLAC URL, downloads to configured folder, embeds metadata, triggers library scan.
/// </summary>
public class MusicResolverService
{
    private readonly IFlacSearchService _flacSearch;
    private readonly FlacTaggingService _flacTagging;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MusicResolverService> _log;
    private readonly SemaphoreSlim _downloadSemaphore;

    public MusicResolverService(
        IFlacSearchService flacSearch,
        FlacTaggingService flacTagging,
        ILibraryManager libraryManager,
        ILogger<MusicResolverService> log)
    {
        _flacSearch = flacSearch;
        _flacTagging = flacTagging;
        _libraryManager = libraryManager;
        _log = log;
        var config = JfresolvePlugin.Instance?.Configuration;
        var maxConcurrent = config != null
            ? Math.Clamp(config.MaxConcurrentMusicDownloads, Constants.MaxConcurrentMusicDownloadsMin, Constants.MaxConcurrentMusicDownloadsMax)
            : 3;
        _downloadSemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>
    /// Downloads FLAC for the given Spotify track, tags it, saves to MusicDownloadFolder, and triggers library scan.
    /// Returns the path of the saved file, or null if download/lookup failed.
    /// </summary>
    public async Task<string?> DownloadAndSaveAsync(SpotifyTrackMetadata metadata, CancellationToken ct = default)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || !config.EnableMusicMode || string.IsNullOrWhiteSpace(config.MusicDownloadFolder))
        {
            _log.LogWarning("Jfresolve Music: Music mode or download folder not configured");
            return null;
        }

        var folderPath = config.MusicDownloadFolder.Trim();
        var safePath = GetSafeFilePath(folderPath, metadata);
        if (System.IO.File.Exists(safePath))
        {
            _log.LogInformation("Jfresolve Music: File already exists {Path}", safePath);
            return safePath;
        }

        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            var flacUrl = await _flacSearch.FindFlacUrlAsync(metadata, ct);
            if (string.IsNullOrWhiteSpace(flacUrl))
            {
                _log.LogWarning("Jfresolve Music: No FLAC URL found for {Artist} - {Track}", metadata.GetArtistName(), metadata.Name);
                return null;
            }

            var downloaded = await DownloadToFileAsync(flacUrl, safePath, ct);
            if (!downloaded)
            {
                if (System.IO.File.Exists(safePath))
                    try { System.IO.File.Delete(safePath); } catch { /* ignore */ }
                return null;
            }

            await _flacTagging.WriteMetadataAsync(safePath, metadata, fetchCoverArt: true, ct);

            try
            {
                _libraryManager.QueueLibraryScan();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve Music: Failed to queue library scan");
            }

            return safePath;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// Tries to find a library item that matches the given file path (e.g. after a scan).
    /// Polls briefly in case scan is in progress.
    /// </summary>
    public BaseItem? FindItemByPath(string filePath, int maxAttempts = 8, int delayMs = 500)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var item = _libraryManager.FindByPath(filePath, false);
            if (item != null) return item;
            if (i < maxAttempts - 1)
                Thread.Sleep(delayMs);
        }
        return null;
    }

    private static string GetSafeFilePath(string folderPath, SpotifyTrackMetadata metadata)
    {
        var artist = SanitizeFileName(metadata.GetArtistName());
        var album = SanitizeFileName(metadata.GetAlbumName());
        var title = SanitizeFileName(metadata.Name ?? "Unknown");
        var trackNum = metadata.TrackNumber > 0 ? metadata.TrackNumber.ToString("D2") : "00";
        var name = $"{artist} - {album} - {trackNum} - {title}.flac";
        return Path.Combine(folderPath, name);
    }

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(s.Trim().Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }

    private async Task<bool> DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(Constants.MusicDownloadTimeoutSeconds);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Constants.UserAgent);

        for (int attempt = 0; attempt <= Constants.MaxRetryAttempts; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                await stream.CopyToAsync(fileStream, ct);
                _log.LogInformation("Jfresolve Music: Downloaded to {Path}", destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve Music: Download attempt {Attempt} failed for {Url}", attempt + 1, url);
                if (attempt == Constants.MaxRetryAttempts) return false;
                await Task.Delay(Constants.RetryDelays[Math.Min(attempt, Constants.RetryDelays.Length - 1)], ct);
            }
        }

        return false;
    }
}
