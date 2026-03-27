using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

/// <summary>
/// Fetches movie release candidates from dvdsreleasedates.com digital releases pages.
/// </summary>
public sealed class DvdReleaseDatesService
{
    private static readonly Regex DateRegex = new(
        @"\b(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s+[A-Za-z]+\s+\d{1,2},\s+\d{4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MovieLinkRegex = new(
        "<a[^>]+href=\"(?<href>https?://www\\.dvdsreleasedates\\.com/movies/[^\"]+|/movies/[^\"]+)\"[^>]*>(?<title>[^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ImdbRegex = new(
        "imdb\\.com/title/(?<id>tt\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PrevMonthRegex = new(
        "<a[^>]+href=\"(?<href>https?://www\\.dvdsreleasedates\\.com/digital-releases/\\d{4}/\\d{1,2}/[^\"]+|/digital-releases/\\d{4}/\\d{1,2}/[^\"]+)\"[^>]*>\\s*&lt;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DvdReleaseDatesService> _log;
    private readonly TmdbService _tmdbService;

    // key: "{monthsBack}:{utcDate:yyyyMMdd}"
    private static readonly ConcurrentDictionary<string, (DateTime expiry, IReadOnlyList<TmdbMovie> movies)> _cache = new();

    public DvdReleaseDatesService(
        IHttpClientFactory httpClientFactory,
        ILogger<DvdReleaseDatesService> log,
        TmdbService tmdbService)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
        _tmdbService = tmdbService;
    }

    public async Task<IReadOnlyList<TmdbMovie>> GetReleasedMoviesAsTmdbAsync(
        string tmdbApiKey,
        int monthsBack,
        bool includeAdult,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        monthsBack = Math.Clamp(monthsBack, 0, 3);
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 120);
        var cacheKey = $"{monthsBack}:{DateTime.UtcNow:yyyyMMdd}";
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > now)
        {
            return cached.movies;
        }

        var pageUrls = await GetMonthPagesAsync(monthsBack, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        var rawEntries = new List<DvdReleaseMovieEntry>();
        foreach (var url in pageUrls)
        {
            var html = await FetchPageAsync(url, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                continue;
            }

            rawEntries.AddRange(ParseEntries(html));
        }

        var releasedEntries = rawEntries
            .Where(e => e.ReleaseDate.Date <= DateTime.UtcNow.Date)
            .GroupBy(e => e.ImdbId ?? $"{e.Title}:{e.ReleaseDate:yyyy-MM-dd}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        _log.LogInformation(
            "Jfresolve: DVD source parsed {RawCount} entries; {ReleasedCount} already released",
            rawEntries.Count,
            releasedEntries.Count);

        var resolved = new List<TmdbMovie>(releasedEntries.Count);
        foreach (var entry in releasedEntries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            TmdbMovie? movie = null;
            if (!string.IsNullOrWhiteSpace(entry.ImdbId))
            {
                movie = await _tmdbService.FindMovieByImdbIdAsync(entry.ImdbId!, tmdbApiKey, includeAdult, cancellationToken).ConfigureAwait(false);
            }

            movie ??= await _tmdbService.FindBestMovieByTitleYearAsync(entry.Title, entry.ReleaseDate.Year, tmdbApiKey, includeAdult, cancellationToken).ConfigureAwait(false);

            if (movie != null)
            {
                resolved.Add(movie);
            }
        }

        var distinctResolved = resolved
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();

        _cache[cacheKey] = (DateTime.UtcNow.AddHours(12), distinctResolved);
        return distinctResolved;
    }

    private async Task<List<string>> GetMonthPagesAsync(int monthsBack, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        var currentUrl = "https://www.dvdsreleasedates.com/digital-releases/";
        for (var i = 0; i <= monthsBack; i++)
        {
            var html = await FetchPageAsync(currentUrl, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                break;
            }

            urls.Add(currentUrl);
            var prev = ExtractPreviousMonthUrl(html);
            if (string.IsNullOrWhiteSpace(prev))
            {
                break;
            }

            currentUrl = prev;
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<string?> FetchPageAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(Constants.UserAgent);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Jfresolve: DVD source returned status {Status} for {Url}", response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Jfresolve: Failed to fetch DVD source page {Url}", url);
            return null;
        }
    }

    private static string? ExtractPreviousMonthUrl(string html)
    {
        var match = PrevMonthRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var href = match.Groups["href"].Value;
        return NormalizeUrl(href);
    }

    private static IReadOnlyList<DvdReleaseMovieEntry> ParseEntries(string html)
    {
        var dateMarkers = DateRegex.Matches(html)
            .Select(m => new { m.Index, Date = ParseDate(m.Value) })
            .Where(x => x.Date.HasValue)
            .Select(x => new DateMarker(x.Index, x.Date!.Value))
            .OrderBy(x => x.Index)
            .ToList();

        var imdbMarkers = ImdbRegex.Matches(html)
            .Select(m => new IndexedImdbId(m.Index, m.Groups["id"].Value))
            .OrderBy(x => x.Index)
            .ToList();

        if (dateMarkers.Count == 0)
        {
            return Array.Empty<DvdReleaseMovieEntry>();
        }

        var entries = new List<DvdReleaseMovieEntry>();
        foreach (Match match in MovieLinkRegex.Matches(html))
        {
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var date = GetLatestDateBeforeIndex(dateMarkers, match.Index);
            if (!date.HasValue)
            {
                continue;
            }

            var imdbId = GetNearestImdbId(imdbMarkers, match.Index);
            entries.Add(new DvdReleaseMovieEntry(title, imdbId, date.Value));
        }

        return entries;
    }

    private static DateTime? ParseDate(string value)
    {
        if (DateTime.TryParseExact(
                value.Trim(),
                "dddd MMMM d, yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.Date;
        }

        return null;
    }

    private static DateTime? GetLatestDateBeforeIndex(IReadOnlyList<DateMarker> markers, int index)
    {
        DateTime? result = null;
        for (var i = 0; i < markers.Count; i++)
        {
            if (markers[i].Index > index)
            {
                break;
            }
            result = markers[i].Date;
        }

        return result;
    }

    private static string? GetNearestImdbId(IReadOnlyList<IndexedImdbId> imdbMarkers, int index)
    {
        const int maxDistance = 1200;
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var marker in imdbMarkers)
        {
            var distance = Math.Abs(marker.Index - index);
            if (distance > maxDistance)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = marker.ImdbId;
            }
        }

        return best;
    }

    private static string NormalizeUrl(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        return $"https://www.dvdsreleasedates.com{href}";
    }

    private sealed record DvdReleaseMovieEntry(string Title, string? ImdbId, DateTime ReleaseDate);
    private sealed record DateMarker(int Index, DateTime Date);
    private sealed record IndexedImdbId(int Index, string ImdbId);
}
