using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Sources;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Retimer.Api;

internal sealed record LibraryChoice(Guid Id, string Name, string Kind, bool Hidden);

/// <summary>
/// The settings as the settings screen shows them: every value, and for each key only whether one is stored.
/// </summary>
internal sealed record SettingsView
{
    public string SubtitleLanguage { get; init; } = string.Empty;

    public string PreferredSource { get; init; } = string.Empty;

    public bool JimakuEnabled { get; init; }

    public bool HasJimakuApiKey { get; init; }

    public bool OpenSubtitlesEnabled { get; init; }

    public bool HasOpenSubtitlesApiKey { get; init; }

    public string OpenSubtitlesUsername { get; init; } = string.Empty;

    public bool HasOpenSubtitlesPassword { get; init; }

    /// <summary>Gets how many downloads OpenSubtitles said are left today, when it said so.</summary>
    public int? OpenSubtitlesRemaining { get; init; }

    public bool RetimerEnabled { get; init; }

    public string RetimerUrl { get; init; } = string.Empty;

    public bool HasRetimerApiKey { get; init; }

    public string SendAudio { get; init; } = string.Empty;

    public bool ConvertPictureSubtitles { get; init; }

    public bool RemoveHearingImpaired { get; init; }

    public bool RemoveSongs { get; init; }

    public bool RemoveSignsAndCredits { get; init; }

    public bool RemoveSymbols { get; init; }

    public bool RemoveStyling { get; init; }

    public string OutputFormat { get; init; } = string.Empty;

    public bool HasOpenRouterApiKey { get; init; }

    public IReadOnlyList<ModelEntry> Models { get; init; } = [];

    public string ModelInUse { get; init; } = string.Empty;

    public string WithoutModel { get; init; } = string.Empty;

    public IReadOnlyList<LibraryChoice> Libraries { get; init; } = [];

    public string FetchAccess { get; init; } = string.Empty;

    public int MaxJobsPerDay { get; init; }

    public double MaxCostPerDay { get; init; }

    public IReadOnlyList<LanguageView> Languages { get; init; } = [];

    public IReadOnlyList<SourceView> Sources { get; init; } = [];

    public string RetimerProjectUrl { get; init; } = SettingsService.RetimerProjectUrl;
}

/// <summary>
/// Reads, checks and saves the settings for administrators, and tests the services they point to.
/// </summary>
internal sealed partial class SettingsService
{
    /// <summary>Where Retimer API's code and its description are.</summary>
    internal const string RetimerProjectUrl = "https://github.com/friedrich-de/retimer-api";

    internal const int MaxModels = 20;

    private static readonly string[] _secrets =
    [
        nameof(PluginConfiguration.JimakuApiKey),
        nameof(PluginConfiguration.OpenSubtitlesApiKey),
        nameof(PluginConfiguration.OpenSubtitlesPassword),
        nameof(PluginConfiguration.RetimerApiKey),
        nameof(PluginConfiguration.OpenRouterApiKey)
    ];

    private readonly IPluginSettings _settings;
    private readonly IVideoLibrary _library;
    private readonly SubtitleSources _sources;
    private readonly ModelPicker _model;
    private readonly IRetimerClient _retimerClient;
    private readonly IRetimerApi _retimerApi;
    private readonly JobQueue _queue;

    public SettingsService(IPluginSettings settings, IVideoLibrary library, SubtitleSources sources, ModelPicker model, IRetimerClient retimerClient, IRetimerApi retimerApi, JobQueue queue)
    {
        _queue = queue;
        _settings = settings;
        _library = library;
        _sources = sources;
        _model = model;
        _retimerClient = retimerClient;
        _retimerApi = retimerApi;
    }

    public SettingsView Get()
    {
        var c = _settings.Current;
        var hidden = c.HiddenLibraryIds.ToHashSet();
        return new SettingsView
        {
            SubtitleLanguage = Languages.Normalize(c.SubtitleLanguage) ?? Languages.Default,
            PreferredSource = c.PreferredSource,
            JimakuEnabled = c.JimakuEnabled,
            HasJimakuApiKey = !string.IsNullOrWhiteSpace(c.JimakuApiKey),
            OpenSubtitlesEnabled = c.OpenSubtitlesEnabled,
            HasOpenSubtitlesApiKey = !string.IsNullOrWhiteSpace(c.OpenSubtitlesApiKey),
            OpenSubtitlesUsername = c.OpenSubtitlesUsername,
            HasOpenSubtitlesPassword = !string.IsNullOrWhiteSpace(c.OpenSubtitlesPassword),
            OpenSubtitlesRemaining = _sources.All.OfType<OpenSubtitlesSource>().FirstOrDefault()?.RemainingDownloads,
            RetimerEnabled = c.RetimerEnabled,
            RetimerUrl = c.RetimerUrl,
            HasRetimerApiKey = !string.IsNullOrWhiteSpace(c.RetimerApiKey),
            SendAudio = c.SendAudio,
            ConvertPictureSubtitles = c.ConvertPictureSubtitles,
            RemoveHearingImpaired = c.RemoveHearingImpaired,
            RemoveSongs = c.RemoveSongs,
            RemoveSignsAndCredits = c.RemoveSignsAndCredits,
            RemoveSymbols = c.RemoveSymbols,
            RemoveStyling = c.RemoveStyling,
            OutputFormat = c.OutputFormat,
            HasOpenRouterApiKey = !string.IsNullOrWhiteSpace(c.OpenRouterApiKey),
            Models = c.Models.Select(m => new ModelEntry { Id = m.Id, Name = m.Name }).ToList(),
            ModelInUse = c.ModelInUse,
            WithoutModel = c.WithoutModel,
            Libraries = _library.GetAllLibraries().Select(l => new LibraryChoice(l.Id, l.Name, l.Kind, hidden.Contains(l.Id))).ToList(),
            FetchAccess = c.FetchAccess,
            MaxJobsPerDay = c.MaxJobsPerDay,
            MaxCostPerDay = c.MaxCostPerDay,
            Languages = Languages.All.Select(l => new LanguageView(l.Code, l.Name)).ToList(),
            Sources = _sources.All.Select(s => new SourceView(s.Id, s.Name, s.IsReady)).ToList()
        };
    }

    public (SettingsView? View, ServiceError? Error) Save(SettingsUpdate update)
    {
        static ServiceError Bad(string message) => new(StatusCodes.Status400BadRequest, message);

        // A copy, so a value that is refused leaves the settings in use untouched.
        var c = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(_settings.Current))!;

        if (update.SubtitleLanguage is not null)
        {
            var code = Languages.Normalize(update.SubtitleLanguage);
            if (code is null)
            {
                return (null, Bad("The plugin does not know that subtitle language."));
            }

            c.SubtitleLanguage = code;
        }

        if (update.PreferredSource is not null)
        {
            var source = _sources.Find(update.PreferredSource);
            if (source is null)
            {
                return (null, Bad("The plugin does not know that source."));
            }

            c.PreferredSource = source.Id;
        }

        if (update.SendAudio is not null)
        {
            if (update.SendAudio is not (PluginConfiguration.AudioNever or PluginConfiguration.AudioFallback or PluginConfiguration.AudioAlways))
            {
                return (null, Bad("The audio setting is never, fallback or always."));
            }

            c.SendAudio = update.SendAudio;
        }

        if (update.OutputFormat is not null)
        {
            if (update.OutputFormat is not ("same" or "srt" or "ass"))
            {
                return (null, Bad("The format is same, srt or ass."));
            }

            c.OutputFormat = update.OutputFormat;
        }

        if (update.WithoutModel is not null)
        {
            if (update.WithoutModel is not ("rules" or "ask"))
            {
                return (null, Bad("Without a model, the plugin uses the rules or asks."));
            }

            c.WithoutModel = update.WithoutModel;
        }

        if (update.FetchAccess is not null)
        {
            if (update.FetchAccess is not ("managers" or "everyone"))
            {
                return (null, Bad("Subtitles may be fetched by managers or by everyone."));
            }

            c.FetchAccess = update.FetchAccess;
        }

        if (update.RetimerUrl is not null)
        {
            var address = update.RetimerUrl.Trim();
            if (address.Length > 0 && !RetimerClient.TryGetBaseAddress(address, out _))
            {
                return (null, Bad("The address of Retimer API must start with http:// or https:// and hold no question mark."));
            }

            c.RetimerUrl = address;
        }

        if (update.Models is not null)
        {
            var models = new List<ModelEntry>();
            foreach (var entry in update.Models)
            {
                var id = entry?.Id?.Trim() ?? string.Empty;
                if (!ModelId().IsMatch(id))
                {
                    return (null, Bad("A model id looks like anthropic/claude-haiku-5.5: letters, digits and . _ : / @ - only."));
                }

                if (models.Any(m => m.Id == id))
                {
                    continue;
                }

                var name = entry!.Name?.Trim() ?? string.Empty;
                models.Add(new ModelEntry { Id = id, Name = name.Length == 0 ? id : (name.Length > 60 ? name[..60] : name) });
            }

            if (models.Count > MaxModels)
            {
                return (null, Bad(string.Create(CultureInfo.InvariantCulture, $"The list holds up to {MaxModels} models.")));
            }

            c.Models = [.. models];
        }

        if (update.ModelInUse is not null)
        {
            c.ModelInUse = update.ModelInUse.Trim();
        }

        if (c.Models.All(m => m.Id != c.ModelInUse))
        {
            // The model in use is always one of the list. An empty list means the rules choose.
            c.ModelInUse = c.Models.Length > 0 ? c.Models[0].Id : string.Empty;
        }

        if (update.MaxJobsPerDay is { } jobs)
        {
            if (jobs is < 0 or > 100000)
            {
                return (null, Bad("The limit of subtitles for a day is between 0 and 100000."));
            }

            c.MaxJobsPerDay = jobs;
        }

        if (update.MaxCostPerDay is { } cost)
        {
            if (double.IsNaN(cost) || cost is < 0 or > 10000)
            {
                return (null, Bad("The cost limit for a day is between 0 and 10000 dollars."));
            }

            c.MaxCostPerDay = Math.Round(cost, 2);
        }

        if (update.OpenSubtitlesUsername is not null)
        {
            var name = update.OpenSubtitlesUsername.Trim();
            if (name.Length > 100)
            {
                return (null, Bad("The user name is too long."));
            }

            c.OpenSubtitlesUsername = name;
        }

        c.JimakuEnabled = update.JimakuEnabled ?? c.JimakuEnabled;
        c.OpenSubtitlesEnabled = update.OpenSubtitlesEnabled ?? c.OpenSubtitlesEnabled;
        c.RetimerEnabled = update.RetimerEnabled ?? c.RetimerEnabled;
        c.ConvertPictureSubtitles = update.ConvertPictureSubtitles ?? c.ConvertPictureSubtitles;
        c.RemoveHearingImpaired = update.RemoveHearingImpaired ?? c.RemoveHearingImpaired;
        c.RemoveSongs = update.RemoveSongs ?? c.RemoveSongs;
        c.RemoveSignsAndCredits = update.RemoveSignsAndCredits ?? c.RemoveSignsAndCredits;
        c.RemoveSymbols = update.RemoveSymbols ?? c.RemoveSymbols;
        c.RemoveStyling = update.RemoveStyling ?? c.RemoveStyling;

        if (update.HiddenLibraryIds is not null)
        {
            c.HiddenLibraryIds = [.. update.HiddenLibraryIds.Where(id => id != Guid.Empty).Distinct()];
        }

        foreach (var name in update.ClearSecrets ?? [])
        {
            if (!_secrets.Contains(name, StringComparer.Ordinal))
            {
                return (null, Bad("The plugin stores no key of that name."));
            }
        }

        string Secret(string name, string? typed, string stored)
        {
            var value = typed?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            return update.ClearSecrets is not null && update.ClearSecrets.Contains(name, StringComparer.Ordinal) ? string.Empty : stored;
        }

        foreach (var typed in new[] { update.JimakuApiKey, update.OpenSubtitlesApiKey, update.OpenSubtitlesPassword, update.RetimerApiKey, update.OpenRouterApiKey })
        {
            if (typed is { Length: > 500 })
            {
                return (null, Bad("A key or password is too long."));
            }
        }

        c.JimakuApiKey = Secret(nameof(c.JimakuApiKey), update.JimakuApiKey, c.JimakuApiKey);
        c.OpenSubtitlesApiKey = Secret(nameof(c.OpenSubtitlesApiKey), update.OpenSubtitlesApiKey, c.OpenSubtitlesApiKey);
        c.OpenSubtitlesPassword = Secret(nameof(c.OpenSubtitlesPassword), update.OpenSubtitlesPassword, c.OpenSubtitlesPassword);
        c.RetimerApiKey = Secret(nameof(c.RetimerApiKey), update.RetimerApiKey, c.RetimerApiKey);
        c.OpenRouterApiKey = Secret(nameof(c.OpenRouterApiKey), update.OpenRouterApiKey, c.OpenRouterApiKey);

        _settings.Save(c);
        _library.Invalidate();
        return (Get(), null);
    }

    /// <summary>
    /// Tests one of the services with the settings as they are stored.
    /// </summary>
    /// <param name="target">One of <c>jimaku</c>, <c>opensubtitles</c>, <c>retimer</c>, <c>model</c>.</param>
    /// <param name="modelId">The model to test. Without one, the model in use.</param>
    /// <param name="cancellationToken">Cancels the test.</param>
    /// <returns>The outcome, or <c>null</c> for a target the plugin does not know.</returns>
    public async Task<ConnectionTestResponse?> TestAsync(string target, string? modelId, CancellationToken cancellationToken)
    {
        switch (target.ToLowerInvariant())
        {
            case "jimaku":
            case "opensubtitles":
                var source = _sources.Find(target);
                if (source is null)
                {
                    return null;
                }

                try
                {
                    return new ConnectionTestResponse { Ok = true, Status = "Ok", Message = await source.CheckAsync(cancellationToken).ConfigureAwait(false) };
                }
                catch (SourceException ex)
                {
                    return new ConnectionTestResponse { Ok = false, Status = ex.Error.ToString(), Message = ex.Message };
                }

            case "retimer":
                return await TestRetimerAsync(cancellationToken).ConfigureAwait(false);

            case "model":
                return await TestModelAsync(modelId, cancellationToken).ConfigureAwait(false);

            default:
                return null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/@-]{1,99}$")]
    private static partial Regex ModelId();

    private async Task<ConnectionTestResponse> TestRetimerAsync(CancellationToken cancellationToken)
    {
        var status = await _retimerClient.CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
        var message = status switch
        {
            RetimerConnectionStatus.Ok => "Retimer API answered and accepted the key.",
            RetimerConnectionStatus.NotConfigured => "Enter the address and the key of Retimer API first.",
            RetimerConnectionStatus.InvalidAddress => "The address must start with http:// or https://.",
            RetimerConnectionStatus.Unauthorized => "Retimer API answered and rejected the key.",
            _ => "No Retimer API answered at that address."
        };

        if (status == RetimerConnectionStatus.Ok)
        {
            try
            {
                var features = await _retimerApi.GetFeaturesAsync(cancellationToken).ConfigureAwait(false);
                message += features.ReferenceTracks
                    ? string.Create(CultureInfo.InvariantCulture, $" It takes up to {features.MaxReferenceSubtitles} subtitle tracks of a video and chooses the one that fits.")
                    : " It is an older release: it takes one subtitle track of a video, and it cannot remove signs and credits.";
            }
            catch (RetimerException ex)
            {
                message += " What it can do could not be read: " + ex.Message;
            }
        }

        return new ConnectionTestResponse { Ok = status == RetimerConnectionStatus.Ok, Status = status.ToString(), Message = message };
    }

    private async Task<ConnectionTestResponse> TestModelAsync(string? modelId, CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var id = string.IsNullOrWhiteSpace(modelId) ? configuration.ModelInUse : modelId.Trim();
        if (string.IsNullOrWhiteSpace(configuration.OpenRouterApiKey))
        {
            return new ConnectionTestResponse { Ok = false, Status = nameof(ModelError.NotConfigured), Message = "Enter the OpenRouter API key first." };
        }

        // Only a model of the list is asked, so the test cannot be used to call any model with the server's key.
        var entry = configuration.Models.FirstOrDefault(m => m.Id == id);
        if (entry is null)
        {
            return new ConnectionTestResponse { Ok = false, Status = nameof(ModelError.NotConfigured), Message = "Add the model to the list and save first." };
        }

        try
        {
            var cost = await _model.TestAsync(entry.Id, cancellationToken).ConfigureAwait(false);

            // The question is paid with the server's key like any other, so it counts toward the day.
            _queue.AddCost(cost);
            return new ConnectionTestResponse
            {
                Ok = true,
                Status = "Ok",
                Message = entry.Name + " answered and can use tools." + (cost > 0 ? string.Create(CultureInfo.InvariantCulture, $" The question cost ${cost:0.0000}.") : string.Empty)
            };
        }
        catch (ModelException ex)
        {
            return new ConnectionTestResponse { Ok = false, Status = ex.Error.ToString(), Message = ex.Message };
        }
    }
}
