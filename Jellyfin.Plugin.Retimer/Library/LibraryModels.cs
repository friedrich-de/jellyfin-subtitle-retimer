using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;

namespace Jellyfin.Plugin.Retimer.Library;

/// <summary>
/// Who is calling, as far as the plugin's rules need to know.
/// </summary>
internal sealed record Caller(Guid UserId, string Name, bool IsAdministrator, bool MayManageSubtitles);

/// <summary>
/// A library: its kind is <c>tvshows</c>, <c>movies</c> or <c>mixed</c>.
/// </summary>
internal sealed record LibraryInfo(Guid Id, string Name, string Kind);

/// <summary>
/// A show or a film in the grid, with how many of its videos have a subtitle in its language.
/// </summary>
internal sealed record ShowSummary
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public int? Year { get; init; }

    public bool IsMovie { get; init; }

    /// <summary>Gets a value that changes when the poster changes, or <c>null</c> without a poster.</summary>
    public string? ImageTag { get; init; }

    public int Total { get; init; }

    public int Have { get; init; }
}

/// <summary>
/// A subtitle track of a video: inside the file, or a file next to it.
/// </summary>
internal sealed record TrackInfo
{
    public int Index { get; init; }

    public string? Language { get; init; }

    public string? Title { get; init; }

    public string Codec { get; init; } = string.Empty;

    public bool IsExternal { get; init; }

    public bool IsText { get; init; }

    public bool IsDefault { get; init; }

    public bool IsForced { get; init; }

    public string? Path { get; init; }
}

/// <summary>
/// An episode or a film, with what the plugin needs to find and save a subtitle for it.
/// </summary>
internal sealed record VideoInfo
{
    public required Guid Id { get; init; }

    /// <summary>Gets the id of the show, or of the film itself.</summary>
    public required Guid ShowId { get; init; }

    public required string ShowName { get; init; }

    public string? ShowOriginalName { get; init; }

    public int? Year { get; init; }

    public bool IsMovie { get; init; }

    public int? Season { get; init; }

    /// <summary>Gets the season's own name, such as the title of its arc, or <c>null</c> for a plain "Season 2".</summary>
    public string? SeasonName { get; init; }

    public int? Episode { get; init; }

    public int? AbsoluteEpisode { get; init; }

    /// <summary>Gets the day the episode or film first aired.</summary>
    public DateTime? Aired { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? FileName { get; init; }

    /// <summary>Gets a value indicating whether the video is a file the server can read, not a stream from elsewhere.</summary>
    public bool IsLocalFile { get; init; }

    public long? RunTimeTicks { get; init; }

    public string? ImageTag { get; init; }

    public int? AnilistId { get; init; }

    public int? TmdbId { get; init; }

    public string? ImdbId { get; init; }

    public string? EpisodeImdbId { get; init; }

    public IReadOnlyList<TrackInfo> Subtitles { get; init; } = [];

    public IReadOnlyList<string> AudioLanguages { get; init; } = [];

    /// <summary>Gets when the video came into the library, as far as Jellyfin knows.</summary>
    public DateTimeOffset? Added { get; init; }

    /// <summary>
    /// Gets a value indicating whether Jellyfin has read the video's streams yet. Until it has, the video lists
    /// no subtitle track, also when its file holds one.
    /// </summary>
    public bool StreamsRead { get; init; } = true;
}

internal sealed record SeasonDetails(int? Number, string Name, IReadOnlyList<VideoInfo> Episodes);

internal sealed record ShowDetails
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public int? Year { get; init; }

    public bool IsMovie { get; init; }

    public string? PosterTag { get; init; }

    public string? BackdropTag { get; init; }

    public IReadOnlyList<SeasonDetails> Seasons { get; init; } = [];
}

/// <summary>
/// How saving a subtitle through Jellyfin ended.
/// </summary>
/// <param name="Path">Where Jellyfin put the file, when the plugin could tell.</param>
/// <param name="Listed">Whether the video lists the new subtitle track, so players offer it.</param>
internal sealed record SaveResult(string? Path, bool Listed);

/// <summary>
/// Everything the plugin asks of Jellyfin's library.
/// </summary>
internal interface IVideoLibrary
{
    /// <summary>The libraries of shows and films the user may see and the settings do not hide.</summary>
    IReadOnlyList<LibraryInfo> GetLibraries(Guid userId);

    /// <summary>Every library of shows and films, for the settings.</summary>
    IReadOnlyList<LibraryInfo> GetAllLibraries();

    IReadOnlyList<ShowSummary> GetShows(Guid userId, Guid libraryId, Func<Guid, string> languageOf);

    ShowDetails? GetShow(Guid userId, Guid showId);

    /// <summary>One video. An empty user id means the server itself asks.</summary>
    VideoInfo? GetVideo(Guid userId, Guid itemId);

    /// <summary>Whether the user may see the show, film or episode.</summary>
    bool CanSee(Guid userId, Guid itemId);

    /// <summary>The episodes of the video's season, for a model that matches files to all of them at once.</summary>
    IReadOnlyList<SeasonEpisode> GetSeasonEpisodes(Guid itemId);

    /// <summary>
    /// Reads the video's text subtitle tracks as files, leaving out the given external files. Tracks in the
    /// given language and English ones come first, so they are among those read. The folder is for work files.
    /// </summary>
    Task<IReadOnlyList<ReferenceUpload>> ReadTextTracksAsync(Guid itemId, string language, int max, IReadOnlyCollection<string> skipPaths, string scratchDirectory, CancellationToken cancellationToken);

    /// <summary>Reads the first picture subtitle track (PGS) inside the video, or <c>null</c>.</summary>
    Task<ReferenceUpload?> ReadPictureTrackAsync(Guid itemId, string outputDirectory, CancellationToken cancellationToken);

    /// <summary>Cuts the audio out of the video as a small mono Opus file. Returns <c>false</c> when that fails.</summary>
    Task<bool> ExtractAudioAsync(Guid itemId, string? language, string outputPath, CancellationToken cancellationToken);

    Task<SaveResult> SaveSubtitleAsync(Guid itemId, string language, string format, byte[] content, CancellationToken cancellationToken);

    /// <summary>Removes a subtitle file the plugin saved for the video.</summary>
    Task<bool> RemoveSubtitleAsync(Guid itemId, string path, CancellationToken cancellationToken);

    /// <summary>Tells the library that the videos of a show changed, so cached counts are dropped.</summary>
    void Invalidate();

    /// <summary>The shows (series, not films) of the libraries the page shows that came into the library at or after the given time.</summary>
    IReadOnlyList<Guid> GetShowsAddedSince(DateTimeOffset since);
}
