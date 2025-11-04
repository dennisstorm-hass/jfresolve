using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.Jfresolve.Models
{
    /// <summary>
    /// Model for TMDB search results.
    /// </summary>
    public class TmdbResult
    {
        /// <summary>
        /// Gets or sets the TMDB ID.
        /// </summary>
        public int TmdbId { get; set; }

        /// <summary>
        /// Gets or sets the IMDb ID.
        /// </summary>
        public string? ImdbId { get; set; }

        /// <summary>
        /// Gets or Sets Type of the result, e.g., "Movie" or "Series".
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Gets or Sets Type of the genre list.
        /// </summary>
        public IReadOnlyCollection<int>? Genres { get; set; }

        /// <summary>
        /// Gets or Sets Title of the movie or series.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// Gets or Sets Overview or description of the movie or series.
        /// </summary>
        public string? Overview { get; set; }

        /// <summary>
        /// Gets or Sets Path to the poster image.
        /// </summary>
        public string? PosterPath { get; set; }

        /// <summary>
        /// Gets or Sets Release date in string format (e.g., "YYYY-MM-DD").
        /// </summary>
        public string? ReleaseDate { get; set; }

        /// <summary>
        /// Gets or Sets Popularity score of the movie or series.
        /// </summary>
        public double Popularity { get; set; }
    }
}
