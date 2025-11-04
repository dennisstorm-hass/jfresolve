using System;
using System.IO;

namespace Jfresolve.Utilities
{
    /// <summary>
    /// Utility class for file and folder name operations.
    /// Provides consistent methods for generating and sanitizing file/folder names.
    /// </summary>
    public static class FileNameUtility
    {
        /// <summary>
        /// Sanitizes a filename by replacing invalid characters with underscores.
        /// Extracted from: StrmFileGenerator.cs, InsertActionFilter.cs, JfresolvePopulator.cs
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "unknown";

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        /// <summary>
        /// Builds a library folder name for a movie.
        /// Format: "Title (Year)"
        /// </summary>
        public static string BuildMovieFolderName(string title, int? year)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "unknown";

            var yearStr = year?.ToString() ?? "Unknown";
            var folderName = $"{title} ({yearStr})";
            return SanitizeFileName(folderName);
        }

        /// <summary>
        /// Builds a library folder name for a TV series.
        /// Format: "Title (Year)"
        /// </summary>
        public static string BuildSeriesFolderName(string title, int? year)
        {
            // Same format as movie
            return BuildMovieFolderName(title, year);
        }

        /// <summary>
        /// Builds a STRM filename for a movie.
        /// Format: "Title (Year).strm"
        /// </summary>
        public static string BuildMovieStrmFileName(string title, int? year)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "unknown.strm";

            var yearStr = year?.ToString() ?? "Unknown";
            var fileName = $"{title} ({yearStr}).strm";
            return SanitizeFileName(fileName);
        }

        /// <summary>
        /// Builds a STRM filename for a TV episode.
        /// Format: "Title (Year) S##E##.strm"
        /// </summary>
        public static string BuildEpisodeStrmFileName(string title, int? year, int season, int episode)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "unknown.strm";

            var yearStr = year?.ToString() ?? "Unknown";
            var fileName = $"{title} ({yearStr}) S{season:D2}E{episode:D2}.strm";
            return SanitizeFileName(fileName);
        }

        /// <summary>
        /// Builds a season folder name.
        /// Format: "Season ##"
        /// </summary>
        public static string BuildSeasonFolderName(int seasonNumber)
        {
            return $"Season {seasonNumber:D2}";
        }

        /// <summary>
        /// Builds a display name for an episode.
        /// Format: "Title S##E##"
        /// </summary>
        public static string BuildEpisodeDisplayName(string title, int season, int episode)
        {
            if (string.IsNullOrWhiteSpace(title))
                return $"S{season:D2}E{episode:D2}";

            return $"{title} S{season:D2}E{episode:D2}";
        }
    }
}
