using System;

namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// A Jimaku entry: one directory of subtitle files for a series or a movie.
/// </summary>
public sealed class JimakuEntry
{
    /// <summary>
    /// Gets or sets the entry's id on Jimaku.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the romaji name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the English name.
    /// </summary>
    public string? EnglishName { get; set; }

    /// <summary>
    /// Gets or sets the Japanese name, in kanji and kana.
    /// </summary>
    public string? JapaneseName { get; set; }

    /// <summary>
    /// Gets or sets the AniList id.
    /// </summary>
    public int? AnilistId { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id in Jimaku's form, <c>tv:123</c> or <c>movie:123</c>.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the date of the newest uploaded file.
    /// </summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// Gets or sets the entry's flags.
    /// </summary>
    public JimakuEntryFlags Flags { get; set; } = new();

    /// <summary>
    /// Gets or sets notes from Jimaku's editors.
    /// </summary>
    public string? Notes { get; set; }
}
