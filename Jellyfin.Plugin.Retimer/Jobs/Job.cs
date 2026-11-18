using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace Jellyfin.Plugin.Retimer.Jobs;

/// <summary>
/// Where a job stands.
/// </summary>
internal static class JobState
{
    public const string Queued = "queued";
    public const string Working = "working";

    /// <summary>The subtitle was retimed and saved.</summary>
    public const string Saved = "saved";

    /// <summary>The subtitle was saved as downloaded.</summary>
    public const string SavedRaw = "raw";

    /// <summary>No file fits the video.</summary>
    public const string NoFit = "nofit";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsActive(string state) => state is Queued or Working;
}

/// <summary>
/// What a job is asked to do.
/// </summary>
internal static class JobKind
{
    /// <summary>Search, pick, check, retime when Retimer API is on, save.</summary>
    public const string Get = "get";

    /// <summary>Like <see cref="Get"/>, and the subtitle is saved as downloaded.</summary>
    public const string GetPlain = "plain";

    /// <summary>Retime the download the plugin kept for a subtitle it saved as downloaded.</summary>
    public const string Retime = "retime";

    /// <summary>Use the file the user chose in the picker.</summary>
    public const string Chosen = "chosen";

    /// <summary>Like <see cref="Get"/>, and files that were saved or tried before are skipped.</summary>
    public const string Another = "another";
}

/// <summary>
/// One subtitle to get for one video, as the activity list shows it.
/// </summary>
internal sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public Guid ItemId { get; set; }

    public Guid ShowId { get; set; }

    public string ShowName { get; set; } = string.Empty;

    /// <summary>Gets or sets the line under the show's name, such as "Season 1 · 5. Howl of the Mad Beast".</summary>
    public string ItemLabel { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string Kind { get; set; } = JobKind.Get;

    /// <summary>Gets or sets a value indicating whether the user asked to retime (for a chosen file).</summary>
    public bool Retime { get; set; } = true;

    /// <summary>Gets or sets the chosen file's source, for <see cref="JobKind.Chosen"/>.</summary>
    public string? ChosenSource { get; set; }

    public string? ChosenEntryId { get; set; }

    public string? ChosenFileId { get; set; }

    public string? ChosenFileName { get; set; }

    public string State { get; set; } = JobState.Queued;

    /// <summary>Gets or sets the number of the current step, from 1.</summary>
    public int Step { get; set; }

    public int Steps { get; set; }

    public string StepText { get; set; } = string.Empty;

    /// <summary>Gets or sets a short addition to the step, such as "Haiku 5.5 is reading the file names".</summary>
    public string? StepNote { get; set; }

    /// <summary>Gets or sets one sentence on how the job ended.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? Started { get; set; }

    public DateTimeOffset? Finished { get; set; }

    public Guid StartedById { get; set; }

    public string StartedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets what the services reported as cost for this job, in US dollars.</summary>
    public double CostUsd { get; set; }

    public List<TriedFile> Tried { get; set; } = [];

    /// <summary>Gets a counter that grows with every change, so the page can ask for what is new.</summary>
    [JsonIgnore]
    public long Version { get; set; }

    [JsonIgnore]
    public CancellationTokenSource? Cancellation { get; set; }

    public Job Clone() => (Job)MemberwiseClone();
}
