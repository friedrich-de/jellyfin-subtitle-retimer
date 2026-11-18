using System;

namespace Jellyfin.Plugin.Retimer.Api.Models;

/// <summary>
/// Asks the plugin to get subtitles for videos.
/// </summary>
public sealed class StartJobsRequest
{
    /// <summary>
    /// Gets or sets the ids of the episodes or films.
    /// </summary>
    public Guid[]? ItemIds { get; set; }

    /// <summary>
    /// Gets or sets what to do: <c>get</c> (search, pick, retime when Retimer API is on, save), <c>plain</c> (save as downloaded),
    /// <c>retime</c> (retime a subtitle that was saved as downloaded), <c>chosen</c> (use the named file) or
    /// <c>another</c> (like get, skipping the files used or tried before).
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a chosen file is retimed. Only read for <c>chosen</c>.
    /// </summary>
    public bool Retime { get; set; } = true;

    /// <summary>
    /// Gets or sets the source of the chosen file.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the source's entry that holds the chosen file, for a source with entries.
    /// </summary>
    public string? EntryId { get; set; }

    /// <summary>
    /// Gets or sets the chosen file's id at its source.
    /// </summary>
    public string? FileId { get; set; }
}
