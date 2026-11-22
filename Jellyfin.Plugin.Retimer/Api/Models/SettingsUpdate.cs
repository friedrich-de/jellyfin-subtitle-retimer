using System;
using Jellyfin.Plugin.Retimer.Configuration;

namespace Jellyfin.Plugin.Retimer.Api.Models;

/// <summary>
/// New values for the plugin's settings. A value that is left out stays as it is.
/// A key or password that is left out or empty stays as it is too; <see cref="ClearSecrets"/> removes one.
/// </summary>
public sealed class SettingsUpdate
{
    /// <summary>Gets or sets the subtitle language for the server, as a code such as <c>ja</c>.</summary>
    public string? SubtitleLanguage { get; set; }

    /// <summary>Gets or sets the id of the source whose files win a tie.</summary>
    public string? PreferredSource { get; set; }

    /// <summary>Gets or sets a value indicating whether Jimaku is searched.</summary>
    public bool? JimakuEnabled { get; set; }

    /// <summary>Gets or sets a new Jimaku API key.</summary>
    public string? JimakuApiKey { get; set; }

    /// <summary>Gets or sets a value indicating whether OpenSubtitles is searched.</summary>
    public bool? OpenSubtitlesEnabled { get; set; }

    /// <summary>Gets or sets a new OpenSubtitles API key.</summary>
    public string? OpenSubtitlesApiKey { get; set; }

    /// <summary>Gets or sets the OpenSubtitles user name.</summary>
    public string? OpenSubtitlesUsername { get; set; }

    /// <summary>Gets or sets a new OpenSubtitles password.</summary>
    public string? OpenSubtitlesPassword { get; set; }

    /// <summary>Gets or sets a value indicating whether subtitles are retimed with Retimer API.</summary>
    public bool? RetimerEnabled { get; set; }

    /// <summary>Gets or sets the address of Retimer API.</summary>
    public string? RetimerUrl { get; set; }

    /// <summary>Gets or sets a new Retimer API key.</summary>
    public string? RetimerApiKey { get; set; }

    /// <summary>Gets or sets when the video's audio is sent: <c>never</c>, <c>fallback</c> or <c>always</c>.</summary>
    public string? SendAudio { get; set; }

    /// <summary>Gets or sets a value indicating whether picture subtitles are converted with Retimer API.</summary>
    public bool? ConvertPictureSubtitles { get; set; }

    /// <summary>Gets or sets a value indicating whether text for the hard of hearing is removed.</summary>
    public bool? RemoveHearingImpaired { get; set; }

    /// <summary>Gets or sets a value indicating whether song lyrics are removed.</summary>
    public bool? RemoveSongs { get; set; }

    /// <summary>Gets or sets a value indicating whether signs and credits are removed.</summary>
    public bool? RemoveSignsAndCredits { get; set; }

    /// <summary>Gets or sets a value indicating whether music notes and other symbols are removed.</summary>
    public bool? RemoveSymbols { get; set; }

    /// <summary>Gets or sets a value indicating whether styling is removed.</summary>
    public bool? RemoveStyling { get; set; }

    /// <summary>Gets or sets the format of retimed subtitles: <c>same</c>, <c>srt</c> or <c>ass</c>.</summary>
    public string? OutputFormat { get; set; }

    /// <summary>Gets or sets a new OpenRouter API key.</summary>
    public string? OpenRouterApiKey { get; set; }

    /// <summary>Gets or sets the models the plugin may use.</summary>
    public ModelEntry[]? Models { get; set; }

    /// <summary>Gets or sets the id of the model that chooses files.</summary>
    public string? ModelInUse { get; set; }

    /// <summary>Gets or sets the libraries the page does not show.</summary>
    public Guid[]? HiddenLibraryIds { get; set; }

    /// <summary>Gets or sets who may get subtitles: <c>managers</c> or <c>everyone</c>.</summary>
    public string? FetchAccess { get; set; }

    /// <summary>Gets or sets how many subtitles may be fetched on one day. 0 means no limit.</summary>
    public int? MaxJobsPerDay { get; set; }

    /// <summary>Gets or sets how much the services may cost on one day, in US dollars. 0 means no limit.</summary>
    public double? MaxCostPerDay { get; set; }

    /// <summary>Gets or sets a value indicating whether shows that come into the library from now on are watched by themselves.</summary>
    public bool? WatchNewShows { get; set; }

    /// <summary>Gets or sets for how many days watch mode looks for an episode's subtitle. 0 means for as long as the season is watched.</summary>
    public int? WatchDays { get; set; }

    /// <summary>
    /// Gets or sets the stored keys and passwords to remove, by the names of their settings, such as <c>JimakuApiKey</c>.
    /// </summary>
    public string[]? ClearSecrets { get; set; }
}
