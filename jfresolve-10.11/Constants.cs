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
    public const int StreamRequestTimeoutHours = 4; // Timeout for streaming requests (4 hours to handle long movies/episodes)

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
    
    // Stream proxy retry configuration (for initial connection only, not during streaming)
    public const int MaxStreamRetryAttempts = 3;
    public static readonly int[] StreamRetryDelays = { 1000, 2000, 4000 }; // milliseconds, exponential backoff

    // TMDB API optimization
    public const int MaxConcurrentTmdbRequests = 5; // Max concurrent requests to TMDB API (rate limit protection)
    public const int ImdbIdCacheMaxSize = 1000; // Maximum cached IMDB ID lookups
    public static readonly TimeSpan ImdbIdCacheExpiry = TimeSpan.FromHours(24); // Cache IMDB IDs for 24 hours

    // Stream metadata caching
    public const int StreamMetadataCacheMaxSize = 500; // Maximum cached stream metadata responses
    public static readonly TimeSpan StreamMetadataCacheExpiry = TimeSpan.FromHours(1); // Cache stream metadata for 1 hour (increased for better resume performance)
    public static readonly TimeSpan StreamMetadataCacheCleanupInterval = TimeSpan.FromHours(2); // Cleanup every 2 hours
    
    // Redirect URL caching (before redirects) - avoids re-resolving on every Range request
    public const int RedirectUrlCacheMaxSize = 200; // Maximum cached redirect URLs
    public static readonly TimeSpan RedirectUrlCacheExpiry = TimeSpan.FromHours(1); // Cache redirect URLs for 1 hour
    public static readonly TimeSpan RedirectUrlCacheCleanupInterval = TimeSpan.FromHours(2); // Cleanup every 2 hours
    
    // Final resolved URL caching (after redirects) - speeds up resume significantly
    public const int ResolvedUrlCacheMaxSize = 200; // Maximum cached resolved URLs
    public static readonly TimeSpan ResolvedUrlCacheExpiry = TimeSpan.FromHours(2); // Cache resolved URLs for 2 hours
    public static readonly TimeSpan ResolvedUrlCacheCleanupInterval = TimeSpan.FromHours(4); // Cleanup every 4 hours

    // Folder lookup caching
    public const int FolderCacheMaxSize = 100; // Maximum cached folder references
    public static readonly TimeSpan FolderCacheExpiry = TimeSpan.FromHours(1); // Cache folders for 1 hour
    public static readonly TimeSpan FolderCacheCleanupInterval = TimeSpan.FromHours(2); // Cleanup every 2 hours
    
    // Lock cleanup configuration
    public static readonly TimeSpan LockCleanupInterval = TimeSpan.FromHours(1); // Cleanup locks every hour
    public static readonly TimeSpan LockMaxIdleTime = TimeSpan.FromHours(24); // Remove locks unused for 24 hours
    public const int MaxItemLocks = 1000; // Maximum item locks before forced cleanup
    public const int MaxPathLocks = 1000; // Maximum path locks before forced cleanup
    
    // Circuit breaker configuration
    public const int CircuitBreakerFailureThreshold = 5; // Open circuit after 5 consecutive failures
    public static readonly TimeSpan CircuitBreakerOpenDuration = TimeSpan.FromMinutes(1); // Keep circuit open for 1 minute
    public static readonly TimeSpan CircuitBreakerHalfOpenTimeout = TimeSpan.FromSeconds(30); // Timeout for half-open test
}
