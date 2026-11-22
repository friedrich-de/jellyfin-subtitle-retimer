namespace Jellyfin.Plugin.Retimer.Api.Models;

/// <summary>
/// Switches watch mode on or off for a show, one of its seasons, or a film.
/// </summary>
public sealed class WatchRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether the show or season is watched.
    /// </summary>
    public bool Watch { get; set; }

    /// <summary>
    /// Gets or sets the season, by the key the show's page gives it (its number, or "-"). Without one the
    /// whole show is switched, seasons added later included.
    /// </summary>
    public string? Season { get; set; }
}
