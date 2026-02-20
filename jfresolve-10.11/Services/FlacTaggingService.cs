using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Music;
using Microsoft.Extensions.Logging;
using TagLib;
using ByteVector = TagLib.ByteVector;
using File = TagLib.File;
using IPicture = TagLib.IPicture;

namespace Jfresolve.Services;

/// <summary>
/// Embeds Spotify metadata and cover art into FLAC files using TagLibSharp.
/// </summary>
public class FlacTaggingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FlacTaggingService> _log;

    public FlacTaggingService(
        IHttpClientFactory httpClientFactory,
        ILogger<FlacTaggingService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    /// <summary>
    /// Writes metadata and optional cover art to an existing FLAC file.
    /// </summary>
    public async Task WriteMetadataAsync(
        string flacPath,
        SpotifyTrackMetadata metadata,
        bool fetchCoverArt = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flacPath) || !System.IO.File.Exists(flacPath))
        {
            _log.LogWarning("Jfresolve Music: Cannot tag missing file {Path}", flacPath);
            return;
        }

        try
        {
            using var tagFile = File.Create(flacPath);
            var tag = tagFile.Tag;

            tag.Title = metadata.Name ?? string.Empty;
            tag.Album = metadata.GetAlbumName();
            tag.AlbumArtists = new[] { metadata.GetArtistName() };
            tag.Performers = new[] { metadata.GetAllArtistNames() };
            tag.Track = (uint)Math.Max(1, metadata.TrackNumber);
            tag.Disc = (uint)Math.Max(1, metadata.DiscNumber);
            if (uint.TryParse(metadata.GetReleaseYear(), out var year) && year > 0)
                tag.Year = year;

            // Cover art
            if (fetchCoverArt && metadata.GetCoverImageUrl() is { } coverUrl)
            {
                try
                {
                    var coverData = await DownloadCoverAsync(coverUrl, ct);
                    if (coverData != null && coverData.Length > 0)
                    {
                        var picture = new Picture
                        {
                            Data = new ByteVector(coverData),
                            Type = PictureType.FrontCover,
                            MimeType = "image/jpeg",
                            Description = "Cover"
                        };
                        tag.Pictures = new IPicture[] { picture };
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Jfresolve Music: Failed to embed cover art for {Path}", flacPath);
                }
            }

            tagFile.Save();
            _log.LogInformation("Jfresolve Music: Tagged FLAC {Path}", flacPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve Music: Failed to write tags to {Path}", flacPath);
            throw;
        }
    }

    private async Task<byte[]?> DownloadCoverAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Jfresolve Music: Could not download cover from {Url}", url);
            return null;
        }
    }
}
