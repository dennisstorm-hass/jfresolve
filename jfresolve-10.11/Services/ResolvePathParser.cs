using System;
using Microsoft.AspNetCore.WebUtilities;

namespace Jfresolve.Services;

public readonly record struct ParsedResolvePath(
    string Type,
    string Id,
    string? Season,
    string? Episode,
    string? Quality,
    int? Index);

/// <summary>
/// Parses Jfresolve resolve URLs stored on library items into stream resolution parameters.
/// </summary>
public static class ResolvePathParser
{
    private const string ResolveMarker = "/plugins/jfresolve/resolve/";

    public static bool TryParse(string? path, out ParsedResolvePath result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            path = path[5..];

        var lower = path.ToLowerInvariant();
        var markerIndex = lower.IndexOf(ResolveMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var afterMarker = path[(markerIndex + ResolveMarker.Length)..];
        afterMarker = afterMarker.Replace("/stream.m3u8", string.Empty, StringComparison.OrdinalIgnoreCase);

        var queryIndex = afterMarker.IndexOf('?');
        var pathPart = queryIndex >= 0 ? afterMarker[..queryIndex] : afterMarker;
        var query = queryIndex >= 0 ? afterMarker[queryIndex..] : string.Empty;

        var segments = pathPart.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        var type = segments[0];
        var id = segments[1].Split('?', '/')[0];
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
            return false;

        string? season = null;
        string? episode = null;
        string? quality = null;
        int? index = null;

        if (!string.IsNullOrEmpty(query))
        {
            var parsed = QueryHelpers.ParseQuery(query);
            if (parsed.TryGetValue("season", out var seasonValues))
                season = seasonValues.ToString();
            if (parsed.TryGetValue("episode", out var episodeValues))
                episode = episodeValues.ToString();
            if (parsed.TryGetValue("quality", out var qualityValues))
                quality = qualityValues.ToString();
            if (parsed.TryGetValue("index", out var indexValues)
                && int.TryParse(indexValues.ToString(), out var parsedIndex))
            {
                index = parsedIndex;
            }
        }

        result = new ParsedResolvePath(type, id, season, episode, quality, index);
        return true;
    }
}
