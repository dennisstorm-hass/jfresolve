namespace Jfresolve.Configuration;

/// <summary>
/// Per-user playback preferences. Null values mean "use global default".
/// </summary>
public class UserPlaybackPrefs
{
    /// <summary>
    /// When true, prefer HDR over Dolby Vision at the same resolution.
    /// When null, use the global plugin default (PreferHdrOverDolbyVision).
    /// </summary>
    public bool? PreferHdrOverDolbyVision { get; set; }
}
