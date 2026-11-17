namespace Jellyfin.Plugin.Retimer.Retimer;

/// <summary>
/// The outcome of checking the retimer's address and key.
/// </summary>
public enum RetimerConnectionStatus
{
    /// <summary>
    /// The retimer answered and accepted the key.
    /// </summary>
    Ok,

    /// <summary>
    /// The address or the key is missing in the plugin's settings. Nothing was sent.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The address is not a usable http or https address. Nothing was sent.
    /// </summary>
    InvalidAddress,

    /// <summary>
    /// Nothing answered at the address, or what answered is not a retimer.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The retimer answered and rejected the key.
    /// </summary>
    Unauthorized
}
