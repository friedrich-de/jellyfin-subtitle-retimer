namespace Jellyfin.Plugin.Retimer.Api.Models;

/// <summary>
/// Says which entry of a source belongs to a video's show and season.
/// </summary>
public sealed class PinEntryRequest
{
    /// <summary>
    /// Gets or sets the source's id.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the entry's id, as the search returned it.
    /// </summary>
    public string? EntryId { get; set; }
}
