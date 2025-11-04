using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jfresolve.Configuration;
using Jfresolve.Provider;
using Jfresolve.Utilities;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Provider
{
    /// <summary>
    /// Generates STRM files for library items on-demand.
    /// STRM files are simple text files containing a URL that Jellyfin can stream.
    /// </summary>
    public class StrmFileGenerator
    {
        private readonly ILogger<StrmFileGenerator> _logger;

        public StrmFileGenerator(ILogger<StrmFileGenerator> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Creates STRM file(s) for a movie from external metadata.
        /// Returns the folder path where the STRM was created.
        /// </summary>
        public async Task<string?> CreateMovieStrmAsync(ExternalMeta meta)
        {
            if (meta.Type != "Movie")
            {
                _logger.LogWarning("[StrmGenerator] Item {Name} is not a movie", meta.Name);
                return null;
            }

            if (string.IsNullOrWhiteSpace(meta.ImdbId))
            {
                _logger.LogWarning("[StrmGenerator] Movie {Name} has no IMDB ID, cannot create STRM", meta.Name);
                return null;
            }

            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.MoviesLibraryPath))
            {
                _logger.LogWarning("[StrmGenerator] Movies library path not configured");
                return null;
            }

            try
            {
                var year = meta.Year;
                var folderName = FileNameUtility.BuildMovieFolderName(meta.Name, year);

                // Determine which library path to use based on anime classification
                string libraryPath = config.MoviesLibraryPath;
                if (meta.IsAnime && !string.IsNullOrWhiteSpace(config.AnimeLibraryPath))
                {
                    libraryPath = config.AnimeLibraryPath;
                    _logger.LogInformation("[StrmGenerator] Using anime library path for movie: {Title}", meta.Name);
                }

                var movieFolder = Path.Combine(libraryPath, folderName);
                var strmFileName = FileNameUtility.BuildMovieStrmFileName(meta.Name, year);
                var strmPath = Path.Combine(movieFolder, strmFileName);

                // Skip if already exists
                if (File.Exists(strmPath))
                {
                    _logger.LogInformation("[StrmGenerator] Movie STRM already exists: {Path}", strmPath);
                    return movieFolder;
                }

                Directory.CreateDirectory(movieFolder);

                var strmContent = UrlBuilder.BuildMovieResolverUrl(config.JellyfinBaseUrl, meta.ImdbId!);

                await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);

                // Create metadata sidecar with GUID and provider IDs for library matching
                await CreateMetadataSidecarAsync(strmPath, meta, "Movie").ConfigureAwait(false);

                _logger.LogInformation("[StrmGenerator] Created movie STRM: {Title} ({Year})", meta.Name, year);

                return movieFolder;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StrmGenerator] Error creating STRM for movie '{Title}'", meta.Name);
                return null;
            }
        }

        /// <summary>
        /// Creates STRM file(s) for a TV series from external metadata.
        /// Fetches full season/episode details from TMDB and creates STRM files for all episodes.
        /// </summary>
        public async Task<string?> CreateSeriesStrmAsync(ExternalMeta meta)
        {
            if (meta.Type != "Series")
            {
                _logger.LogWarning("[StrmGenerator] Item {Name} is not a series", meta.Name);
                return null;
            }

            if (string.IsNullOrWhiteSpace(meta.ImdbId))
            {
                _logger.LogWarning("[StrmGenerator] Series {Name} has no IMDB ID, cannot create STRM", meta.Name);
                return null;
            }

            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.ShowsLibraryPath))
            {
                _logger.LogWarning("[StrmGenerator] Shows library path not configured");
                return null;
            }

            try
            {
                var year = meta.Year;
                var folderName = FileNameUtility.BuildSeriesFolderName(meta.Name, year);

                // Determine which library path to use based on anime classification
                string libraryPath = config.ShowsLibraryPath;
                if (meta.IsAnime && !string.IsNullOrWhiteSpace(config.AnimeLibraryPath))
                {
                    libraryPath = config.AnimeLibraryPath;
                    _logger.LogInformation("[StrmGenerator] Using anime library path for series: {Title}", meta.Name);
                }

                var showFolder = Path.Combine(libraryPath, folderName);

                // Fetch full series details from TMDB to get episode information
                var seriesDetails = await FetchSeriesDetailsAsync(meta.TmdbId, config);
                if (seriesDetails == null)
                {
                    _logger.LogWarning("[StrmGenerator] Failed to fetch series details for {Title}", meta.Name);
                    Directory.CreateDirectory(showFolder);
                    return showFolder;
                }

                // Create STRM files for all episodes
                int episodeCount = await CreateEpisodeStrmFilesAsync(
                    seriesDetails.Value,
                    meta.Name,
                    year,
                    meta.ImdbId,
                    showFolder,
                    config);

                _logger.LogInformation("[StrmGenerator] Created series STRM files: {Title} ({Episodes} episodes)", meta.Name, episodeCount);

                return showFolder;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StrmGenerator] Error creating STRM for series '{Title}'", meta.Name);
                return null;
            }
        }

        /// <summary>
        /// Updates an existing series with new episodes if they exist on TMDB.
        /// Only creates STRM files for episodes that don't already exist.
        /// Returns the folder path if updates were made, null otherwise.
        /// </summary>
        public async Task<string?> UpdateSeriesStrmAsync(ExternalMeta meta)
        {
            if (meta.Type != "Series")
            {
                _logger.LogWarning("[StrmGenerator] Item {Name} is not a series", meta.Name);
                return null;
            }

            if (string.IsNullOrWhiteSpace(meta.ImdbId))
            {
                _logger.LogWarning("[StrmGenerator] Series {Name} has no IMDB ID, cannot update STRM", meta.Name);
                return null;
            }

            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.ShowsLibraryPath))
            {
                _logger.LogWarning("[StrmGenerator] Shows library path not configured");
                return null;
            }

            try
            {
                var year = meta.Year;
                var folderName = FileNameUtility.BuildSeriesFolderName(meta.Name, year);

                // Determine which library path to use based on anime classification
                string libraryPath = config.ShowsLibraryPath;
                if (meta.IsAnime && !string.IsNullOrWhiteSpace(config.AnimeLibraryPath))
                {
                    libraryPath = config.AnimeLibraryPath;
                    _logger.LogInformation("[StrmGenerator] Using anime library path for series: {Title}", meta.Name);
                }

                var showFolder = Path.Combine(libraryPath, folderName);

                // Check if folder exists - if not, series hasn't been created yet
                if (!Directory.Exists(showFolder))
                {
                    _logger.LogDebug("[StrmGenerator] Series folder does not exist for {Title}, skipping update", meta.Name);
                    return null;
                }

                // Fetch latest series details from TMDB
                var seriesDetails = await FetchSeriesDetailsAsync(meta.TmdbId, config);
                if (seriesDetails == null)
                {
                    _logger.LogWarning("[StrmGenerator] Failed to fetch series details for {Title}", meta.Name);
                    return null;
                }

                // Create STRM files for all episodes (only new ones will be created due to existence check)
                int newEpisodeCount = await CreateEpisodeStrmFilesAsync(
                    seriesDetails.Value,
                    meta.Name,
                    year,
                    meta.ImdbId,
                    showFolder,
                    config);

                if (newEpisodeCount > 0)
                {
                    _logger.LogInformation("[StrmGenerator] Updated series with new episodes: {Title} ({NewEpisodes} new)", meta.Name, newEpisodeCount);
                    return showFolder;
                }
                else
                {
                    _logger.LogDebug("[StrmGenerator] No new episodes for series: {Title}", meta.Name);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StrmGenerator] Error updating STRM for series '{Title}'", meta.Name);
                return null;
            }
        }

        /// <summary>
        /// Fetches full series details from TMDB including season and episode information.
        /// </summary>
        private async Task<JsonElement?> FetchSeriesDetailsAsync(int tmdbId, PluginConfiguration config)
        {
            try
            {
                var url = $"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={config.TmdbApiKey}";

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var response = await client.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmGenerator] Error fetching series details for TMDB {Id}", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Creates STRM files for all episodes in a series.
        /// </summary>
        private async Task<int> CreateEpisodeStrmFilesAsync(
            JsonElement seriesDetails,
            string title,
            int? year,
            string imdbId,
            string showFolder,
            PluginConfiguration config)
        {
            int episodeCount = 0;

            if (!seriesDetails.TryGetProperty("seasons", out var seasonsEl))
            {
                return 0;
            }

            foreach (var season in seasonsEl.EnumerateArray())
            {
                if (!season.TryGetProperty("season_number", out var seasonNumEl) ||
                    !season.TryGetProperty("episode_count", out var episodeCountEl))
                {
                    continue;
                }

                var seasonNum = seasonNumEl.GetInt32();
                var episodesInSeason = episodeCountEl.GetInt32();

                // Skip season 0 (specials) if configured
                if (seasonNum == 0 && !config.IncludeSpecials)
                {
                    _logger.LogDebug("[StrmGenerator] Skipping Season 0 for {Title}", title);
                    continue;
                }

                var seasonFolder = UrlBuilder.BuildSeasonPath(showFolder, seasonNum);
                Directory.CreateDirectory(seasonFolder);

                for (int ep = 1; ep <= episodesInSeason; ep++)
                {
                    try
                    {
                        var strmFileName = FileNameUtility.BuildEpisodeStrmFileName(title, year, seasonNum, ep);
                        var strmPath = Path.Combine(seasonFolder, strmFileName);

                        // Skip if already exists
                        if (File.Exists(strmPath))
                        {
                            _logger.LogDebug("[StrmGenerator] Episode STRM already exists: {Path}", strmPath);
                            continue;
                        }

                        // Create resolver URL for this episode
                        var strmContent = UrlBuilder.BuildSeriesResolverUrl(config.JellyfinBaseUrl, imdbId, seasonNum, ep);

                        await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);

                        // Create NFO metadata file for the episode
                        // This helps Jellyfin identify the episode and link it to the series
                        await CreateEpisodeNfoAsync(strmPath, title, seasonNum, ep);

                        episodeCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[StrmGenerator] Error creating STRM for episode S{Season}E{Episode}", seasonNum, ep);
                    }
                }
            }

            return episodeCount;
        }

        /// <summary>
        /// Creates a metadata sidecar file with GUID and provider IDs.
        /// This helps Jellyfin match the STRM file with the cached item when scanning.
        /// </summary>
        private async Task CreateMetadataSidecarAsync(string strmPath, ExternalMeta meta, string itemType)
        {
            try
            {
                var uri = new JfresolveUri(itemType, meta.Id);
                var guid = uri.ToGuid();
                var metadataPath = Path.ChangeExtension(strmPath, ".jfresolve.json");

                var metadata = new
                {
                    version = "1.0",
                    type = itemType,
                    uri = uri.ToString(),
                    guid = guid.ToString(),
                    tmdbId = meta.TmdbId,
                    imdbId = meta.ImdbId,
                    name = meta.Name,
                    year = meta.Year,
                    createdAt = DateTime.UtcNow.ToString("O")
                };

                var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(metadataPath, json).ConfigureAwait(false);

                _logger.LogDebug("[StrmGenerator] Created metadata sidecar: {Path}", metadataPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StrmGenerator] Error creating metadata sidecar for {Name}", meta.Name);
            }
        }

        /// <summary>
        /// Creates an NFO (metadata) file for an episode.
        /// NFO files help Jellyfin identify episodes and properly link them to their parent series.
        /// </summary>
        private async Task CreateEpisodeNfoAsync(string strmPath, string seriesTitle, int seasonNumber, int episodeNumber)
        {
            try
            {
                var nfoPath = Path.ChangeExtension(strmPath, ".nfo");

                // Skip if already exists
                if (File.Exists(nfoPath))
                {
                    _logger.LogDebug("[StrmGenerator] Episode NFO already exists: {Path}", nfoPath);
                    return;
                }

                // Create a minimal NFO file that Jellyfin can parse
                // NFO format is XML-based and Jellyfin uses it to identify episodes
                var nfoContent = $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<episodedetails>
    <title>{EscapeXml(seriesTitle)} S{seasonNumber:D2}E{episodeNumber:D2}</title>
    <showtitle>{EscapeXml(seriesTitle)}</showtitle>
    <season>{seasonNumber}</season>
    <episode>{episodeNumber}</episode>
</episodedetails>";

                await File.WriteAllTextAsync(nfoPath, nfoContent).ConfigureAwait(false);
                _logger.LogDebug("[StrmGenerator] Created episode NFO: {Path}", nfoPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StrmGenerator] Error creating NFO for episode S{Season}E{Episode}", seasonNumber, episodeNumber);
            }
        }

        /// <summary>
        /// Escapes special XML characters in a string.
        /// </summary>
        private static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

    }
}
