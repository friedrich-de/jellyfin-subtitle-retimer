using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Sources;
using Jellyfin.Plugin.Retimer.Watch;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// What the page can ask for and do, with the rules on who may do it.
/// </summary>
internal sealed class PageService
{
    /// <summary>How many videos one request may start jobs for.</summary>
    internal const int MaxItemsPerRequest = 300;

    private static readonly TimeSpan _visibleLifetime = TimeSpan.FromSeconds(60);

    private readonly IPluginSettings _settings;
    private readonly IVideoLibrary _library;
    private readonly SubtitleSources _sources;
    private readonly FileFinder _finder;
    private readonly JobQueue _queue;
    private readonly PluginStore _store;
    private readonly ModelPicker _model;
    private readonly WatchService _watch;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, SourceEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid User, Guid Show), (DateTimeOffset At, bool Visible)> _visible = [];

    public PageService(IPluginSettings settings, IVideoLibrary library, SubtitleSources sources, FileFinder finder, JobQueue queue, PluginStore store, ModelPicker model, WatchService watch)
    {
        _watch = watch;
        _settings = settings;
        _library = library;
        _sources = sources;
        _finder = finder;
        _queue = queue;
        _store = store;
        _model = model;
    }

    private string DefaultLanguage => Languages.Normalize(_settings.Current.SubtitleLanguage) ?? Languages.Default;

    private static ServiceError Forbidden => new(StatusCodes.Status403Forbidden, "Your Jellyfin user may not get or change subtitles here. An administrator decides that in the plugin's settings.");

    private static ServiceError NoVideo => new(StatusCodes.Status404NotFound, "That video is not in a library you can see.");

    /// <summary>
    /// The line under a show's name that says which video a job is for.
    /// </summary>
    public static string Label(VideoInfo video)
    {
        if (video.IsMovie)
        {
            return video.Year is { } year ? string.Create(CultureInfo.InvariantCulture, $"Film · {year}") : "Film";
        }

        var season = video.Season switch
        {
            null => null,
            0 => "Specials",
            var number => string.Create(CultureInfo.InvariantCulture, $"Season {number}")
        };
        var episode = video.Episode is { } e ? string.Create(CultureInfo.InvariantCulture, $"{e}. {video.Name}") : video.Name;
        return season is null ? episode : season + " · " + episode;
    }

    /// <summary>
    /// Whether the user may get, replace and remove subtitles.
    /// </summary>
    public bool MayFetch(Caller caller) =>
        caller.IsAdministrator || caller.MayManageSubtitles || string.Equals(_settings.Current.FetchAccess, "everyone", StringComparison.Ordinal);

    /// <summary>
    /// The subtitle language of a show or film: its own, or the server's.
    /// </summary>
    public string LanguageOf(Guid showId) => Languages.Normalize(_store.Show(showId)?.Language) ?? DefaultLanguage;

    public StatusView Status(Caller caller)
    {
        var configuration = _settings.Current;
        var language = DefaultLanguage;
        return new StatusView
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0",
            UserName = caller.Name,
            IsAdministrator = caller.IsAdministrator,
            MayFetch = MayFetch(caller),
            Language = language,
            LanguageName = Languages.NameOf(language),
            Languages = Languages.All.Select(l => new LanguageView(l.Code, l.Name)).ToList(),
            Sources = _sources.All.Select(s => new SourceView(s.Id, s.Name, s.IsReady)).ToList(),
            RetimerEnabled = configuration.RetimerEnabled,
            RetimerReady = JobRunner.RetimerReady(configuration),
            ModelName = _model.Model?.Name,
            Usage = UsageFor(caller),
            JobsVersion = _queue.Version
        };
    }

    public IReadOnlyList<LibraryInfo> Libraries(Caller caller) => _library.GetLibraries(caller.UserId);

    public IReadOnlyList<ShowView>? Shows(Caller caller, Guid libraryId)
    {
        if (_library.GetLibraries(caller.UserId).All(library => library.Id != libraryId))
        {
            return null;
        }

        // One read of the store for all shows, not one for each.
        var own = _store.Read(data => data.Shows
            .Where(pair => Languages.Normalize(pair.Value.Language) is not null)
            .ToDictionary(pair => pair.Key, pair => Languages.Normalize(pair.Value.Language)!, StringComparer.Ordinal));
        var watched = _store.Read(data => data.Shows
            .Where(pair => WatchService.AnyWatched(pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal));
        var fallback = DefaultLanguage;
        string LanguageFor(Guid id) => own.GetValueOrDefault(PluginStore.ShowKey(id)) ?? fallback;

        return _library.GetShows(caller.UserId, libraryId, LanguageFor)
            .Select(show => new ShowView
            {
                Id = show.Id,
                Name = show.Name,
                Year = show.Year,
                IsMovie = show.IsMovie,
                ImageTag = show.ImageTag,
                Total = show.Total,
                Have = show.Have,
                Language = LanguageFor(show.Id),
                OwnLanguage = own.ContainsKey(PluginStore.ShowKey(show.Id)),
                Watched = watched.Contains(PluginStore.ShowKey(show.Id))
            })
            .ToList();
    }

    public ShowDetailView? Show(Caller caller, Guid showId)
    {
        var show = _library.GetShow(caller.UserId, showId);
        if (show is null)
        {
            return null;
        }

        var showRecord = _store.Show(showId);
        var stored = Languages.Normalize(showRecord?.Language);
        var language = stored ?? DefaultLanguage;
        var active = _queue.Active();
        var retimerReady = JobRunner.RetimerReady(_settings.Current);
        var watchDays = _settings.Current.WatchDays;
        var now = _watch.Now;
        var mayFetch = MayFetch(caller);
        var keys = show.Seasons.SelectMany(season => season.Episodes).Select(video => PluginStore.ItemKey(video.Id, language)).ToList();
        var records = _store.Read(data => keys
            .Where(data.Items.ContainsKey)
            .ToDictionary(key => key, key => data.Items[key].Snapshot(), StringComparer.Ordinal));

        return new ShowDetailView
        {
            Id = show.Id,
            Name = show.Name,
            Year = show.Year,
            IsMovie = show.IsMovie,
            PosterTag = show.PosterTag,
            BackdropTag = show.BackdropTag,
            Language = language,
            LanguageName = Languages.NameOf(language),
            OwnLanguage = stored is not null,
            Sources = _sources.ReadyFor(language).Select(source => source.Name).ToList(),
            WatchWhole = showRecord?.Watch == true,
            WatchDays = watchDays,
            WatchEveryHours = _watch.Schedule.Interval?.TotalHours,
            Seasons = show.Seasons
                .Select(season =>
                {
                    var rule = WatchService.Rule(showRecord, season.Number);
                    return new SeasonView(
                        season.Number,
                        season.Name,
                        season.Episodes
                            .Select(video =>
                            {
                                var key = PluginStore.ItemKey(video.Id, language);
                                var view = Describe(
                                    video,
                                    language,
                                    records.GetValueOrDefault(key),
                                    active.TryGetValue(key, out var job) ? job : null,
                                    retimerReady,
                                    caller,
                                    mayFetch);
                                if (!rule.On || !video.IsLocalFile || WatchService.HasSubtitle(video, language))
                                {
                                    return view;
                                }

                                var until = WatchService.Until(video, rule, watchDays);
                                return view with { Watched = true, WatchUntil = until, WatchOver = until is { } end && now > end };
                            })
                            .ToList())
                    {
                        Key = WatchService.SeasonKey(season.Number),
                        Watched = rule.On
                    };
                })
                .ToList()
        };
    }

    /// <summary>
    /// Switches watch mode on or off for a show, a season of it, or a film. Switching on checks it at once.
    /// </summary>
    public ServiceError? SetWatch(Caller caller, Guid showId, WatchRequest request)
    {
        if (!MayFetch(caller))
        {
            return Forbidden;
        }

        var show = _library.GetShow(caller.UserId, showId);
        if (show is null)
        {
            return new ServiceError(StatusCodes.Status404NotFound, "That show is not in a library you can see.");
        }

        if (request.Season is null)
        {
            _watch.SetShow(showId, request.Watch);
        }
        else
        {
            if (!WatchService.TryParseSeasonKey(request.Season, out var season) || show.Seasons.All(s => WatchService.SeasonKey(s.Number) != request.Season))
            {
                return new ServiceError(StatusCodes.Status400BadRequest, "That show has no such season.");
            }

            _watch.SetSeason(showId, season, request.Watch);
        }

        if (request.Watch)
        {
            _watch.CheckShowNow(showId);
        }

        return null;
    }

    /// <summary>
    /// The watched shows the user can see, with the videos watch mode still looks for, and how the last check went.
    /// </summary>
    public WatchView Watching(Caller caller)
    {
        var configuration = _settings.Current;
        var now = _watch.Now;
        var watched = _store.Read(data => data.Shows
            .Where(pair => WatchService.AnyWatched(pair.Value))
            .Select(pair => (pair.Key, Record: pair.Value.Snapshot()))
            .ToList());
        var active = _queue.Active();
        var shows = new List<WatchedShowView>();
        foreach (var (key, record) in watched)
        {
            if (!Guid.TryParseExact(key, "N", out var id) || _library.GetShow(caller.UserId, id) is not { } show)
            {
                continue;
            }

            var language = Languages.Normalize(record.Language) ?? DefaultLanguage;
            var seasons = new List<string>();
            var skipped = new List<string>();
            var waiting = new List<WatchedVideoView>();
            var over = 0;
            foreach (var season in show.Seasons)
            {
                var rule = WatchService.Rule(record, season.Number);
                if (!rule.On)
                {
                    skipped.Add(season.Name);
                    continue;
                }

                seasons.Add(season.Name);
                foreach (var video in season.Episodes.Where(video => video.IsLocalFile && !WatchService.HasSubtitle(video, language)))
                {
                    var until = WatchService.Until(video, rule, configuration.WatchDays);
                    if (until is { } end && now > end)
                    {
                        over++;
                        continue;
                    }

                    var item = _store.Item(video.Id, language);
                    var state = active.TryGetValue(PluginStore.ItemKey(video.Id, language), out var job)
                        ? job.Job.State
                        : item?.LastError is not null ? "failed" : item is { NoFit: true } ? "nofit" : "none";
                    waiting.Add(new WatchedVideoView(video.Id, Label(video), state, until));
                }
            }

            shows.Add(new WatchedShowView
            {
                Id = show.Id,
                Name = show.Name,
                IsMovie = show.IsMovie,
                PosterTag = show.PosterTag,
                LanguageName = Languages.NameOf(language),
                Whole = record.Watch == true,
                Seasons = seasons,
                Skipped = skipped,
                Waiting = waiting,
                Over = over
            });
        }

        var last = _watch.LastRun();
        return new WatchView
        {
            Shows = shows.OrderBy(show => show.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            LastCheck = last is null ? null : new WatchRunView(last.Started, last.Finished, last.Shows, last.LookedFor, last.JobsStarted, last.Problem),
            EveryHours = _watch.Schedule.Interval?.TotalHours,
            Scheduled = _watch.Schedule.Scheduled,
            Days = configuration.WatchDays,
            NewShows = configuration.WatchNewShows,
            MayCheck = caller.IsAdministrator
        };
    }

    /// <summary>
    /// Has Jellyfin run the check of all watched shows now. Only administrators may.
    /// </summary>
    public ServiceError? CheckWatchedNow(Caller caller)
    {
        if (!caller.IsAdministrator)
        {
            return new ServiceError(StatusCodes.Status403Forbidden, "Only an administrator can start the check.");
        }

        _watch.Schedule.RunNow();
        return null;
    }

    public ServiceError? SetLanguage(Caller caller, Guid showId, string? language)
    {
        if (!MayFetch(caller))
        {
            return Forbidden;
        }

        if (!_library.CanSee(caller.UserId, showId))
        {
            return new ServiceError(StatusCodes.Status404NotFound, "That show is not in a library you can see.");
        }

        string? code = null;
        if (!string.IsNullOrWhiteSpace(language))
        {
            code = Languages.Normalize(language);
            if (code is null)
            {
                return new ServiceError(StatusCodes.Status400BadRequest, "The plugin does not know that language.");
            }
        }

        // The server's own language is no override: the show follows the setting again when that changes.
        if (code == DefaultLanguage)
        {
            code = null;
        }

        _store.UpdateShow(showId, show => show.Language = code);
        _library.Invalidate();
        return null;
    }

    public (StartView? View, ServiceError? Error) Start(Caller caller, StartJobsRequest request)
    {
        if (!MayFetch(caller))
        {
            return (null, Forbidden);
        }

        var kind = string.IsNullOrWhiteSpace(request.Kind) ? JobKind.Get : request.Kind;
        if (kind is not (JobKind.Get or JobKind.GetPlain or JobKind.Retime or JobKind.Chosen or JobKind.Another))
        {
            return (null, new ServiceError(StatusCodes.Status400BadRequest, "The plugin does not know that kind of job."));
        }

        var ids = (request.ItemIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return (null, new ServiceError(StatusCodes.Status400BadRequest, "Name at least one video."));
        }

        if (ids.Count > MaxItemsPerRequest)
        {
            return (null, new ServiceError(StatusCodes.Status400BadRequest, string.Create(CultureInfo.InvariantCulture, $"One request may name up to {MaxItemsPerRequest} videos.")));
        }

        var configuration = _settings.Current;
        if (kind == JobKind.Chosen)
        {
            var source = _sources.Find(request.Source);
            if (ids.Count != 1 || source is null || string.IsNullOrWhiteSpace(request.FileId))
            {
                return (null, new ServiceError(StatusCodes.Status400BadRequest, "A chosen file needs one video, the file's source and the file's id."));
            }

            if (!source.IsReady)
            {
                return (null, new ServiceError(StatusCodes.Status409Conflict, source.Name + " is not set up."));
            }
        }

        if (kind == JobKind.Retime && !JobRunner.RetimerReady(configuration))
        {
            return (null, new ServiceError(StatusCodes.Status409Conflict, "Retimer API is switched off or not set up."));
        }

        if (kind is JobKind.Get or JobKind.GetPlain or JobKind.Another && _model.Model is null)
        {
            // These pick a file by themselves, and picking is the model's part.
            return (null, new ServiceError(StatusCodes.Status409Conflict, FileFinder.NoModel));
        }

        var started = 0;
        var already = 0;
        var skipped = 0;
        string? skippedBecause = null;
        string? refused = null;

        void Skip(string reason)
        {
            skipped++;
            skippedBecause ??= reason;
        }

        foreach (var id in ids)
        {
            var video = _library.GetVideo(caller.UserId, id);
            if (video is null)
            {
                Skip("A video is not in a library you can see.");
                continue;
            }

            if (!video.IsLocalFile)
            {
                Skip("A video is not a file on this server, so no subtitle can be saved for it.");
                continue;
            }

            var language = LanguageOf(video.ShowId);
            if (kind is JobKind.Get or JobKind.GetPlain or JobKind.Another && _sources.ReadyFor(language).Count == 0)
            {
                Skip("No subtitle source is set up for " + Languages.NameOf(language) + ".");
                continue;
            }

            var job = new Job
            {
                ItemId = video.Id,
                ShowId = video.ShowId,
                ShowName = video.ShowName,
                ItemLabel = Label(video),
                Language = language,
                Kind = kind,
                Retime = kind != JobKind.Chosen || request.Retime,
                ChosenSource = kind == JobKind.Chosen ? _sources.Find(request.Source)?.Id : null,
                ChosenEntryId = kind == JobKind.Chosen ? request.EntryId : null,
                ChosenFileId = kind == JobKind.Chosen ? request.FileId : null,
                StartedById = caller.UserId,
                StartedBy = caller.Name
            };

            var (queued, reason) = _queue.Enqueue(job);
            if (queued is null)
            {
                refused = reason;
                break;
            }

            if (queued.Id == job.Id)
            {
                started++;
            }
            else
            {
                already++;
            }
        }

        return (new StartView
        {
            Started = started,
            Already = already,
            Skipped = skipped,
            SkippedBecause = skippedBecause,
            Refused = refused,
            JobsVersion = _queue.Version
        }, null);
    }

    public JobsView Jobs(Caller caller, long since)
    {
        // The counter is read first: a change after it is in the next answer, never lost.
        var version = _queue.Version;
        var mayFetch = MayFetch(caller);
        var jobs = _queue.Since(since)
            .Where(entry => CanSee(caller, entry.Job))
            .Select(entry => View(entry.Job, entry.Position, caller, mayFetch))
            .ToList();
        return new JobsView { Version = version, Jobs = jobs, Usage = mayFetch ? _queue.Usage() : null };
    }

    public ServiceError? Cancel(Caller caller, string id)
    {
        var job = _queue.Find(id);
        if (job is null || !CanSee(caller, job))
        {
            return new ServiceError(StatusCodes.Status404NotFound, "The plugin has no such job.");
        }

        if (!MayCancel(caller, job, MayFetch(caller)))
        {
            return JobState.IsActive(job.State)
                ? new ServiceError(StatusCodes.Status403Forbidden, "Only " + job.StartedBy + " or an administrator can cancel this job.")
                : new ServiceError(StatusCodes.Status409Conflict, "The job is already done.");
        }

        return _queue.Cancel(id) ? null : new ServiceError(StatusCodes.Status409Conflict, "The job is already done.");
    }

    public (StartView? View, ServiceError? Error) Retry(Caller caller, string id)
    {
        var job = _queue.Find(id);
        if (job is null || !CanSee(caller, job))
        {
            return (null, new ServiceError(StatusCodes.Status404NotFound, "The plugin has no such job."));
        }

        return Start(caller, new StartJobsRequest
        {
            ItemIds = [job.ItemId],
            Kind = job.Kind,
            Retime = job.Retime,
            Source = job.ChosenSource,
            EntryId = job.ChosenEntryId,
            FileId = job.ChosenFileId
        });
    }

    public ServiceError? ClearFinished(Caller caller)
    {
        if (!caller.IsAdministrator)
        {
            return new ServiceError(StatusCodes.Status403Forbidden, "Only an administrator can clear the list.");
        }

        _queue.ClearFinished();
        return null;
    }

    public async Task<(FilesView? View, ServiceError? Error)> FilesAsync(Caller caller, Guid itemId, CancellationToken cancellationToken)
    {
        if (!MayFetch(caller))
        {
            return (null, Forbidden);
        }

        var video = _library.GetVideo(caller.UserId, itemId);
        if (video is null)
        {
            return (null, NoVideo);
        }

        var language = LanguageOf(video.ShowId);
        var ready = _sources.ReadyFor(language);
        var view = new FilesView
        {
            ItemId = video.Id,
            ShowId = video.ShowId,
            ShowName = video.ShowName,
            ItemLabel = Label(video),
            IsMovie = video.IsMovie,
            Language = language,
            LanguageName = Languages.NameOf(language),
            RetimerReady = JobRunner.RetimerReady(_settings.Current),
            CanSearchEntries = ready.Any(source => source.HasEntries)
        };
        if (ready.Count == 0)
        {
            return (null, new ServiceError(StatusCodes.Status409Conflict, "No subtitle source is set up for " + view.LanguageName + "."));
        }

        var found = await _finder.FindAsync(video, language, _ => { }, cancellationToken, byHand: true).ConfigureAwait(false);
        _queue.AddCost(found.CostUsd);

        var record = _store.Item(itemId, language);
        var options = _finder.Options();
        string NameOf(string source) => _sources.Find(source)?.Name ?? source;
        string? TriedReason(SourceFile file) => record?.Tried.Find(t => t.Source == file.Source && t.FileId == file.Id)?.Reason;
        bool IsCurrent(SourceFile file) => record is { SavedAt: not null } && record.Source == file.Source && record.FileId == file.Id;

        FileView Row(SourceFile file, bool fits, string reason, string? suggestedBy, string? rejectedBefore = null) => new()
        {
            Source = file.Source,
            SourceName = NameOf(file.Source),
            EntryId = file.EntryId,
            FileId = file.Id,
            Name = file.Name,
            Size = file.Size,
            Modified = file.Modified,
            Downloads = file.Downloads,
            HearingImpaired = RulesPicker.IsHearingImpaired(file),
            MachineTranslated = file.MachineTranslated,
            Reason = reason,
            SuggestedBy = suggestedBy,
            Fits = fits,
            Tried = TriedReason(file),
            RejectedBefore = rejectedBefore,
            Current = IsCurrent(file),
            Unusable = RulesPicker.IsUsable(file, options) ? null : UnusableReason(file, options)
        };

        // The model names a few files for an episode. What it left out and the rules still place at this
        // episode by its name belongs to the episode's list too, after the model's.
        var files = found.Candidates
            .Select(candidate => Row(candidate.File, candidate.Auto || candidate.Score >= RulesPicker.AutoScore, candidate.Reason, candidate.SuggestedBy, candidate.RejectedBefore))
            .ToList();
        files.AddRange(found.Others
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => Row(file, false, video.IsMovie ? "Not for this film, by its name" : "For another episode, by its name", null)));

        return (view with
        {
            Files = files,
            Sources = found.Sources.Select(s => new SourceReportView(s.Source, s.Name, s.EntryId, s.EntryName, s.Files, s.MatchedBy, s.Problem)).ToList(),
            Model = found.Model,
            ModelProblem = found.ModelProblem
        }, null);
    }

    public async Task<(IReadOnlyList<EntryView>? Entries, ServiceError? Error)> EntriesAsync(Caller caller, Guid itemId, string? title, CancellationToken cancellationToken)
    {
        if (!MayFetch(caller))
        {
            return (null, Forbidden);
        }

        var video = _library.GetVideo(caller.UserId, itemId);
        if (video is null)
        {
            return (null, NoVideo);
        }

        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200)
        {
            return (null, new ServiceError(StatusCodes.Status400BadRequest, "Type a title of up to 200 letters."));
        }

        var language = LanguageOf(video.ShowId);
        IReadOnlyList<SourceEntry> found;
        try
        {
            found = await _finder.SearchEntriesAsync(video, language, title, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            return (null, new ServiceError(ex.Error == SourceError.RateLimited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status502BadGateway, ex.Message));
        }

        var matches = _store.Show(video.ShowId)?.Matches;
        lock (_cacheLock)
        {
            if (_entries.Count > 2000)
            {
                _entries.Clear();
            }

            foreach (var entry in found)
            {
                _entries[entry.Source + "\n" + entry.Id] = entry;
            }
        }

        return (found
            .Select(entry => new EntryView(
                entry.Source,
                _sources.Find(entry.Source)?.Name ?? entry.Source,
                entry.Id,
                entry.Name,
                entry.EnglishName,
                entry.JapaneseName,
                matches is not null && matches.TryGetValue(FileFinder.MatchKey(entry.Source, video, language), out var match) && match.EntryId == entry.Id))
            .ToList(), null);
    }

    public ServiceError? PinEntry(Caller caller, Guid itemId, PinEntryRequest request)
    {
        if (!MayFetch(caller))
        {
            return Forbidden;
        }

        var video = _library.GetVideo(caller.UserId, itemId);
        if (video is null)
        {
            return NoVideo;
        }

        // Only an entry a search just returned can be chosen, so nothing the browser sends is stored as it is.
        SourceEntry? entry;
        lock (_cacheLock)
        {
            entry = _entries.GetValueOrDefault(request.Source + "\n" + request.EntryId);
        }

        if (entry is null)
        {
            return new ServiceError(StatusCodes.Status404NotFound, "Search again, then choose the entry from the results.");
        }

        var language = LanguageOf(video.ShowId);
        _finder.Pin(video, language, entry, caller.Name);

        // What was tried or found missing with the old entry says nothing about the new one.
        var show = _library.GetShow(caller.UserId, video.ShowId);
        var keys = (show?.Seasons ?? [])
            .Where(season => video.IsMovie || season.Number == video.Season)
            .SelectMany(season => season.Episodes)
            .Select(episode => PluginStore.ItemKey(episode.Id, language))
            .ToList();
        _store.Update(data =>
        {
            foreach (var key in keys)
            {
                if (data.Items.TryGetValue(key, out var item))
                {
                    item.NoFit = false;
                    item.LastError = null;
                    item.Tried.Clear();
                }
            }
        });
        return null;
    }

    public async Task<ServiceError?> RemoveSubtitleAsync(Caller caller, Guid itemId, CancellationToken cancellationToken)
    {
        if (!MayFetch(caller))
        {
            return Forbidden;
        }

        var video = _library.GetVideo(caller.UserId, itemId);
        if (video is null)
        {
            return NoVideo;
        }

        var language = LanguageOf(video.ShowId);
        if (_queue.Active().ContainsKey(PluginStore.ItemKey(itemId, language)))
        {
            return new ServiceError(StatusCodes.Status409Conflict, "A job is running for this video. Cancel it first.");
        }

        var record = _store.Item(itemId, language);
        if (record is null || record.SavedPath.Length == 0)
        {
            return new ServiceError(StatusCodes.Status409Conflict, "The plugin did not save this subtitle, so it leaves it alone. Remove it in Jellyfin.");
        }

        var removed = await _library.RemoveSubtitleAsync(itemId, record.SavedPath, cancellationToken).ConfigureAwait(false);
        if (!removed && video.Subtitles.Any(track => SamePath(track.Path, record.SavedPath)))
        {
            return new ServiceError(StatusCodes.Status502BadGateway, "Jellyfin could not remove the subtitle file.");
        }

        _store.UpdateItem(itemId, language, item =>
        {
            item.SavedPath = string.Empty;
            item.SavedAt = null;
            item.SavedBy = null;
            item.Retimed = false;
            item.Note = null;
            item.KeptDownload = null;
            item.NoFit = false;
            item.LastError = null;
        });

        try
        {
            var folder = Path.Combine(_store.DownloadsDirectory, PluginStore.ItemKey(itemId, language).Replace(':', '-'));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The kept download is only a convenience; a leftover file does no harm.
        }

        return null;
    }

    /// <summary>
    /// Today's use and the limits, for those who may start jobs. What the server spent is not for everyone.
    /// </summary>
    private UsageToday? UsageFor(Caller caller) => MayFetch(caller) ? _queue.Usage() : null;

    private static string UnusableReason(SourceFile file, PickOptions options)
    {
        var guess = FileNameParser.Parse(file.Name);
        if (guess.IsPicture)
        {
            return options.PicturesAllowed
                ? "A picture subtitle of a kind Retimer API cannot read"
                : "A picture subtitle. Converting those is switched off in the settings";
        }

        return guess.IsArchive ? "A pack the plugin cannot open (only zip)" : "Not a subtitle file the plugin can use";
    }

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    /// <summary>
    /// A job may be cancelled by whoever started it and by administrators. A job of watch mode belongs to
    /// nobody in particular, so everyone who may fetch may cancel it.
    /// </summary>
    private static bool MayCancel(Caller caller, Job job, bool mayFetch) =>
        JobState.IsActive(job.State) && mayFetch && (caller.IsAdministrator || job.StartedById == caller.UserId || job.StartedById == Guid.Empty);

    private static JobView View(Job job, int position, Caller caller, bool mayFetch)
    {
        var active = JobState.IsActive(job.State);
        return new JobView
        {
            Id = job.Id,
            ItemId = job.ItemId,
            ShowId = job.ShowId,
            ShowName = job.ShowName,
            ItemLabel = job.ItemLabel,
            Language = job.Language,
            LanguageName = Languages.NameOf(job.Language),
            Kind = job.Kind,
            State = job.State,
            Step = job.Step,
            Steps = job.Steps,
            StepText = active ? job.StepText : string.Empty,
            StepNote = active ? job.StepNote : null,
            Detail = job.Detail,
            Position = position,
            Created = job.Created,
            Started = job.Started,
            Finished = job.Finished,
            StartedBy = job.StartedBy,
            CostUsd = mayFetch ? Math.Round(job.CostUsd, 4) : 0,

            // The list of a running job is still being written, so it is shown when the job is done.
            Tried = active ? [] : job.Tried.Select(t => new TriedView(t.Name, t.Reason)).ToList(),
            MayCancel = MayCancel(caller, job, mayFetch),
            Version = job.Version
        };
    }

    private static EpisodeView Describe(VideoInfo video, string language, ItemRecord? record, (Job Job, int Position)? active, bool retimerReady, Caller caller, bool mayFetch)
    {
        var languageName = Languages.NameOf(language);
        var noun = video.IsMovie ? "film" : "episode";
        var inLanguage = video.Subtitles.Where(track => Languages.Matches(track.Language, language)).ToList();

        // The subtitle the plugin saved, as long as Jellyfin still lists its file.
        TrackInfo? own = null;
        if (record?.SavedAt is not null)
        {
            own = record.SavedPath.Length > 0
                ? video.Subtitles.FirstOrDefault(track => track.IsExternal && SamePath(track.Path, record.SavedPath))
                : inLanguage.FirstOrDefault(track => track.IsExternal);
        }

        var view = new EpisodeView
        {
            Id = video.Id,
            Number = video.Episode,
            Name = video.Name,
            ImageTag = video.ImageTag,
            Minutes = video.RunTimeTicks is { } ticks ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes) : null,
            CanSave = video.IsLocalFile,
            FromPlugin = own is not null,
            FileName = own is null ? null : record!.FileName,
            Source = own is null ? null : record!.Source,
            PickedBy = own is null ? null : record!.PickedBy
        };

        if (active is { } running)
        {
            var job = running.Job;
            if (job.State == JobState.Working)
            {
                return view with
                {
                    State = "working",
                    Text = job.StepText.Length == 0 ? "Starting" : job.StepText,
                    Detail = job.StepNote,
                    JobId = job.Id,
                    Step = job.Step,
                    Steps = job.Steps,
                    MayCancel = MayCancel(caller, job, mayFetch)
                };
            }

            return view with
            {
                State = "queued",
                Text = "Queued",
                Detail = running.Position <= 1
                    ? "Starts when the one before it is done"
                    : string.Create(CultureInfo.InvariantCulture, $"{running.Position - 1} more before it"),
                JobId = job.Id,
                MayCancel = MayCancel(caller, job, mayFetch)
            };
        }

        if (own is not null)
        {
            return view with
            {
                State = record!.Retimed ? "retimed" : "raw",
                Text = languageName + " subtitle, " + (record.Retimed ? "retimed" : "not retimed"),
                Detail = record.Note,
                CanRetime = !record.Retimed && retimerReady && !string.IsNullOrEmpty(record.KeptDownload) && SubtitleText.IsRetimable(record.KeptDownload)
            };
        }

        if (inLanguage.Count > 0)
        {
            return view with
            {
                State = "present",
                Text = languageName + " subtitle",
                Detail = (inLanguage.Any(track => !track.IsExternal) ? "In the video file" : "A file next to the video")
                    + (record?.LastError is null ? string.Empty : ". The last try to get another failed: " + record.LastError)
            };
        }

        if (record?.LastError is not null)
        {
            return view with { State = "failed", Text = "Failed", Detail = record.LastError };
        }

        if (record is { NoFit: true })
        {
            return view with { State = "nofit", Text = "No file fits this " + noun };
        }

        return view with { State = "none", Text = "No " + languageName + " subtitle" };
    }

    private bool CanSee(Caller caller, Job job)
    {
        if (caller.IsAdministrator || job.StartedById == caller.UserId)
        {
            return true;
        }

        var key = (caller.UserId, job.ShowId);
        lock (_cacheLock)
        {
            if (_visible.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < _visibleLifetime)
            {
                return cached.Visible;
            }
        }

        var visible = _library.CanSee(caller.UserId, job.ShowId);
        lock (_cacheLock)
        {
            if (_visible.Count > 5000)
            {
                _visible.Clear();
            }

            _visible[key] = (DateTimeOffset.UtcNow, visible);
        }

        return visible;
    }
}
