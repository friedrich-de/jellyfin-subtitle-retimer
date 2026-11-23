using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Retimer.Jobs;

namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// Why a request was refused, with the HTTP status that says so.
/// </summary>
internal sealed record ServiceError(int Status, string Message);

internal sealed record LanguageView(string Code, string Name);

internal sealed record SourceView(string Id, string Name, bool Ready);

/// <summary>
/// What the page needs to know before it shows anything. It holds no key.
/// </summary>
internal sealed record StatusView
{
    public required string Version { get; init; }

    public required string UserName { get; init; }

    public bool IsAdministrator { get; init; }

    /// <summary>Gets a value indicating whether the user may get, replace and remove subtitles.</summary>
    public bool MayFetch { get; init; }

    public required string Language { get; init; }

    public required string LanguageName { get; init; }

    public IReadOnlyList<LanguageView> Languages { get; init; } = [];

    public IReadOnlyList<SourceView> Sources { get; init; } = [];

    public bool RetimerEnabled { get; init; }

    /// <summary>Gets a value indicating whether Retimer API is switched on and has an address and a key.</summary>
    public bool RetimerReady { get; init; }

    /// <summary>Gets the name of the model that chooses files, or <c>null</c> when none is set up and nothing is picked.</summary>
    public string? ModelName { get; init; }

    /// <summary>Gets today's use against the limits, or <c>null</c> for a user who may not start jobs.</summary>
    public UsageToday? Usage { get; init; }

    public long JobsVersion { get; init; }
}

internal sealed record ShowView
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public int? Year { get; init; }

    public bool IsMovie { get; init; }

    public string? ImageTag { get; init; }

    public int Total { get; init; }

    public int Have { get; init; }

    public string Language { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the show has its own subtitle language.</summary>
    public bool OwnLanguage { get; init; }

    /// <summary>Gets a value indicating whether the show, one of its seasons, or the film is watched.</summary>
    public bool Watched { get; init; }
}

internal sealed record EpisodeView
{
    public Guid Id { get; init; }

    public int? Number { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? ImageTag { get; init; }

    public int? Minutes { get; init; }

    /// <summary>Gets one of <c>none</c>, <c>queued</c>, <c>working</c>, <c>retimed</c>, <c>raw</c>, <c>present</c>, <c>nofit</c>, <c>failed</c>.</summary>
    public string State { get; init; } = "none";

    public string Text { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public string? JobId { get; init; }

    public int Step { get; init; }

    public int Steps { get; init; }

    /// <summary>Gets a value indicating whether the user may stop the job that waits or runs for this video.</summary>
    public bool MayCancel { get; init; }

    /// <summary>Gets a value indicating whether the subtitle was saved as downloaded and can be retimed now.</summary>
    public bool CanRetime { get; init; }

    /// <summary>Gets a value indicating whether the plugin saved the subtitle, so it can remove or replace it.</summary>
    public bool FromPlugin { get; init; }

    /// <summary>Gets a value indicating whether a subtitle can be saved for this video at all.</summary>
    public bool CanSave { get; init; } = true;

    public string? FileName { get; init; }

    public string? Source { get; init; }

    public string? PickedBy { get; init; }

    /// <summary>Gets a value indicating whether watch mode looks for this video's subtitle: its season is watched and it has none.</summary>
    public bool Watched { get; init; }

    /// <summary>Gets until when watch mode looks for it, or <c>null</c> when it looks for as long as the season is watched.</summary>
    public DateTimeOffset? WatchUntil { get; init; }

    /// <summary>Gets a value indicating whether watch mode no longer looks for it, because its days are over.</summary>
    public bool WatchOver { get; init; }
}

internal sealed record SeasonView(int? Number, string Name, IReadOnlyList<EpisodeView> Episodes)
{
    /// <summary>Gets the key that names the season when it is switched to watched: its number, or "-".</summary>
    public string Key { get; init; } = "-";

    /// <summary>Gets a value indicating whether the season is watched, on its own or with the whole show.</summary>
    public bool Watched { get; init; }
}

internal sealed record ShowDetailView
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public int? Year { get; init; }

    public bool IsMovie { get; init; }

    public string? PosterTag { get; init; }

    public string? BackdropTag { get; init; }

    public string Language { get; init; } = string.Empty;

    public string LanguageName { get; init; } = string.Empty;

    public bool OwnLanguage { get; init; }

    /// <summary>Gets the names of the sources that have subtitles in the show's language and are set up.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public IReadOnlyList<SeasonView> Seasons { get; init; } = [];

    /// <summary>Gets a value indicating whether the whole show is watched, seasons added later included, or the film.</summary>
    public bool WatchWhole { get; init; }

    /// <summary>Gets for how many days watch mode looks for an episode's subtitle. 0 means for as long as the season is watched.</summary>
    public int WatchDays { get; init; }

    /// <summary>Gets how many hours pass between two scheduled checks of the watched shows, or <c>null</c> without such a schedule.</summary>
    public double? WatchEveryHours { get; init; }
}

/// <summary>
/// An episode or film of a watched season that has no subtitle yet.
/// </summary>
/// <param name="Id">The video.</param>
/// <param name="Label">Its season, number and name.</param>
/// <param name="State">One of <c>none</c>, <c>nofit</c>, <c>failed</c>, <c>queued</c>, <c>working</c>.</param>
/// <param name="Until">Until when watch mode looks for it, or <c>null</c> for as long as the season is watched.</param>
internal sealed record WatchedVideoView(Guid Id, string Label, string State, DateTimeOffset? Until);

internal sealed record WatchedShowView
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsMovie { get; init; }

    public string? PosterTag { get; init; }

    public string LanguageName { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the whole show is watched, seasons added later included.</summary>
    public bool Whole { get; init; }

    /// <summary>Gets the names of the watched seasons.</summary>
    public IReadOnlyList<string> Seasons { get; init; } = [];

    /// <summary>Gets the names of the seasons that are not watched.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>Gets the videos watch mode still looks for.</summary>
    public IReadOnlyList<WatchedVideoView> Waiting { get; init; } = [];

    /// <summary>Gets how many videos without a subtitle watch mode no longer looks for, because their days are over.</summary>
    public int Over { get; init; }
}

internal sealed record WatchRunView(DateTimeOffset Started, DateTimeOffset? Finished, int Shows, int LookedFor, int JobsStarted, string? Problem);

/// <summary>
/// The "Watching" tab of the activity screen.
/// </summary>
internal sealed record WatchView
{
    public IReadOnlyList<WatchedShowView> Shows { get; init; } = [];

    /// <summary>Gets how the last check of all watched shows went, or <c>null</c> before the first.</summary>
    public WatchRunView? LastCheck { get; init; }

    /// <summary>Gets how many hours pass between two scheduled checks, or <c>null</c> without such a schedule.</summary>
    public double? EveryHours { get; init; }

    /// <summary>Gets a value indicating whether Jellyfin runs the check by itself at all.</summary>
    public bool Scheduled { get; init; }

    public int Days { get; init; }

    public bool NewShows { get; init; }

    /// <summary>Gets a value indicating whether the user may start a check now.</summary>
    public bool MayCheck { get; init; }
}

internal sealed record TriedView(string Name, string Reason);

internal sealed record JobView
{
    public string Id { get; init; } = string.Empty;

    public Guid ItemId { get; init; }

    public Guid ShowId { get; init; }

    public string ShowName { get; init; } = string.Empty;

    public string ItemLabel { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string LanguageName { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public int Step { get; init; }

    public int Steps { get; init; }

    public string StepText { get; init; } = string.Empty;

    public string? StepNote { get; init; }

    public string? Detail { get; init; }

    /// <summary>Gets the job's place in the line: 0 when it runs or is done, 1 for the next one.</summary>
    public int Position { get; init; }

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset? Started { get; init; }

    public DateTimeOffset? Finished { get; init; }

    public string StartedBy { get; init; } = string.Empty;

    public double CostUsd { get; init; }

    public IReadOnlyList<TriedView> Tried { get; init; } = [];

    public bool MayCancel { get; init; }

    public long Version { get; init; }
}

internal sealed record JobsView
{
    public long Version { get; init; }

    public IReadOnlyList<JobView> Jobs { get; init; } = [];

    /// <summary>Gets today's use against the limits, or <c>null</c> for a user who may not start jobs.</summary>
    public UsageToday? Usage { get; init; }
}

internal sealed record StartView
{
    /// <summary>Gets how many jobs were put in the line.</summary>
    public int Started { get; init; }

    /// <summary>Gets how many of the videos already had a job waiting or running.</summary>
    public int Already { get; init; }

    /// <summary>Gets how many of the videos were left out, and why the first of them was.</summary>
    public int Skipped { get; init; }

    public string? SkippedBecause { get; init; }

    /// <summary>Gets why the rest was not started, when a limit was reached.</summary>
    public string? Refused { get; init; }

    public long JobsVersion { get; init; }
}

internal sealed record FileView
{
    public string Source { get; init; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    public string? EntryId { get; init; }

    public string FileId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public long? Size { get; init; }

    public DateTimeOffset? Modified { get; init; }

    public int? Downloads { get; init; }

    public bool HearingImpaired { get; init; }

    public bool MachineTranslated { get; init; }

    /// <summary>Gets why the file is in the list, in a few words.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the name of the model that suggested the file, or <c>null</c>.</summary>
    public string? SuggestedBy { get; init; }

    /// <summary>Gets a value indicating whether the file is for this video, as far as the plugin can tell.</summary>
    public bool Fits { get; init; }

    /// <summary>Gets why the file was rejected when it was tried before, or <c>null</c>.</summary>
    public string? Tried { get; init; }

    /// <summary>Gets why a file of the same release was rejected for another episode of the show, or <c>null</c>.</summary>
    public string? RejectedBefore { get; init; }

    /// <summary>Gets a value indicating whether this is the file the saved subtitle came from.</summary>
    public bool Current { get; init; }

    /// <summary>Gets why the file cannot be used, or <c>null</c>.</summary>
    public string? Unusable { get; init; }
}

internal sealed record SourceReportView(string Source, string Name, string? EntryId, string? EntryName, int Files, string? MatchedBy, string? Problem, IReadOnlyList<string>? EntryNames = null);

internal sealed record FilesView
{
    public Guid ItemId { get; init; }

    public Guid ShowId { get; init; }

    public string ShowName { get; init; } = string.Empty;

    public string ItemLabel { get; init; } = string.Empty;

    public bool IsMovie { get; init; }

    public string Language { get; init; } = string.Empty;

    public string LanguageName { get; init; } = string.Empty;

    public IReadOnlyList<FileView> Files { get; init; } = [];

    public IReadOnlyList<SourceReportView> Sources { get; init; } = [];

    /// <summary>Gets the name of the model that ranked the files, or <c>null</c> when the rules did.</summary>
    public string? Model { get; init; }

    public string? ModelProblem { get; init; }

    public bool RetimerReady { get; init; }

    /// <summary>Gets a value indicating whether some source keeps entries, so "Wrong show?" can search them.</summary>
    public bool CanSearchEntries { get; init; }
}

internal sealed record EntryView(string Source, string SourceName, string Id, string Name, string? EnglishName, string? JapaneseName, bool Current);
