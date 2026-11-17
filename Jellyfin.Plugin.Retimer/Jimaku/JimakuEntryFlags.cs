namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// The flags of a <see cref="JimakuEntry"/>.
/// </summary>
public sealed class JimakuEntryFlags
{
    /// <summary>
    /// Gets or sets a value indicating whether the entry is an anime.
    /// </summary>
    public bool Anime { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entry is a movie.
    /// </summary>
    public bool Movie { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entry is meant for adult audiences.
    /// </summary>
    public bool Adult { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entry comes from an external source.
    /// </summary>
    public bool External { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jimaku's editors have not checked the entry yet.
    /// </summary>
    public bool Unverified { get; set; }
}
