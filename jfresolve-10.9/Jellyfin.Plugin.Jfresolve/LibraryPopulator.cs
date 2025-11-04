// librarypopulator.cs
using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jfresolve.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jfresolve
{
    /// <summary>
    /// Library populator class.
    /// </summary>
    public class LibraryPopulator : IDisposable
    {
        private readonly PluginConfiguration _config;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryPopulator"/> class.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="logger">Logger instance for logging messages.</param>
        public LibraryPopulator(PluginConfiguration config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Releases the resources used by this instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method.
        /// </summary>
        /// <param name="disposing">True if called from Dispose, false if from finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _httpClient?.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// Entry point: populate all configured libraries.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task PopulateLibrariesAsync()
        {
            if (!string.IsNullOrWhiteSpace(_config.MoviesLibraryPath))
            {
                await PopulateMoviesAsync().ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_config.ShowsLibraryPath))
            {
                await PopulateShowsAsync().ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_config.AnimeLibraryPath))
            {
                await PopulateAnimeAsync().ConfigureAwait(false);
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(c, '_');
                }

            return name;
        }

        private async Task PopulateMoviesAsync()
        {
            _logger.LogInformation("Populating Movies...");
            var items = await FetchFromTmdbAsync("movie/popular").ConfigureAwait(false);

            foreach (var item in items)
            {
                try
                {
                    var releaseDateStr = GetProperty(item, "release_date");
                    if (!_config.IncludeUnreleased && DateTime.TryParse(releaseDateStr, out var releaseDate))
                    {
                        if (releaseDate > DateTime.UtcNow)
                        {
                            _logger.LogInformation("Skipping unreleased title: {0} (Release date: {1})", GetProperty(item, "title"), releaseDate.ToShortDateString());
                            continue;
                        }
                    }

                    var movieId = GetProperty(item, "id");
                    var movieDetails = await GetMovieDetailsAsync(movieId).ConfigureAwait(false);

                    var title = GetProperty(movieDetails, "title");
                    var year = !string.IsNullOrEmpty(releaseDateStr) && releaseDateStr.Length >= 4 ? releaseDateStr[..4] : "Unknown";
                    string? imdbId = movieDetails.GetProperty("external_ids").GetProperty("imdb_id").GetString();
                    if (string.IsNullOrEmpty(imdbId))
                    {
                        _logger.LogWarning("Movie {0} does not have an IMDb ID, skipping...", title);
                        continue;
                    }

                    var folderName = SanitizeFileName($"{title} ({year})");
                    var movieFolder = Path.Combine(_config.MoviesLibraryPath, folderName);
                    var strmFileName = $"{title} ({year}).strm";
                    var strmPath = Path.Combine(movieFolder, SanitizeFileName(strmFileName));

                    Directory.CreateDirectory(movieFolder);
                    var jellyfinBase = _config.JellyfinBaseUrl.TrimEnd('/');
                    var strmContent = $"{jellyfinBase}/Plugins/Jfresolve/resolve/movie/{imdbId}";
                    await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);
                    _logger.LogInformation("Created STRM file for {0} at {1}", folderName, strmPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing movie item");
                }
            }

            _logger.LogInformation("Movies population completed.");
        }

        private async Task PopulateShowsAsync()
        {
            _logger.LogInformation("Populating Shows...");
            var items = await FetchFromTmdbAsync("tv/popular", "&without_genres=16").ConfigureAwait(false);

            foreach (var item in items)
            {
                try
                {
                    var releaseDateStr = GetProperty(item, "first_air_date");
                    if (!_config.IncludeUnreleased && DateTime.TryParse(releaseDateStr, out var releaseDate))
                    {
                        if (releaseDate > DateTime.UtcNow)
                        {
                            _logger.LogInformation("Skipping unreleased title: {0} (Release date: {1})", GetProperty(item, "name"), releaseDate.ToShortDateString());
                            continue;
                        }
                    }

                    var showId = GetProperty(item, "id");
                    var showDetails = await GetShowDetailsAsync(showId).ConfigureAwait(false);

                    var showName = GetProperty(showDetails, "name");
                    var year = !string.IsNullOrEmpty(releaseDateStr) && releaseDateStr.Length >= 4 ? releaseDateStr[..4] : "Unknown";
                    var showFolderName = SanitizeFileName($"{showName} ({year})");
                    var showFolder = Path.Combine(_config.ShowsLibraryPath, showFolderName);
                    string? imdbId = showDetails.GetProperty("external_ids").GetProperty("imdb_id").GetString();
                    if (string.IsNullOrEmpty(imdbId))
                    {
                        _logger.LogWarning("Show {0} does not have an IMDb ID, skipping...", showName);
                        continue;
                    }

                    Directory.CreateDirectory(showFolder);

                    var seasons = GetArrayProperty(showDetails, "seasons");
                    foreach (var season in seasons)
                    {
                        var seasonNum = int.Parse(GetProperty(season, "season_number"), CultureInfo.InvariantCulture);
                        if (seasonNum == 0 && !_config.IncludeSpecials)
                        {
                            _logger.LogInformation("Skipping Season 0 (Specials) for {0}", showName);
                            continue;
                        }

                        var seasonFolder = Path.Combine(showFolder, $"Season {seasonNum:D2}");
                        Directory.CreateDirectory(seasonFolder);

                        var episodeCount = int.Parse(GetProperty(season, "episode_count"), CultureInfo.InvariantCulture);
                        for (int ep = 1; ep <= episodeCount; ep++)
                        {
                            var strmFileName = $"{showName} ({year}) S{seasonNum:D2}E{ep:D2}.strm";
                            var strmPath = Path.Combine(seasonFolder, SanitizeFileName(strmFileName));

                            if (File.Exists(strmPath))
                            {
                                continue;
                            }

                            var jellyfinBase = _config.JellyfinBaseUrl.TrimEnd('/');
                            var strmContent = $"{jellyfinBase}/Plugins/Jfresolve/resolve/series/{imdbId}?season={seasonNum}&episode={ep}";
                            await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);
                        }
                    }

                    _logger.LogInformation("Created STRM files for show: {0}", showFolderName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing show item");
                }
            }

            _logger.LogInformation("Shows population completed.");
        }

        private async Task PopulateAnimeAsync()
        {
            _logger.LogInformation("Populating anime...");
            var items = await FetchFromTmdbAsync("tv/popular", "&with_genres=16").ConfigureAwait(false);

            foreach (var item in items)
            {
                try
                {
                    var releaseDateStr = GetProperty(item, "first_air_date");
                    if (!_config.IncludeUnreleased && DateTime.TryParse(releaseDateStr, out var releaseDate))
                    {
                        if (releaseDate > DateTime.UtcNow)
                        {
                            _logger.LogInformation("Skipping unreleased title: {0} (Release date: {1})", GetProperty(item, "name"), releaseDate.ToShortDateString());
                            continue;
                        }
                    }

                    var showId = GetProperty(item, "id");
                    var showDetails = await GetShowDetailsAsync(showId).ConfigureAwait(false);

                    var showName = GetProperty(showDetails, "name");
                    var year = !string.IsNullOrEmpty(releaseDateStr) && releaseDateStr.Length >= 4 ? releaseDateStr[..4] : "Unknown";
                    var showFolderName = SanitizeFileName($"{showName} ({year})");
                    var showFolder = Path.Combine(_config.AnimeLibraryPath, showFolderName);
                    string? imdbId = showDetails.GetProperty("external_ids").GetProperty("imdb_id").GetString();
                    if (string.IsNullOrEmpty(imdbId))
                    {
                        _logger.LogWarning("Show {0} does not have an IMDb ID, skipping...", showName);
                        continue;
                    }

                    Directory.CreateDirectory(showFolder);

                    var seasons = GetArrayProperty(showDetails, "seasons");
                    foreach (var season in seasons)
                    {
                        var seasonNum = int.Parse(GetProperty(season, "season_number"), CultureInfo.InvariantCulture);
                        if (seasonNum == 0 && !_config.IncludeSpecials)
                        {
                            _logger.LogInformation("Skipping Season 0 (Specials) for {0}", showName);
                            continue;
                        }

                        var seasonFolder = Path.Combine(showFolder, $"Season {seasonNum:D2}");
                        Directory.CreateDirectory(seasonFolder);

                        var episodeCount = int.Parse(GetProperty(season, "episode_count"), CultureInfo.InvariantCulture);
                        for (int ep = 1; ep <= episodeCount; ep++)
                        {
                            var strmFileName = $"{showName} ({year}) S{seasonNum:D2}E{ep:D2}.strm";
                            var strmPath = Path.Combine(seasonFolder, SanitizeFileName(strmFileName));

                            if (File.Exists(strmPath))
                            {
                                continue;
                            }

                            var jellyfinBase = _config.JellyfinBaseUrl.TrimEnd('/');
                            var strmContent = $"{jellyfinBase}/Plugins/Jfresolve/resolve/series/{imdbId}?season={seasonNum}&episode={ep}";
                            await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);
                        }
                    }

                    _logger.LogInformation("Created STRM files for anime: {0}", showFolderName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing anime item");
                }
            }

            _logger.LogInformation("Anime population completed.");
        }

        private async Task<JsonElement> GetMovieDetailsAsync(string movieId)
        {
            var url = $"https://api.themoviedb.org/3/movie/{movieId}?api_key={_config.TmdbApiKey}&append_to_response=external_ids";
            var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JsonElement>(response);
        }

        private async Task<JsonElement> GetShowDetailsAsync(string showId)
        {
            var url = $"https://api.themoviedb.org/3/tv/{showId}?api_key={_config.TmdbApiKey}&append_to_response=external_ids";
            var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JsonElement>(response);
        }

        /// <summary>
        /// Fetch items from TMDb using the API key.
        /// </summary>
        private async Task<JsonElement[]> FetchFromTmdbAsync(string endpoint, string extraParams = "")
        {
            var includeAdult = _config.IncludeAdult ? "true" : "false";
            var url = $"https://api.themoviedb.org/3/{endpoint}?api_key={_config.TmdbApiKey}&include_adult={includeAdult}{extraParams}";

            var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var results = result.GetProperty("results");

            var items = new JsonElement[results.GetArrayLength()];
            int i = 0;
            foreach (var item in results.EnumerateArray())
            {
                items[i++] = item;
            }

            return items;
        }

        private string GetProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? string.Empty : prop.ToString();
            }

            return string.Empty;
        }

        private JsonElement[] GetArrayProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                var array = new JsonElement[prop.GetArrayLength()];
                int i = 0;
                foreach (var item in prop.EnumerateArray())
                {
                    array[i++] = item;
                }

                return array;
            }

            return Array.Empty<JsonElement>();
        }
    }
}
