namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// One search for Jimaku entries. Set exactly the fields the search should use.
/// </summary>
public sealed record JimakuEntryQuery
{
    /// <summary>
    /// Gets the AniList id to look up.
    /// </summary>
    public int? AnilistId { get; init; }

    /// <summary>
    /// Gets the TMDB id to look up, as <c>tv:123</c> or <c>movie:123</c>.
    /// </summary>
    public string? TmdbId { get; init; }

    /// <summary>
    /// Gets the title to search for. Jimaku matches it loosely.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets a value indicating whether to search anime (<c>true</c>, Jimaku's default) or
    /// live action (<c>false</c>). <c>null</c> leaves the choice to Jimaku.
    /// </summary>
    public bool? Anime { get; init; }
}
