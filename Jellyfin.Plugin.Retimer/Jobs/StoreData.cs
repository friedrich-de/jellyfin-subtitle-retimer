using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Retimer.Jobs;

/// <summary>
/// Everything the plugin remembers between restarts, kept as one JSON file in Jellyfin's data folder.
/// </summary>
internal sealed class StoreData
{
    /// <summary>Gets or sets what is known about each show or film, by its Jellyfin id.</summary>
    public Dictionary<string, ShowRecord> Shows { get; set; } = [];

    /// <summary>Gets or sets what the plugin did for each video and language, by "id:language".</summary>
    public Dictionary<string, ItemRecord> Items { get; set; } = [];

    /// <summary>Gets or sets the finished jobs, oldest first.</summary>
    public List<Job> Jobs { get; set; } = [];

    /// <summary>Gets or sets the jobs that wait or run, so a restart can say what it interrupted.</summary>
    public List<Job> Pending { get; set; } = [];

    /// <summary>Gets or sets the highest change counter the jobs reached, so it keeps growing over a restart.</summary>
    public long JobsVersion { get; set; }

    /// <summary>Gets or sets what was used on each day, by its date (UTC).</summary>
    public Dictionary<string, DayUsage> Usage { get; set; } = [];
}

internal sealed class ShowRecord
{
    /// <summary>Gets or sets the subtitle language of this show, when it differs from the server's.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the entry chosen at each source, by "source|season|language".</summary>
    public Dictionary<string, EntryMatch> Matches { get; set; } = [];

    /// <summary>Gets or sets the model's episode-to-file lists, by "source|entry|season|language".</summary>
    public Dictionary<string, FileMapping> Mappings { get; set; } = [];

    /// <summary>
    /// Gets or sets the kinds of file that were rejected for an episode of this show for a reason that holds
    /// for the other episodes too, such as a second language. Oldest first.
    /// </summary>
    public List<RejectedKind> Rejected { get; set; } = [];

    /// <summary>A copy that is safe to read while the stored record changes. The matches, mappings and rejections themselves are never changed in place.</summary>
    public ShowRecord Snapshot() => new()
    {
        Language = Language,
        Matches = new Dictionary<string, EntryMatch>(Matches, StringComparer.Ordinal),
        Mappings = new Dictionary<string, FileMapping>(Mappings, StringComparer.Ordinal),
        Rejected = [.. Rejected]
    };
}

internal sealed class RejectedKind
{
    public string Source { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's name with every number left out, which the files of one release share.</summary>
    public string Pattern { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}

internal sealed class EntryMatch
{
    /// <summary>Gets or sets the entry's id at the source. Empty when the search found no entry.</summary>
    public string EntryId { get; set; } = string.Empty;

    public string EntryName { get; set; } = string.Empty;

    /// <summary>Gets or sets who chose: <c>model</c>, <c>rules</c> or <c>user</c>.</summary>
    public string By { get; set; } = "rules";

    /// <summary>Gets or sets the name of the model that chose.</summary>
    public string? Model { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset At { get; set; }
}

internal sealed class FileMapping
{
    /// <summary>Gets or sets a fingerprint of the file list and the episodes the model saw.</summary>
    public string ListHash { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets the files for each episode number, best first.</summary>
    public Dictionary<int, MappedEpisode> Episodes { get; set; } = [];
}

internal sealed class MappedEpisode
{
    public List<string> Files { get; set; } = [];

    public string? Reason { get; set; }
}

internal sealed class ItemRecord
{
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets where Jellyfin saved the subtitle. Empty when nothing is saved.</summary>
    public string SavedPath { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FileId { get; set; } = string.Empty;

    public string? EntryId { get; set; }

    public bool Retimed { get; set; }

    /// <summary>Gets or sets one sentence on the result, such as why the subtitle was not retimed.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets who picked the file: a model's name, "the rules" or "you".</summary>
    public string? PickedBy { get; set; }

    public DateTimeOffset? SavedAt { get; set; }

    public string? SavedBy { get; set; }

    /// <summary>Gets or sets the name of the downloaded file the plugin kept, so it can be retimed later.</summary>
    public string? KeptDownload { get; set; }

    /// <summary>Gets or sets a value indicating whether the last search found no file that fits.</summary>
    public bool NoFit { get; set; }

    /// <summary>Gets or sets why the last job failed.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the files that were tried and rejected.</summary>
    public List<TriedFile> Tried { get; set; } = [];

    /// <summary>A copy that is safe to read while the stored record changes.</summary>
    public ItemRecord Snapshot()
    {
        var copy = (ItemRecord)MemberwiseClone();
        copy.Tried = [.. Tried];
        return copy;
    }
}

internal sealed class TriedFile
{
    public string Source { get; set; } = string.Empty;

    public string FileId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}

internal sealed class DayUsage
{
    /// <summary>Gets or sets how many jobs were started.</summary>
    public int Jobs { get; set; }

    /// <summary>Gets or sets what the services reported as cost, in US dollars.</summary>
    public double CostUsd { get; set; }
}
