namespace Jfresolve;

/// <summary>
/// Constants used throughout the Jfresolve plugin
/// </summary>
public static class Constants
{
    // Streaming buffer configuration
    public const int StreamBufferSize = 262144; // 256KB buffer for better throughput
    public const int StreamFlushInterval = 4; // Flush every 4 buffers (1MB chunks)
    public const int LegacyStreamBufferSize = 81920; // 80KB (kept for reference)

    // HTTP client timeouts
    public const int AddonRequestTimeoutSeconds = 30; // Timeout for Stremio addon requests
    public const int StreamRequestTimeoutMinutes = 10; // Timeout for streaming requests

    // Cache configuration
    public const int MaxMetadataCacheSize = 500; // Maximum metadata cache entries
    public const double CacheEvictionPercentage = 0.1; // Remove 10% when cache is full
    public static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2); // Cache expiry time

    // Failover cache cleanup
    public static readonly TimeSpan FailoverCacheCleanupInterval = TimeSpan.FromHours(1); // Cleanup every hour
    public static readonly TimeSpan FailoverCacheEntryMaxAge = TimeSpan.FromHours(24); // Max age for cache entries

    // Image download retry configuration
    public const int MaxImageRetryAttempts = 5;
    public static readonly int[] ImageRetryDelays = { 500, 1000, 2000, 5000, 10000, 15000 }; // milliseconds

    // Quality priority order (highest to lowest)
    public static readonly string[] QualityPriority = { "4K", "1440p", "1080p", "720p", "480p" };

    // HTTP headers
    public const string UserAgent = "Jfresolve/1.0";
    public const string CacheControlNoCache = "no-cache, no-store, must-revalidate";
    public const string PragmaNoCache = "no-cache";
    public const string ExpiresZero = "0";
    public const string AcceptRangesBytes = "bytes";

    // API endpoints
    public const string TmdbBaseUrl = "https://api.themoviedb.org/3";
    
    // Limits
    public const int MaxItemsPerQualityDefault = 1;
    public const int MaxItemsPerQualityMin = 1;
    public const int MaxItemsPerQualityMax = 10;

    // Retry configuration
    public const int MaxRetryAttempts = 3;
    public static readonly int[] RetryDelays = { 500, 1000, 2000 }; // milliseconds, exponential backoff

    // TMDB API optimization
    public const int MaxConcurrentTmdbRequests = 5; // Max concurrent requests to TMDB API (rate limit protection)
    public const int ImdbIdCacheMaxSize = 1000; // Maximum cached IMDB ID lookups
    public static readonly TimeSpan ImdbIdCacheExpiry = TimeSpan.FromHours(24); // Cache IMDB IDs for 24 hours

    // Stream metadata caching
    public const int StreamMetadataCacheMaxSize = 500; // Maximum cached stream metadata responses
    public static readonly TimeSpan StreamMetadataCacheExpiry = TimeSpan.FromMinutes(10); // Cache stream metadata for 10 minutes
    public static readonly TimeSpan StreamMetadataCacheCleanupInterval = TimeSpan.FromMinutes(30); // Cleanup every 30 minutes

    // Folder lookup caching
    public const int FolderCacheMaxSize = 100; // Maximum cached folder references
    public static readonly TimeSpan FolderCacheExpiry = TimeSpan.FromHours(1); // Cache folders for 1 hour
    public static readonly TimeSpan FolderCacheCleanupInterval = TimeSpan.FromHours(2); // Cleanup every 2 hours
}
