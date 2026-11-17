namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// What every signed-in user may know about the plugin. It never carries a key.
/// </summary>
public sealed class StatusResponse
{
    /// <summary>
    /// Gets the plugin's version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets a value indicating whether a Jimaku API key is set.
    /// </summary>
    public required bool JimakuConfigured { get; init; }

    /// <summary>
    /// Gets a value indicating whether a retimer address and key are set.
    /// </summary>
    public required bool RetimerConfigured { get; init; }

    /// <summary>
    /// Gets a value indicating whether the caller is an administrator, who may open the settings.
    /// </summary>
    public required bool IsAdministrator { get; init; }
}
