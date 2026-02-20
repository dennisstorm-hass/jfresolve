using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Music;

namespace Jfresolve.Services;

/// <summary>
/// Searches for a direct FLAC download URL given track metadata.
/// Implementations may use public/unauthenticated APIs (e.g. Tidal/Qobuz wrappers) or configurable endpoints.
/// </summary>
public interface IFlacSearchService
{
    /// <summary>
    /// Tries to find a direct FLAC (or preferred quality) download URL for the given track.
    /// Returns null if no match or not configured.
    /// </summary>
    Task<string?> FindFlacUrlAsync(SpotifyTrackMetadata metadata, CancellationToken ct = default);
}
