using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Retimer.Sources;

/// <summary>
/// All subtitle sources the plugin knows.
/// </summary>
internal sealed class SubtitleSources
{
    private readonly IReadOnlyList<ISubtitleSource> _sources;

    public SubtitleSources(IEnumerable<ISubtitleSource> sources)
    {
        _sources = sources.ToList();
    }

    public IReadOnlyList<ISubtitleSource> All => _sources;

    /// <summary>The sources that are switched on, set up, and have subtitles in the language.</summary>
    public IReadOnlyList<ISubtitleSource> ReadyFor(string language) =>
        _sources.Where(source => source.IsReady && source.Covers(language)).ToList();

    public ISubtitleSource? Find(string? id) =>
        _sources.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));
}
