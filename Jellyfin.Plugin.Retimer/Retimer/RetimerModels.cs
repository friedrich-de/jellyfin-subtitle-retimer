using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.Retimer;

/// <summary>
/// What the Retimer API at the configured address can do beyond the basics.
/// </summary>
internal sealed record RetimerFeatures
{
    /// <summary>Gets a value indicating whether a job may carry several reference subtitles.</summary>
    public bool ReferenceTracks { get; init; }

    public bool RemoveSignsAndCredits { get; init; }

    public bool Ocr { get; init; }

    public int MaxReferenceSubtitles { get; init; } = 1;

    public long RequestBytes { get; init; } = 95L * 1024 * 1024;

    public long AudioBytes { get; init; } = 90L * 1024 * 1024;

    public double AudioMinutes { get; init; } = 240;

    public long ImageSubtitleBytes { get; init; } = 90L * 1024 * 1024;
}

/// <summary>
/// A subtitle track of the video, sent as something the subtitle can be compared with.
/// </summary>
/// <param name="FileName">A file name with the track's format.</param>
/// <param name="Content">The track as a subtitle file.</param>
/// <param name="Language">The language the video names for the track.</param>
/// <param name="Title">The track's title.</param>
/// <param name="IsDefault">Whether the video marks the track as its default.</param>
/// <param name="IsForced">Whether the video marks the track as forced, which holds signs only.</param>
/// <param name="IsHearingImpaired">Whether the video marks the track as captions for the hearing impaired.</param>
internal sealed partial record ReferenceUpload(string FileName, byte[] Content, string? Language, string? Title, bool IsDefault, bool IsForced, bool IsHearingImpaired = false)
{
    /// <summary>Gets a value indicating whether the track holds captions for the hearing impaired, by the video's mark or by its title.</summary>
    public bool ForHearingImpaired => IsHearingImpaired || (Title is not null && CaptionsTitle().IsMatch(Title));

    [GeneratedRegex(@"(^|[^a-z])(cc|sdh)([^a-z]|$)|closed caption|hearing", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaptionsTitle();
}

/// <summary>
/// One job for Retimer API.
/// </summary>
internal sealed class RetimerSubmission
{
    public required string TargetName { get; init; }

    public required byte[] Target { get; init; }

    public IReadOnlyList<ReferenceUpload> References { get; init; } = [];

    /// <summary>Gets the path of the audio file to send along, if any.</summary>
    public string? AudioPath { get; init; }

    public JsonObject Options { get; init; } = [];
}

/// <summary>
/// What Retimer API says about one of the reference tracks it was given.
/// </summary>
internal sealed record RetimerCandidate(int Index, string? Language, string? Title, string Verdict, string? Reason);

/// <summary>
/// A job as Retimer API reports it.
/// </summary>
internal sealed class RetimerJob
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets <c>queued</c>, <c>running</c> or <c>finished</c>.</summary>
    public string Status { get; init; } = string.Empty;

    public string? Stage { get; init; }

    /// <summary>Gets the outcome once finished: <c>synced</c>, <c>already_in_sync</c>, <c>partial</c>, <c>rejected_no_match</c>, <c>transcribed</c> or <c>failed</c>.</summary>
    public string? Outcome { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public double CostUsd { get; init; }

    public double? Confidence { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Gets how the timing was fitted: <c>constant</c>, <c>rate</c>, <c>blocks</c> or <c>rate_blocks</c>.</summary>
    public string? Fit { get; init; }

    /// <summary>Gets by how many milliseconds each part of the file was moved, in the file's order. Above zero is later.</summary>
    public IReadOnlyList<int> ShiftsMs { get; init; } = [];

    /// <summary>Gets a value indicating whether the answer says which reference track was used.</summary>
    public bool HasReferenceReport { get; init; }

    /// <summary>Gets the position of the track Retimer API used, or <c>null</c> when none fits.</summary>
    public int? ChosenReference { get; init; }

    public string? ReferenceReason { get; init; }

    public IReadOnlyList<RetimerCandidate> Candidates { get; init; } = [];

    public bool IsFinished => Status == "finished";
}

/// <summary>
/// Why a call to Retimer API failed.
/// </summary>
internal enum RetimerError
{
    NotConfigured,
    InvalidAddress,
    Unauthorized,

    /// <summary>A file or an option cannot be used (status 422).</summary>
    Refused,
    TooLarge,

    /// <summary>A limit of the service is reached, such as its daily budget (status 429).</summary>
    LimitReached,
    NotFound,
    Unavailable
}

internal sealed class RetimerException : Exception
{
    public RetimerException(RetimerError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public RetimerError Error { get; }

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// The job calls of Retimer API.
/// </summary>
internal interface IRetimerApi
{
    Task<RetimerFeatures> GetFeaturesAsync(CancellationToken cancellationToken);

    Task<RetimerJob> SubmitAsync(RetimerSubmission submission, CancellationToken cancellationToken);

    /// <summary>Sends a picture subtitle to be read into text.</summary>
    Task<RetimerJob> SubmitOcrAsync(string fileName, byte[] content, string? language, CancellationToken cancellationToken);

    Task<RetimerJob> GetJobAsync(string id, CancellationToken cancellationToken);

    Task<byte[]> GetResultAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes a job and its files from the service. Failures are ignored.</summary>
    Task DeleteJobAsync(string id, CancellationToken cancellationToken);
}
