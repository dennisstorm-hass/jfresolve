// JfresolveSearchProvider.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jfresolve.Configuration;
using Jellyfin.Plugin.Jfresolve.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jfresolve.SearchProviders
{
    /// <summary>
    /// Action filter that intercepts search requests and queries TMDB.
    /// </summary>
    public class JfResolveSearchProvider : IAsyncActionFilter
    {
        // Debounce delay in milliseconds
        private const int DebounceDelayMs = 3000;
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3";

        private const int AnimeGenreId = 16;

        private readonly ILogger<JfResolveSearchProvider> _logger;

        private static readonly object _debounceLock = new object();
        private static readonly HttpClient _httpClient = new HttpClient();

        // Debounce state - shared across all requests
        private static string? _lastSearchTerm;
        private static DateTime _lastSearchTime = DateTime.MinValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="JfResolveSearchProvider"/> class.
        /// </summary>
        /// <param name="logger">The logger instance used for diagnostic output.</param>
        public JfResolveSearchProvider(ILogger<JfResolveSearchProvider> logger)
        {
            _logger = logger;
            _logger.LogInformation("JfResolveSearchProvider initialized");
        }

        private int MaxResultsPerType
        {
            get
            {
                var configured = Plugin.Instance?.Configuration?.SearchNumber ?? 3;
                return configured > 0 ? configured : 3;
            }
        }

        /// <summary>
        /// Executes custom logic before and after the specified action executes.
        /// </summary>
        /// <param name="context">The action executing context.</param>
        /// <param name="next">The next delegate to execute in the pipeline.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                // Log every request
                _logger.LogDebug("Request: {RequestPath}", context.HttpContext.Request.Path);

                // Check if this is a search request
                var queryParams = context.HttpContext.Request.Query;

                if (queryParams.ContainsKey("searchTerm"))
                {
                    string? searchTerm = queryParams["searchTerm"].ToString();

                    // Apply debounce logic
                    bool shouldSearch = ShouldSearch(searchTerm);

                    if (!shouldSearch)
                    {
                        _logger.LogDebug("[SEARCH DEBOUNCED] Ignoring '{SearchTerm}' - waiting for user to finish typing", searchTerm);
                        await next().ConfigureAwait(false);
                        return;
                    }

                    _logger.LogInformation("[SEARCH] User searched for: '{SearchTerm}'", searchTerm);

                    // Access config safely through the plugin instance
                    var config = Plugin.Instance?.Configuration;
                    if (config == null)
                    {
                        _logger.LogWarning("Plugin configuration unavailable");
                    }
                    else if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
                    {
                        _logger.LogWarning("TMDB API key not configured - search will be normal");
                    }
                    else if (!ValidateLibraryPaths(config))
                    {
                        _logger.LogWarning("No valid library paths configured - at least one of Movies or Shows path must exist");
                    }
                    else
                    {
                        _logger.LogInformation("TMDB API key is configured - querying TMDB");

                        try
                        {
                            // Query TMDB
                            var tmdbResults = await QueryTmdbAsync(searchTerm, config).ConfigureAwait(false);
                            _logger.LogInformation("[TMDB] Found {ResultCount} results for '{SearchTerm}'", tmdbResults.Count, searchTerm);

                            // Save results to files
                            await SaveResultsToFilesAsync(tmdbResults, config).ConfigureAwait(false);

                            // Store results in context for later use
                            context.HttpContext.Items["TmdbResults"] = tmdbResults;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[TMDB] Error querying TMDB for '{SearchTerm}'", searchTerm);
                        }
                    }
                }

                // Continue with normal request
                await next().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in JfResolveSearchProvider");
                await next().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Validates that at least one library path exists.
        /// </summary>
        private bool ValidateLibraryPaths(PluginConfiguration config)
        {
            bool moviesPathValid = !string.IsNullOrWhiteSpace(config.MoviesLibraryPath) && Directory.Exists(config.MoviesLibraryPath);
            bool showsPathValid = !string.IsNullOrWhiteSpace(config.ShowsLibraryPath) && Directory.Exists(config.ShowsLibraryPath);
            bool animePathValid = !string.IsNullOrWhiteSpace(config.AnimeLibraryPath) && Directory.Exists(config.AnimeLibraryPath);

            _logger.LogDebug("[PATHS] Movies: {MoviesValid}, Shows: {ShowsValid}, Anime: {AnimeValid}", moviesPathValid, showsPathValid, animePathValid);

            return moviesPathValid || showsPathValid || animePathValid;
        }

        /// <summary>
        /// Saves TMDB results to appropriate library folders as STRM files.
        /// </summary>
        private async Task SaveResultsToFilesAsync(List<TmdbResult> results, PluginConfiguration config)
        {
            var config_local = Plugin.Instance?.Configuration;
            var jellyfinBase = config_local?.JellyfinBaseUrl?.TrimEnd('/') ?? "http://localhost:8096";

            foreach (var result in results)
            {
                try
                {
                    bool isAnime = result.Genres?.Contains(AnimeGenreId) ?? false;
                    string? year = ExtractYear(result.ReleaseDate);
                    string folderName = SanitizeFileName($"{result.Title} ({year}) (jfrtmp)");

                    if (result.Type == "Movie")
                    {
                        // Determine if we should use anime path
                        string? targetPath = isAnime ? config.AnimeLibraryPath : config.MoviesLibraryPath;

                        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
                        {
                            _logger.LogWarning("[SAVE] Movie library path not available for '{Title}'", result.Title);
                            continue;
                        }

                        await SaveMovieAsync(result, targetPath, folderName, jellyfinBase).ConfigureAwait(false);
                    }
                    else if (result.Type == "Series")
                    {
                        // For series, use anime path if it's anime, otherwise shows path
                        string? targetPath = isAnime ? config.AnimeLibraryPath : config.ShowsLibraryPath;

                        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
                        {
                            _logger.LogWarning("[SAVE] Series library path not available for '{Title}'", result.Title);
                            continue;
                        }

                        await SaveSeriesAsync(result, targetPath, folderName, jellyfinBase).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SAVE] Error saving result for '{Title}'", result.Title);
                }
            }

            // Trigger library refresh
            await TriggerLibraryRefreshAsync(config_local).ConfigureAwait(false);
        }

        /// <summary>
        /// Saves a movie result to a STRM file.
        /// </summary>
        private async Task SaveMovieAsync(TmdbResult result, string libraryPath, string folderName, string jellyfinBase)
        {
            try
            {
                string movieFolder = Path.Combine(libraryPath, folderName);
                Directory.CreateDirectory(movieFolder);

                string safeTitle = result.Title ?? "Unknown";
                string strmFileName = $"{SanitizeFileName(safeTitle)} ({ExtractYear(result.ReleaseDate)}).strm";
                string strmPath = Path.Combine(movieFolder, strmFileName);

                // Skip if file already exists
                if (File.Exists(strmPath))
                {
                    _logger.LogDebug("[SAVE] Movie STRM file already exists: {StrmPath}", strmPath);
                    return;
                }

                string strmContent = $"{jellyfinBase}/Plugins/Jfresolve/resolve/movie/{result.ImdbId}";
                await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);

                _logger.LogInformation("[SAVE] Created movie STRM file: {StrmPath}", strmPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SAVE] Error saving movie '{Title}'", result.Title);
            }
        }

        /// <summary>
        /// Saves a series result to STRM files for all episodes.
        /// </summary>
        private async Task SaveSeriesAsync(TmdbResult result, string libraryPath, string folderName, string jellyfinBase)
        {
            try
            {
                string seriesFolder = Path.Combine(libraryPath, folderName);
                Directory.CreateDirectory(seriesFolder);

                // Get full series details including seasons/episodes
                var seriesDetails = await GetShowDetailsAsync(result.TmdbId.ToString(CultureInfo.InvariantCulture), Plugin.Instance?.Configuration!).ConfigureAwait(false);

                if (seriesDetails.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("[SAVE] Failed to get series details for '{Title}'", result.Title);
                    return;
                }

                string year = ExtractYear(result.ReleaseDate) ?? "Unknown";
                var seasons = seriesDetails.TryGetProperty("seasons", out var seasonsEl)
                    ? seasonsEl.EnumerateArray().ToList()
                    : new List<JsonElement>();

                int createdCount = 0;
                foreach (var season in seasons)
                {
                    if (!season.TryGetProperty("season_number", out var seasonNumEl))
                        {
                            continue;
                        }

                    int seasonNum = seasonNumEl.GetInt32();

                    string seasonFolder = Path.Combine(seriesFolder, $"Season {seasonNum:D2}");
                    Directory.CreateDirectory(seasonFolder);

                    if (!season.TryGetProperty("episode_count", out var episodeCountEl))
                        {
                            continue;
                        }

                    int episodeCount = episodeCountEl.GetInt32();

                    for (int ep = 1; ep <= episodeCount; ep++)
                    {
                        string safeTitle = result.Title ?? "Unknown";
                        string strmFileName = $"{SanitizeFileName(safeTitle)} ({year}) S{seasonNum:D2}E{ep:D2}.strm";
                        string strmPath = Path.Combine(seasonFolder, strmFileName);

                        // Skip if file already exists
                        if (File.Exists(strmPath))
                        {
                            _logger.LogDebug("[SAVE] Series STRM file already exists: {StrmPath}", strmPath);
                            continue;
                        }

                        string strmContent = $"{jellyfinBase}/Plugins/Jfresolve/resolve/series/{result.ImdbId}?season={seasonNum}&episode={ep}";
                        await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);
                        createdCount++;
                    }
                }

                _logger.LogInformation("[SAVE] Created {Count} series STRM files for '{Title}'", createdCount, result.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SAVE] Error saving series '{Title}'", result.Title);
            }
        }

        /// <summary>
        /// Triggers a library refresh in Jellyfin.
        /// </summary>
        private async Task TriggerLibraryRefreshAsync(PluginConfiguration? config)
        {
            try
            {
                if (config == null)
                {
                    _logger.LogWarning("[REFRESH] Configuration unavailable for library refresh");
                    return;
                }

                // Implement library refresh logic here
                // This might involve calling Jellyfin's library scan API
                _logger.LogInformation("[REFRESH] Triggering library refresh");
                // TODO: Implement actual refresh call

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REFRESH] Error triggering library refresh");
            }
        }

        /// <summary>
        /// Queries TMDB for movies and TV shows matching the search term.
        /// </summary>
        private async Task<List<TmdbResult>> QueryTmdbAsync(string searchTerm, PluginConfiguration config)
        {
            var results = new List<TmdbResult>();

            try
            {
                // Search for movies
                var movies = await SearchTmdbMoviesAsync(searchTerm, config).ConfigureAwait(false);
                results.AddRange(movies);
                _logger.LogDebug("[TMDB] Found {MovieCount} movies (limited to {MaxResults})", movies.Count, MaxResultsPerType);

                // Search for TV shows
                var shows = await SearchTmdbShowsAsync(searchTerm, config).ConfigureAwait(false);
                results.AddRange(shows);
                _logger.LogDebug("[TMDB] Found {ShowCount} TV shows (limited to {MaxResults})", shows.Count, MaxResultsPerType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying TMDB");
            }

            return results;
        }

        /// <summary>
        /// Searches TMDB for movies.
        /// </summary>
        private async Task<List<TmdbResult>> SearchTmdbMoviesAsync(string searchTerm, PluginConfiguration config)
        {
            var results = new List<TmdbResult>();

            try
            {
                string url = $"{TmdbBaseUrl}/search/movie?api_key={config.TmdbApiKey}&query={Uri.EscapeDataString(searchTerm)}&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}";

                _logger.LogDebug("[TMDB] Querying movies: {SearchTerm}", searchTerm);
                var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    int count = 0;
                    foreach (var movie in resultsArray.EnumerateArray())
                    {
                        if (count >= MaxResultsPerType)
                            {
                                break;
                            }

                        if (!movie.TryGetProperty("id", out var idEl))
                            {
                                continue;
                            }

                        int tmdbId = idEl.GetInt32();

                        // Fetch extended details to get IMDb ID and genres
                        var extendedDetails = await GetMovieDetailsAsync(tmdbId.ToString(CultureInfo.InvariantCulture), config).ConfigureAwait(false);

                        var genres = ExtractGenres(extendedDetails);

                        var result = new TmdbResult
                        {
                            TmdbId = tmdbId,
                            ImdbId = ExtractImdbId(extendedDetails),
                            Type = "Movie",
                            Title = movie.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : "Unknown",
                            Overview = movie.TryGetProperty("overview", out var overviewEl) ? overviewEl.GetString() : string.Empty,
                            PosterPath = movie.TryGetProperty("poster_path", out var posterEl) ? posterEl.GetString() : null,
                            ReleaseDate = movie.TryGetProperty("release_date", out var dateEl) ? dateEl.GetString() : null,
                            Popularity = movie.TryGetProperty("popularity", out var popEl) ? popEl.GetDouble() : 0,
                            Genres = genres
                        };

                        results.Add(result);
                        count++;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[TMDB] HTTP error searching for movies");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TMDB] Error parsing movie results");
            }

            return results;
        }

        /// <summary>
        /// Searches TMDB for TV shows.
        /// </summary>
        private async Task<List<TmdbResult>> SearchTmdbShowsAsync(string searchTerm, PluginConfiguration config)
        {
            var results = new List<TmdbResult>();

            try
            {
                string url = $"{TmdbBaseUrl}/search/tv?api_key={config.TmdbApiKey}&query={Uri.EscapeDataString(searchTerm)}&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}";

                _logger.LogDebug("[TMDB] Querying TV shows: {SearchTerm}", searchTerm);
                var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    int count = 0;
                    foreach (var show in resultsArray.EnumerateArray())
                    {
                        if (count >= MaxResultsPerType)
                            {
                                break;
                            }

                        if (!show.TryGetProperty("id", out var idEl))
                            {
                                continue;
                            }

                        int tmdbId = idEl.GetInt32();

                        // Fetch extended details to get IMDb ID and genres
                        var extendedDetails = await GetShowDetailsAsync(tmdbId.ToString(CultureInfo.InvariantCulture), config).ConfigureAwait(false);

                        var genres = ExtractGenres(extendedDetails);

                        var result = new TmdbResult
                        {
                            TmdbId = tmdbId,
                            ImdbId = ExtractImdbId(extendedDetails),
                            Type = "Series",
                            Title = show.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "Unknown",
                            Overview = show.TryGetProperty("overview", out var overviewEl) ? overviewEl.GetString() : string.Empty,
                            PosterPath = show.TryGetProperty("poster_path", out var posterEl) ? posterEl.GetString() : null,
                            ReleaseDate = show.TryGetProperty("first_air_date", out var dateEl) ? dateEl.GetString() : null,
                            Popularity = show.TryGetProperty("popularity", out var popEl) ? popEl.GetDouble() : 0,
                            Genres = genres
                        };

                        results.Add(result);
                        count++;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[TMDB] HTTP error searching for TV shows");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TMDB] Error parsing TV show results");
            }

            return results;
        }

        /// <summary>
        /// Fetches extended details for a movie including IMDb ID.
        /// </summary>
        private async Task<JsonElement> GetMovieDetailsAsync(string movieId, PluginConfiguration config)
        {
            try
            {
                string url = $"{TmdbBaseUrl}/movie/{movieId}?api_key={config.TmdbApiKey}&append_to_response=external_ids";
                var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                return JsonSerializer.Deserialize<JsonElement>(response);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TMDB] Error fetching extended details for movie {MovieId}", movieId);
                return default;
            }
        }

        /// <summary>
        /// Fetches extended details for a TV show including IMDb ID.
        /// </summary>
        private async Task<JsonElement> GetShowDetailsAsync(string showId, PluginConfiguration config)
        {
            try
            {
                string url = $"{TmdbBaseUrl}/tv/{showId}?api_key={config.TmdbApiKey}&append_to_response=external_ids";
                var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                return JsonSerializer.Deserialize<JsonElement>(response);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TMDB] Error fetching extended details for TV show {ShowId}", showId);
                return default;
            }
        }

        /// <summary>
        /// Extracts IMDb ID from extended details JSON.
        /// </summary>
        private string? ExtractImdbId(JsonElement details)
        {
            try
            {
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("external_ids", out var externalIds) &&
                    externalIds.TryGetProperty("imdb_id", out var imdbIdEl))
                {
                    return imdbIdEl.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TMDB] Error extracting IMDb ID from extended details");
            }

            return null;
        }

        /// <summary>
        /// Extracts genre IDs from TMDB details JSON.
        /// </summary>
        private List<int>? ExtractGenres(JsonElement details)
        {
            try
            {
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("genres", out var genresEl))
                {
                    var genres = new List<int>();
                    foreach (var genre in genresEl.EnumerateArray())
                    {
                        if (genre.TryGetProperty("id", out var idEl))
                        {
                            genres.Add(idEl.GetInt32());
                        }
                    }

                    return genres.Count > 0 ? genres : null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TMDB] Error extracting genres from extended details");
            }

            return null;
        }

        /// <summary>
        /// Extracts the year from a date string (YYYY-MM-DD format).
        /// </summary>
        private string? ExtractYear(string? dateStr)
        {
            if (string.IsNullOrEmpty(dateStr) || dateStr.Length < 4)
                {
                    return "Unknown";
                }

            return dateStr.Substring(0, 4);
        }

        /// <summary>
        /// Sanitizes a string to be used as a filename.
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Concat(fileName.Split(invalidChars));
        }

        /// <summary>
        /// Determines if a search should proceed based on debounce logic.
        /// Returns false if the same search term was recently searched.
        /// </summary>
        private static bool ShouldSearch(string? searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return false;
            }

            lock (_debounceLock)
            {
                var now = DateTime.UtcNow;
                var timeSinceLastSearch = (now - _lastSearchTime).TotalMilliseconds;

                // If same search term and not enough time has passed, skip
                if (searchTerm == _lastSearchTerm && timeSinceLastSearch < DebounceDelayMs)
                {
                    return false;
                }

                // Update the state for next check
                _lastSearchTerm = searchTerm;
                _lastSearchTime = now;

                return true;
            }
        }
    }
}
