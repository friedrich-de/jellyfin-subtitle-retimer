namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// Why a call to Jimaku failed.
/// </summary>
public enum JimakuError
{
    /// <summary>
    /// No Jimaku API key is set in the plugin's settings. Nothing was sent.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// Jimaku rejected the API key.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Jimaku's rate limit was hit. <see cref="JimakuException.RetryAfter"/> says when to try again.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The entry does not exist.
    /// </summary>
    NotFound,

    /// <summary>
    /// Jimaku rejected the request as invalid.
    /// </summary>
    BadRequest,

    /// <summary>
    /// Jimaku could not be reached, or answered with something unexpected.
    /// </summary>
    Unavailable
}
