using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.Sources;

/// <summary>
/// What a source can be asked about: a show or a film, and for a show the season and episode.
/// </summary>
internal sealed record VideoQuery
{
    public required string Title { get; init; }

    public string? OriginalTitle { get; init; }

    public int? Year { get; init; }

    public bool IsMovie { get; init; }

    public int? Season { get; init; }

    /// <summary>Gets the season's own name from the library's metadata, such as the title of its arc. <c>null</c> for a plain "Season 2".</summary>
    public string? SeasonName { get; init; }

    public int? Episode { get; init; }

    /// <summary>Gets the episode's number counted from the first episode of the first season.</summary>
    public int? AbsoluteEpisode { get; init; }

    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the day the episode or film first aired.</summary>
    public DateTime? Aired { get; init; }

    /// <summary>Gets the name of the video file, which often names the release.</summary>
    public string? VideoFileName { get; init; }

    public int? AnilistId { get; init; }

    /// <summary>Gets the TMDB id of the show or the film.</summary>
    public int? TmdbId { get; init; }

    /// <summary>Gets the IMDb id of the show or the film, with its "tt".</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets the IMDb id of the episode itself.</summary>
    public string? EpisodeImdbId { get; init; }

    /// <summary>Gets the wanted subtitle language as a two-letter code.</summary>
    public required string Language { get; init; }
}

/// <summary>
/// A source's page for one show or film: on Jimaku an entry, which holds the files of all its episodes.
/// </summary>
internal sealed record SourceEntry
{
    public required string Source { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? EnglishName { get; init; }

    public string? JapaneseName { get; init; }

    public int? AnilistId { get; init; }

    public string? TmdbId { get; init; }

    public bool IsMovie { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Gets the source's own notes on the entry, such as which episodes it holds.</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// One file a source offers: a subtitle, or an archive of subtitles.
/// </summary>
internal sealed record SourceFile
{
    public required string Source { get; init; }

    /// <summary>Gets what names this file at its source: on Jimaku the file name inside its entry.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the entry the file belongs to, for a source that has entries.</summary>
    public string? EntryId { get; init; }

    public required string Name { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? Modified { get; init; }

    /// <summary>Gets where the file is fetched from. It never leaves the server.</summary>
    public string? Url { get; init; }

    /// <summary>Gets a value indicating whether the source itself says the file belongs to the asked episode.</summary>
    public bool ForThisVideo { get; init; }

    public int? Downloads { get; init; }

    public bool HearingImpaired { get; init; }

    public bool MachineTranslated { get; init; }

    public bool Trusted { get; init; }

    /// <summary>Gets the name of the release the uploader made the subtitle for.</summary>
    public string? Release { get; init; }
}

/// <summary>
/// A file as it came from a source.
/// </summary>
internal sealed record DownloadedFile(string Name, byte[] Content);

/// <summary>
/// Why a call to a subtitle source failed.
/// </summary>
internal enum SourceError
{
    NotConfigured,
    Unauthorized,
    RateLimited,
    DownloadLimit,
    NotFound,
    TooLarge,
    Unavailable
}

/// <summary>
/// A failed call to a subtitle source, with a sentence for the page.
/// </summary>
internal sealed class SourceException : Exception
{
    public SourceException(SourceError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public SourceError Error { get; }

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// A place subtitles come from.
/// </summary>
internal interface ISubtitleSource
{
    /// <summary>Gets the id used in settings and answers, such as <c>jimaku</c>.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Gets a value indicating whether the source is switched on and has what it needs.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets a value indicating whether the source keeps one entry per show, whose files have to be
    /// matched to episodes by their names. Otherwise the source is asked for one episode directly.
    /// </summary>
    bool HasEntries { get; }

    bool Covers(string language);

    /// <summary>Finds entries by a title the caller chooses, or by the ids of the query.</summary>
    Task<IReadOnlyList<SourceEntry>> SearchEntriesAsync(VideoQuery video, string? title, CancellationToken cancellationToken);

    /// <summary>Lists the files of an entry, or for a source without entries the files for the video itself.</summary>
    Task<IReadOnlyList<SourceFile>> ListFilesAsync(VideoQuery video, string? entryId, CancellationToken cancellationToken);

    Task<DownloadedFile> DownloadAsync(SourceFile file, CancellationToken cancellationToken);

    /// <summary>Checks the saved key with one small call.</summary>
    Task<string> CheckAsync(CancellationToken cancellationToken);
}
