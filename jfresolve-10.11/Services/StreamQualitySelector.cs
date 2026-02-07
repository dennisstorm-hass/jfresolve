using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

/// <summary>
/// Service for selecting streams based on quality preferences
/// Extracted from JfresolveApiController for better testability and maintainability
/// </summary>
public class StreamQualitySelector
{
    private readonly ILogger<StreamQualitySelector> _logger;

    public StreamQualitySelector(ILogger<StreamQualitySelector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Selects the best stream from the available streams based on preferred quality
    /// </summary>
    public JsonElement? SelectStreamByQuality(
        JsonElement streams,
        string preferredQuality,
        string? requestedQuality = null,
        int? requestedIndex = null,
        bool preferHdrOverDolbyVision = false)
    {
        var streamArray = streams.EnumerateArray().ToList();
        if (streamArray.Count == 0)
            return null;

        // If a specific quality is requested (Virtual Versioning), filter and pick by index
        if (!string.IsNullOrEmpty(requestedQuality))
        {
            var filteredStreams = FilterStreamsByQuality(streamArray, requestedQuality, preferHdrOverDolbyVision);
            if (filteredStreams.Count > 0)
            {
                var idx = requestedIndex ?? 0;
                // Fallback to last available if index is too high
                if (idx >= filteredStreams.Count)
                {
                    _logger.LogWarning("Jfresolve: Requested index {Index} out of range for quality {Quality}. Falling back to index {FallbackIndex}.",
                        idx, requestedQuality, filteredStreams.Count - 1);
                    idx = filteredStreams.Count - 1;
                }
                _logger.LogInformation("Jfresolve: Selected quality {Quality} stream at index {Index}", requestedQuality, idx);
                return filteredStreams[idx];
            }

            _logger.LogWarning("Jfresolve: Specifically requested quality {Quality} not found, falling back to discovery logic", requestedQuality);
        }

        // Discovery logic (Discovery mode or fallback)
        if (preferredQuality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return SelectHighestQualityStream(streamArray, preferHdrOverDolbyVision);
        }

        // Try to find exact match for preferred quality
        var matchedStream = FindStreamByQuality(streamArray, preferredQuality, preferHdrOverDolbyVision);
        if (matchedStream != null)
        {
            _logger.LogInformation("Jfresolve: Selected {Quality} stream (discovery match)", preferredQuality);
            return matchedStream;
        }

        // Fallback: select highest quality if preferred not found
        _logger.LogInformation("Jfresolve: Preferred quality {Quality} not found, selecting highest available", preferredQuality);
        return SelectHighestQualityStream(streamArray, preferHdrOverDolbyVision);
    }

    /// <summary>
    /// Filters streams list to only those containing the specified quality indicators.
    /// When preferHdrOverDolbyVision is true, sorts so HDR (non-DV) streams come before Dolby Vision.
    /// </summary>
    public List<JsonElement> FilterStreamsByQuality(List<JsonElement> streams, string quality, bool preferHdrOverDolbyVision = false)
    {
        var indicators = GetQualityIndicators(quality);
        var results = new List<JsonElement>();

        foreach (var stream in streams)
        {
            var text = GetStreamText(stream);
            if (indicators.Any(ind => text.Contains(ind, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(stream);
            }
        }

        if (preferHdrOverDolbyVision && results.Count > 1)
        {
            results = results.OrderBy(s => StreamContainsDolbyVision(s) ? 1 : 0).ToList();
        }

        return results;
    }

    /// <summary>
    /// Finds a stream matching the specified quality preference.
    /// When preferHdrOverDolbyVision is true, prefers HDR (non-DV) over Dolby Vision at the same resolution.
    /// </summary>
    public JsonElement? FindStreamByQuality(List<JsonElement> streams, string quality, bool preferHdrOverDolbyVision = false)
    {
        var qualityIndicators = GetQualityIndicators(quality);
        JsonElement? dvFallback = null;

        foreach (var stream in streams)
        {
            var streamText = GetStreamText(stream);

            foreach (var indicator in qualityIndicators)
            {
                if (!streamText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (preferHdrOverDolbyVision)
                {
                    if (StreamContainsDolbyVision(stream))
                    {
                        dvFallback ??= stream;
                        continue;
                    }
                    return stream; // HDR or SDR, avoid DV when possible
                }
                return stream;
            }
        }

        return dvFallback ?? null;
    }

    /// <summary>
    /// Selects the highest quality stream from the available streams
    /// Priority order: 4K/2160p > 1440p > 1080p > 720p > 480p > first available
    /// When preferHdrOverDolbyVision is true, prefers HDR (non-DV) over Dolby Vision at each tier.
    /// </summary>
    public JsonElement SelectHighestQualityStream(List<JsonElement> streams, bool preferHdrOverDolbyVision = false)
    {
        var qualityPriority = Constants.QualityPriority;

        foreach (var quality in qualityPriority)
        {
            var stream = FindStreamByQuality(streams, quality, preferHdrOverDolbyVision);
            if (stream != null)
            {
                _logger.LogInformation("Jfresolve: Auto-selected {Quality} stream (highest available)", quality);
                return stream.Value;
            }
        }

        _logger.LogInformation("Jfresolve: No quality indicators found, using first stream");
        return streams[0];
    }

    /// <summary>
    /// Gets quality indicators for a given quality preference
    /// Maps user-friendly names to various formats used by different addons
    /// </summary>
    public string[] GetQualityIndicators(string quality)
    {
        return quality.ToLowerInvariant() switch
        {
            "4k" => new[] { "4k", "2160p", "2160" },
            "1440p" => new[] { "1440p", "1440" },
            "1080p" => new[] { "1080p", "1080" },
            "720p" => new[] { "720p", "720" },
            "480p" => new[] { "480p", "480" },
            _ => new[] { quality.ToLowerInvariant() }
        };
    }

    /// <summary>
    /// Extracts searchable text from a stream object (name + title fields)
    /// </summary>
    public string GetStreamText(JsonElement stream)
    {
        var text = string.Empty;

        if (stream.TryGetProperty("name", out var name))
        {
            text += name.GetString() + " ";
        }

        if (stream.TryGetProperty("title", out var title))
        {
            text += title.GetString();
        }

        return text;
    }

    /// <summary>
    /// Returns true if the stream name/title indicates Dolby Vision (e.g. "Dolby Vision", "DV", "DoVi")
    /// </summary>
    private static bool StreamContainsDolbyVision(JsonElement stream)
    {
        var text = GetStreamTextPublic(stream);
        return text.Contains("dolby vision", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" dolby vision", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".dv.", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" dv ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dovi", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStreamTextPublic(JsonElement stream)
    {
        var text = string.Empty;
        if (stream.TryGetProperty("name", out var name))
            text += name.GetString() + " ";
        if (stream.TryGetProperty("title", out var title))
            text += title.GetString();
        return text;
    }
}
