using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jfresolve.Music;

/// <summary>
/// Spotify track metadata (from public web API or reverse-engineered endpoints).
/// </summary>
public class SpotifyTrackMetadata
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("artists")]
    public List<SpotifyArtistRef> Artists { get; set; } = new();

    [JsonPropertyName("album")]
    public SpotifyAlbumRef? Album { get; set; }

    [JsonPropertyName("track_number")]
    public int TrackNumber { get; set; }

    [JsonPropertyName("disc_number")]
    public int DiscNumber { get; set; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("external_urls")]
    public SpotifyExternalUrls? ExternalUrls { get; set; }

    /// <summary>
    /// Primary artist name for display and filename.
    /// </summary>
    public string GetArtistName()
    {
        return Artists?.Count > 0 ? Artists[0].Name ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// All artist names joined (e.g. "Artist A, Artist B").
    /// </summary>
    public string GetAllArtistNames()
    {
        if (Artists == null || Artists.Count == 0) return string.Empty;
        return string.Join(", ", Artists.ConvertAll(a => a.Name ?? string.Empty));
    }

    public string GetAlbumName() => Album?.Name ?? string.Empty;
    public string GetReleaseYear()
    {
        var date = Album?.ReleaseDate;
        if (string.IsNullOrWhiteSpace(date)) return string.Empty;
        if (date.Length >= 4) return date.Substring(0, 4);
        return date;
    }

    /// <summary>
    /// Best available cover image URL (prefer largest).
    /// </summary>
    public string? GetCoverImageUrl()
    {
        var images = Album?.Images;
        if (images == null || images.Count == 0) return null;
        return images[0].Url ?? (images.Count > 1 ? images[^1].Url : null);
    }
}

public class SpotifyArtistRef
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class SpotifyAlbumRef
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("images")]
    public List<SpotifyImage>? Images { get; set; }
}

public class SpotifyImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public class SpotifyExternalUrls
{
    [JsonPropertyName("spotify")]
    public string? Spotify { get; set; }
}

/// <summary>
/// Wrapper for Spotify API search response (tracks).
/// </summary>
public class SpotifySearchResponse
{
    [JsonPropertyName("tracks")]
    public SpotifyTracksResult? Tracks { get; set; }
}

public class SpotifyTracksResult
{
    [JsonPropertyName("items")]
    public List<SpotifyTrackMetadata>? Items { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}
