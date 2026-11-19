using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// The plugin's settings. Jellyfin stores them as XML on the host, readable by its administrator.
/// Lists are arrays, because Jellyfin's XML reader would add to a list's default values instead of replacing them.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The audio is never sent to Retimer API.</summary>
    public const string AudioNever = "never";

    /// <summary>The audio is sent only when no subtitle track in the video fits.</summary>
    public const string AudioFallback = "fallback";

    /// <summary>The audio is always sent, together with the subtitle tracks.</summary>
    public const string AudioAlways = "always";

    /// <summary>The id and the name of the model a fresh installation lists.</summary>
    public const string DefaultModelId = "anthropic/claude-haiku-5.5";

    /// <summary>
    /// Gets or sets the subtitle language every show uses unless it has its own, as a two-letter code.
    /// </summary>
    public string SubtitleLanguage { get; set; } = "ja";

    /// <summary>
    /// Gets or sets the source that wins when two files are equally good: <c>jimaku</c> or <c>opensubtitles</c>.
    /// </summary>
    public string PreferredSource { get; set; } = "jimaku";

    /// <summary>
    /// Gets or sets a value indicating whether Jimaku is searched.
    /// </summary>
    public bool JimakuEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the user's own Jimaku API key, from the account page on jimaku.cc.
    /// </summary>
    public string JimakuApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether OpenSubtitles is searched.
    /// </summary>
    public bool OpenSubtitlesEnabled { get; set; }

    /// <summary>
    /// Gets or sets the API key of an OpenSubtitles API consumer.
    /// </summary>
    public string OpenSubtitlesApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles user name. With a login, OpenSubtitles allows more downloads a day.
    /// </summary>
    public string OpenSubtitlesUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles password.
    /// </summary>
    public string OpenSubtitlesPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether subtitles are retimed with Retimer API. Off on a fresh installation.
    /// </summary>
    public bool RetimerEnabled { get; set; }

    /// <summary>
    /// Gets or sets the address of a retimer-api server.
    /// </summary>
    public string RetimerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key for Retimer API at <see cref="RetimerUrl"/>.
    /// </summary>
    public string RetimerApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the video's audio goes to Retimer API: <see cref="AudioNever"/>,
    /// <see cref="AudioFallback"/> or <see cref="AudioAlways"/>.
    /// </summary>
    public string SendAudio { get; set; } = AudioNever;

    /// <summary>
    /// Gets or sets a value indicating whether picture subtitles (PGS) are read into text by Retimer API.
    /// </summary>
    public bool ConvertPictureSubtitles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Retimer API removes sound descriptions and speaker names.
    /// </summary>
    public bool RemoveHearingImpaired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Retimer API removes songs and karaoke lines.
    /// </summary>
    public bool RemoveSongs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Retimer API removes signs and credits.
    /// </summary>
    public bool RemoveSignsAndCredits { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Retimer API removes music notes and other symbols.
    /// </summary>
    public bool RemoveSymbols { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Retimer API removes colours, fonts and positions.
    /// </summary>
    public bool RemoveStyling { get; set; }

    /// <summary>
    /// Gets or sets the format a retimed subtitle is saved in: <c>same</c> as downloaded, <c>srt</c> or <c>ass</c>.
    /// </summary>
    public string OutputFormat { get; set; } = "same";

    /// <summary>
    /// Gets or sets the OpenRouter API key the model's calls are billed to.
    /// </summary>
    public string OpenRouterApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the models the administrator may switch between.
    /// </summary>
    public ModelEntry[] Models { get; set; } = [new ModelEntry { Id = DefaultModelId, Name = "Haiku 5.5" }];

    /// <summary>
    /// Gets or sets the id of the model that makes the choices: one of <see cref="Models"/>.
    /// The plugin picks no file without a model, so the list is never empty.
    /// </summary>
    public string ModelInUse { get; set; } = DefaultModelId;

    /// <summary>
    /// Gets or sets the ids of the libraries that do not appear on the page. A new library appears by itself.
    /// </summary>
    public Guid[] HiddenLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets who may fetch: <c>managers</c> (administrators and users who may manage subtitles) or <c>everyone</c>.
    /// </summary>
    public string FetchAccess { get; set; } = "managers";

    /// <summary>
    /// Gets or sets the most subtitles the plugin fetches in one day. Zero means no limit.
    /// </summary>
    public int MaxJobsPerDay { get; set; } = 200;

    /// <summary>
    /// Gets or sets the most the plugin may spend in one day on Retimer API and the model, in US dollars,
    /// as far as the services report their cost. Zero means no limit.
    /// </summary>
    public double MaxCostPerDay { get; set; } = 2;
}
