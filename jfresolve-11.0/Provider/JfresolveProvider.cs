
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jfresolve.Configuration;
using Jfresolve.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Provider
{
    /// <summary>
    /// Represents external metadata from TMDB or other sources.
    /// This is similar to Gelato's StremioMeta but adapted for TMDB.
    /// </summary>
    public class ExternalMeta
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string Poster { get; set; } = string.Empty;
        public string Backdrop { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// TMDB ID (used to build provider IDs dictionary)
        /// </summary>
        public int TmdbId { get; set; }

        /// <summary>
        /// IMDB ID (used for duplicate detection and provider IDs)
        /// </summary>
        public string? ImdbId { get; set; }

        /// <summary>
        /// Release date as string (e.g., "2024-01-15")
        /// </summary>
        public string? ReleaseDate { get; set; }

        /// <summary>
        /// Popularity score from TMDB
        /// </summary>
        public double Popularity { get; set; }

        /// <summary>
        /// Genre IDs from TMDB (useful for filtering)
        /// </summary>
        public List<int>? GenreIds { get; set; }

        /// <summary>
        /// Whether this is an anime based on genre classification (Genre ID 16)
        /// </summary>
        public bool IsAnime { get; set; }

        public Dictionary<string, string> GetProviderIds()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (TmdbId > 0)
            {
                dict["Tmdb"] = TmdbId.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(ImdbId))
            {
                dict["Imdb"] = ImdbId;
            }

            return dict;
        }
    }

    // A helper class to create unique, identifiable paths for our items.
    public class JfresolveUri
    {
        public const string UriScheme = "jfresolve";
        public string Type { get; }
        public string Id { get; }

        public JfresolveUri(string type, string id)
        {
            Type = type;
            Id = id;
        }

        public override string ToString()
        {
            return $"{UriScheme}://{Type}/{Id}";
        }

        public Guid ToGuid()
        {
            using var provider = System.Security.Cryptography.MD5.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(ToString());
            var hash = provider.ComputeHash(bytes);
            return new Guid(hash);
        }

        public static JfresolveUri FromString(string uri)
        {
            var parts = uri.Replace($"{UriScheme}://", string.Empty).Split('/');
            return new JfresolveUri(parts[0], parts[1]);
        }
    }

    public class JfresolveProvider
    {
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
        private const string TmdbImageBaseUrl = "https://www.themoviedb.org/t/p/w342";
        private const string TmdbBackdropImageBaseUrl = "https://www.themoviedb.org/t/p/w1280";

        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<JfresolveProvider> _logger;

        public Dictionary<Guid, ExternalMeta> MetaCache { get; } = new();

        public JfresolveProvider(
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILogger<JfresolveProvider> logger)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Searches TMDB for movies and series matching the query.
        /// Respects user configuration for filtering (adult, unreleased, etc.)
        /// </summary>
        public async Task<IReadOnlyList<ExternalMeta>> SearchAsync(string query)
        {
            var results = new List<ExternalMeta>();

            if (string.IsNullOrWhiteSpace(query))
            {
                return results;
            }

            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _logger.LogWarning("[JfresolveProvider] TMDB API key not configured - search results unavailable");
                return results;
            }

            try
            {
                // Search for movies
                var movieResults = await SearchTmdbAsync(query, "movie", config);
                results.AddRange(movieResults);

                // Search for TV series
                var seriesResults = await SearchTmdbAsync(query, "tv", config);
                results.AddRange(seriesResults);

                // Fetch IMDB IDs for all results to enable streaming
                await EnrichWithImdbIdsAsync(results, config);

                _logger.LogInformation(
                    "[JfresolveProvider] TMDB search '{Query}' returned {MovieCount} movies and {SeriesCount} series",
                    query,
                    movieResults.Count,
                    seriesResults.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JfresolveProvider] Error searching TMDB for '{Query}'", query);
            }

            return results;
        }

        /// <summary>
        /// Searches TMDB for a specific type (movie or tv).
        /// Limits results based on configuration and filters based on release date.
        /// </summary>
        private async Task<List<ExternalMeta>> SearchTmdbAsync(string searchTerm, string searchType, PluginConfiguration config)
        {
            var results = new List<ExternalMeta>();

            try
            {
                var includeAdult = config.IncludeAdult ? "true" : "false";
                var url = $"{TmdbBaseUrl}/search/{searchType}" +
                    $"?api_key={config.TmdbApiKey}" +
                    $"&query={Uri.EscapeDataString(searchTerm)}" +
                    $"&include_adult={includeAdult}";

                _logger.LogDebug("[JfresolveProvider] Querying TMDB {Type}: {Url}", searchType, url);

                using var client = _httpClientFactory.CreateClient(nameof(JfresolveProvider));
                client.Timeout = TimeSpan.FromSeconds(10);

                using var response = await client.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);

                if (!doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    _logger.LogWarning("[JfresolveProvider] TMDB response missing 'results' field");
                    return results;
                }

                int count = 0;
                int maxResults = config.SearchNumber > 0 ? config.SearchNumber : 3;

                foreach (var item in resultsArray.EnumerateArray())
                {
                    if (count >= maxResults)
                    {
                        break;
                    }

                    try
                    {
                        var meta = ParseTmdbResult(item, searchType, config);
                        if (meta != null)
                        {
                            results.Add(meta);
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[JfresolveProvider] Error parsing TMDB {Type} result", searchType);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[JfresolveProvider] HTTP error searching TMDB {Type}", searchType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JfresolveProvider] Unexpected error searching TMDB {Type}", searchType);
            }

            return results;
        }

        /// <summary>
        /// Parses a single TMDB search result and creates an ExternalMeta object.
        /// Returns null if the item should be filtered out based on configuration.
        /// </summary>
        private ExternalMeta? ParseTmdbResult(JsonElement item, string searchType, PluginConfiguration config)
        {
            if (!item.TryGetProperty("id", out var idEl))
            {
                return null;
            }

            int tmdbId = idEl.GetInt32();

            // Extract common fields
            string title = searchType == "movie"
                ? JsonHelper.GetJsonString(item, "title")
                : JsonHelper.GetJsonString(item, "name");

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string releaseDate = searchType == "movie"
                ? JsonHelper.GetJsonString(item, "release_date")
                : JsonHelper.GetJsonString(item, "first_air_date");

            // Filter based on release date if IncludeUnreleased is false
            if (!config.IncludeUnreleased && !string.IsNullOrWhiteSpace(releaseDate))
            {
                if (DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var relDate))
                {
                    if (relDate > DateTime.UtcNow.AddDays(config.UnreleasedBufferDays))
                    {
                        _logger.LogDebug("[JfresolveProvider] Filtering unreleased item: {Title} ({Date})", title, releaseDate);
                        return null;
                    }
                }
            }

            int? year = null;
            if (!string.IsNullOrWhiteSpace(releaseDate) && int.TryParse(releaseDate.AsSpan(0, 4), out var y))
            {
                year = y;
            }

            string posterPath = JsonHelper.GetJsonString(item, "poster_path");
            string posterUrl = string.IsNullOrWhiteSpace(posterPath)
                ? string.Empty
                : $"{TmdbImageBaseUrl}{posterPath}";

            string backdropPath = JsonHelper.GetJsonString(item, "backdrop_path");
            string backdropUrl = string.IsNullOrWhiteSpace(backdropPath)
                ? string.Empty
                : $"{TmdbBackdropImageBaseUrl}{backdropPath}";

            string overview = JsonHelper.GetJsonString(item, "overview");
            double popularity = JsonHelper.GetJsonDouble(item, "popularity");
            var genreIds = JsonHelper.GetJsonIntArray(item, "genre_ids");

            // Check if this is anime (Genre ID 16 = Anime)
            bool isAnime = genreIds.Contains(16);

            var meta = new ExternalMeta
            {
                Id = tmdbId.ToString(CultureInfo.InvariantCulture),
                TmdbId = tmdbId,
                Name = title,
                Type = searchType == "movie" ? "Movie" : "Series",
                Year = year,
                Poster = posterUrl,
                Backdrop = backdropUrl,
                Description = overview,
                ReleaseDate = releaseDate,
                Popularity = popularity,
                GenreIds = genreIds,
                IsAnime = isAnime
            };

            return meta;
        }

        /// <summary>
        /// Enriches search results with IMDB IDs by making additional API calls.
        /// This is necessary for STRM file generation and streaming.
        /// </summary>
        private async Task EnrichWithImdbIdsAsync(List<ExternalMeta> results, PluginConfiguration config)
        {
            var tasks = new List<Task>();

            foreach (var meta in results)
            {
                if (meta.TmdbId > 0)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var imdbId = await GetImdbIdAsync(meta.TmdbId, meta.Type == "Movie" ? "movie" : "tv", config);
                        if (!string.IsNullOrEmpty(imdbId))
                        {
                            meta.ImdbId = imdbId;
                        }
                    }));
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the IMDB ID for a TMDB item. This requires a second API call.
        /// For Phase 1, we'll make this optional and cache the results.
        /// </summary>
        private async Task<string?> GetImdbIdAsync(int tmdbId, string mediaType, PluginConfiguration config)
        {
            try
            {
                var url = $"{TmdbBaseUrl}/{mediaType}/{tmdbId}/external_ids?api_key={config.TmdbApiKey}";

                using var client = _httpClientFactory.CreateClient(nameof(JfresolveProvider));
                client.Timeout = TimeSpan.FromSeconds(10);

                using var response = await client.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("imdb_id", out var imdbEl))
                {
                    return imdbEl.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[JfresolveProvider] Error fetching IMDB ID for TMDB {Id}", tmdbId);
            }

            return null;
        }



        /// <summary>
        /// Converts external metadata into a Jellyfin BaseItem.
        /// Similar to Gelato's IntoBaseItem but adapted for TMDB.
        /// </summary>
        public BaseItem? IntoBaseItem(ExternalMeta meta)
        {
            if (meta == null || meta.TmdbId <= 0 || string.IsNullOrWhiteSpace(meta.Name))
            {
                return null;
            }

            var uri = new JfresolveUri(meta.Type, meta.Id);

            BaseItem item;
            switch (meta.Type)
            {
                case "Movie":
                    item = new Movie
                    {
                        // Create a new GUID for this item based on our unique URI
                        Id = uri.ToGuid(),
                        Name = meta.Name,
                        ProductionYear = meta.Year,
                        Overview = meta.Description,
                        // The path is our unique identifier
                        Path = uri.ToString(),
                        IsVirtualItem = true
                    };
                    break;
                case "Series":
                    item = new Series
                    {
                        Id = uri.ToGuid(),
                        Name = meta.Name,
                        ProductionYear = meta.Year,
                        Overview = meta.Description,
                        Path = uri.ToString(),
                        IsVirtualItem = true
                    };
                    break;
                default:
                    return null;
            }

            // Set provider IDs (used for duplicate detection)
            // This is critical - these are what Jellyfin uses to find existing items
            var providerIds = meta.GetProviderIds();
            if (providerIds.Count > 0)
            {
                foreach (var kvp in providerIds)
                {
                    item.SetProviderId(kvp.Key, kvp.Value);
                }
            }

            // Set poster and backdrop images
            var images = new List<ItemImageInfo>();

            if (!string.IsNullOrEmpty(meta.Poster))
            {
                images.Add(new ItemImageInfo { Path = meta.Poster, Type = ImageType.Primary });
            }

            if (!string.IsNullOrEmpty(meta.Backdrop))
            {
                images.Add(new ItemImageInfo { Path = meta.Backdrop, Type = ImageType.Backdrop });
            }

            if (images.Count > 0)
            {
                item.ImageInfos = images.ToArray();
            }

            return item;
        }
    }
}
