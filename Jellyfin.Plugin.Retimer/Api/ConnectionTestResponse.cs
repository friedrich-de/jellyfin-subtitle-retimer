namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// The outcome of a connection test from the settings page.
/// </summary>
public sealed class ConnectionTestResponse
{
    /// <summary>
    /// Gets a value indicating whether the connection works with the saved settings.
    /// </summary>
    public required bool Ok { get; init; }

    /// <summary>
    /// Gets a short machine-readable outcome, such as <c>Ok</c> or <c>Unauthorized</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets one sentence for the administrator.
    /// </summary>
    public required string Message { get; init; }
}
