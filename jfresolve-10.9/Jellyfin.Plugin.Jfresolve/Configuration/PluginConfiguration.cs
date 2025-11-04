// pluginconfiguration.cs
using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jfresolve.Configuration
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            TmdbApiKey = string.Empty;
            MoviesLibraryPath = "/data/movies";
            ShowsLibraryPath = "/data/shows";
            AnimeLibraryPath = string.Empty;
            JellyfinBaseUrl = "http://127.0.0.1:8096";
            AddonManifestUrl = string.Empty;
            SearchNumber = 3;
            IncludeAdult = false;
            IncludeUnreleased = false;
            IncludeSpecials = false;
            LibraryPopulationHour = 3;
        }

        /// <summary>
        /// Gets or Sets TMDb API key for fetching metadata.
        /// </summary>
        public string TmdbApiKey { get; set; }

        /// <summary>
        /// Gets or Sets Movies library path for plugin-populated items.
        /// </summary>
        public string MoviesLibraryPath { get; set; }

        /// <summary>
        /// Gets or Sets number of search results for each movies and shows.
        /// </summary>
        public int SearchNumber { get; set; }

        /// <summary>
        /// Gets or Sets TV Shows library path for plugin-populated items.
        /// </summary>
        public string ShowsLibraryPath { get; set; }

        /// <summary>
        /// Gets or Sets Optional Anime library path.
        /// </summary>
        public string AnimeLibraryPath { get; set; }

        /// <summary>
        /// Gets or Sets addon manifest JSON URL.
        /// </summary>
        public string AddonManifestUrl { get; set; }

        /// <summary>
        /// Gets or sets the Jellyfin base URL (including protocol and port).
        /// Example: http://127.0.0.1:8096.
        /// </summary>
        public string JellyfinBaseUrl { get; set; } = "http://127.0.0.1:8096";

        /// <summary>
        /// Gets or sets a value indicating whether adult content is included.
        /// </summary>
        public bool IncludeAdult { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether unreleased titles are included.
        /// </summary>
        public bool IncludeUnreleased { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether specials (Season 0) are included.
        /// </summary>
        public bool IncludeSpecials { get; set; }

        /// <summary>
        /// Gets or Sets last population date and time.
        /// </summary>
        public DateTime? LastPopulationUtc { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether library population is enabled.
        /// </summary>
        public bool EnableLibraryPopulation { get; set; } = true;

        /// <summary>
        /// Gets or sets the hour (0-23 UTC) when daily library population should run.
        /// Default: 3 (3 AM UTC).
        /// </summary>
        public int LibraryPopulationHour { get; set; }
    }
}
