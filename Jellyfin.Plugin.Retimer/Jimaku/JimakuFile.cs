using System;

namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// A file of a Jimaku entry: a subtitle, or an archive of subtitles.
/// Jimaku gives a name, a size and a date, and nothing that ties the file to a video.
/// </summary>
public sealed class JimakuFile
{
    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the download address.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the date the file was last modified.
    /// </summary>
    public DateTimeOffset LastModified { get; set; }
}
