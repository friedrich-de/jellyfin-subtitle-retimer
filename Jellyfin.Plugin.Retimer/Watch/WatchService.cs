using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Watch;

/// <summary>
/// When Jellyfin runs the check of the watched shows, and a way to start it now.
/// </summary>
internal interface IWatchSchedule
{
    /// <summary>Gets the shortest time between two checks that Jellyfin's schedule for the task holds, or <c>null</c> without one.</summary>
    TimeSpan? Interval { get; }

    /// <summary>Gets a value indicating whether the task has a trigger at all, so that it runs by itself.</summary>
    bool Scheduled { get; }

    /// <summary>Asks Jellyfin to run the check now, unless it runs already.</summary>
    void RunNow();
}

/// <summary>
/// Whether a season is watched, and since when.
/// </summary>
/// <param name="On">Whether the season is watched.</param>
/// <param name="Since">When it was switched to watched.</param>
internal readonly record struct WatchRule(bool On, DateTimeOffset? Since);

/// <summary>
/// Watch mode: a show, a season or a film that is watched gets a subtitle for each of its videos that has none,
/// as soon as a source has one. A check runs on Jellyfin's schedule, and for a show soon after it got a new episode.
/// A check only looks: it starts a job for a video when the sources list a file for it that was not tried yet,
/// so a check that finds nothing new costs no job and, with the model's answers kept, no model call either.
/// </summary>
internal sealed class WatchService : IDisposable
{
    /// <summary>The name the activity list shows for jobs that watch mode started.</summary>
    internal const string StartedByName = "Watch mode";

    /// <summary>Sets how long after a new episode its show is checked, in seconds. For tests.</summary>
    internal const string DelayVariable = "RETIMER_WATCH_DELAY_SECONDS";

    /// <summary>How long after a new episode came into the library its show is checked: time for Jellyfin to read its streams.</summary>
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(3);

    /// <summary>After a job of watch mode failed, the video waits this long before watch mode tries it again.</summary>
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(20);

    /// <summary>How often a show is checked again because Jellyfin had not read a new episode yet, before the schedule takes over.</summary>
    internal const int UnreadRetries = 5;

    private readonly IPluginSettings _settings;
    private readonly IVideoLibrary _library;
    private readonly SubtitleSources _sources;
    private readonly FileFinder _finder;
    private readonly JobQueue _queue;
    private readonly PluginStore _store;
    private readonly ModelPicker _model;
    private readonly IWatchSchedule _schedule;
    private readonly ILogger<WatchService> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _delay;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Lock _timersLock = new();
    private readonly Dictionary<Guid, Timer> _soon = [];
    private readonly Dictionary<Guid, int> _unreadRetries = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public WatchService(
        IPluginSettings settings,
        IVideoLibrary library,
        SubtitleSources sources,
        FileFinder finder,
        JobQueue queue,
        PluginStore store,
        ModelPicker model,
        IWatchSchedule schedule,
        ILogger<WatchService> logger,
        Func<DateTimeOffset>? now = null,
        TimeSpan? delay = null)
    {
        _settings = settings;
        _library = library;
        _sources = sources;
        _finder = finder;
        _queue = queue;
        _store = store;
        _model = model;
        _schedule = schedule;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? DelayFromEnvironment();
    }

    /// <summary>Gets when and how often Jellyfin runs the check.</summary>
    public IWatchSchedule Schedule => _schedule;

    /// <summary>Gets the time now, as watch mode reads it.</summary>
    public DateTimeOffset Now => _now();

    /// <summary>The key a season is stored under: its number, or "-" for episodes without one and for a film.</summary>
    public static string SeasonKey(int? season) => season?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Reads a season key back. <c>false</c> for a key that is neither a number nor "-".</summary>
    public static bool TryParseSeasonKey(string? key, out int? season)
    {
        season = null;
        if (key == "-")
        {
            return true;
        }

        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < 10000)
        {
            season = number;
            return true;
        }

        return false;
    }

    /// <summary>Whether a season of a show is watched: on its own, or because the whole show is.</summary>
    public static WatchRule Rule(ShowRecord? show, int? season)
    {
        if (show is null)
        {
            return new WatchRule(false, null);
        }

        if (show.Seasons.TryGetValue(SeasonKey(season), out var own))
        {
            return new WatchRule(own.On, own.On ? own.Since : null);
        }

        return show.Watch == true ? new WatchRule(true, show.WatchSince) : new WatchRule(false, null);
    }

    /// <summary>Whether any season of the show, or the film, is watched.</summary>
    public static bool AnyWatched(ShowRecord? show) => show is not null && (show.Watch == true || show.Seasons.Values.Any(season => season.On));

    /// <summary>
    /// Until when watch mode looks for a video's subtitle: a number of days after the video came into the library,
    /// or after its season was switched to watched when that was later. <c>null</c> when it looks for as long as the season is watched.
    /// </summary>
    public static DateTimeOffset? Until(VideoInfo video, WatchRule rule, int days)
    {
        if (days <= 0)
        {
            return null;
        }

        var start = (video.Added, rule.Since) switch
        {
            ({ } added, { } since) => added > since ? added : since,
            ({ } added, null) => added,
            (null, { } since) => since,
            _ => (DateTimeOffset?)null
        };
        return start?.AddDays(days);
    }

    /// <summary>Whether a video has a subtitle in the language, inside its file or next to it.</summary>
    public static bool HasSubtitle(VideoInfo video, string language) => video.Subtitles.Any(track => Languages.Matches(track.Language, language));

    /// <summary>
    /// Switches a whole show or film to watched, or not. Seasons that were switched on their own follow it again.
    /// </summary>
    public void SetShow(Guid showId, bool on)
    {
        var now = _now();
        _store.UpdateShow(showId, show =>
        {
            show.Watch = on;
            show.WatchSince = on ? now : null;
            show.Seasons.Clear();
        });
    }

    /// <summary>
    /// Switches one season to watched, or not. A season that agrees with the whole show again follows it.
    /// </summary>
    public void SetSeason(Guid showId, int? season, bool on)
    {
        var now = _now();
        var key = SeasonKey(season);
        _store.UpdateShow(showId, show =>
        {
            if (on == (show.Watch == true))
            {
                show.Seasons.Remove(key);
            }
            else
            {
                show.Seasons[key] = new SeasonWatch { On = on, Since = now };
            }
        });
    }

    /// <summary>Gets how the last check of all watched shows went, or <c>null</c> before the first.</summary>
    public WatchRun? LastRun() => _store.Read(data => data.LastWatch?.Clone());

    /// <summary>
    /// Asks for a check of one show soon, because it got a new episode, or is new itself. Several new episodes
    /// in a row lead to one check, a while after the last of them.
    /// </summary>
    public void Soon(Guid showId)
    {
        if (_disposed || showId == Guid.Empty)
        {
            return;
        }

        var record = _store.Show(showId);
        var mayBeNew = _settings.Current.WatchNewShows && record?.Watch is null;
        if (!AnyWatched(record) && !mayBeNew)
        {
            return;
        }

        lock (_timersLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_soon.TryGetValue(showId, out var waiting))
            {
                waiting.Change(_delay, Timeout.InfiniteTimeSpan);
                return;
            }

            _soon[showId] = new Timer(_ => Fire(showId), null, _delay, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Watch mode checks show {ShowId} in {Delay}, because it has something new", showId, _delay);
    }

    /// <summary>
    /// Checks one show right away, in the background: after someone switched it, or one of its seasons, to watched.
    /// </summary>
    public void CheckShowNow(Guid showId)
    {
        CancellationToken token;
        lock (_timersLock)
        {
            if (_disposed)
            {
                return;
            }

            token = _shutdown.Token;
        }

        _ = Task.Run(() => CheckSoonAsync(showId, token), token);
    }

    /// <summary>
    /// Looks for the subtitles of the watched videos that have none, and starts a job for each video the
    /// sources have a file for that was not tried yet. One check runs at a time.
    /// </summary>
    /// <param name="onlyShow">One show to check, or <c>null</c> for all watched shows.</param>
    /// <param name="progress">Told how far the check is, from 0 to 100.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>What the check did.</returns>
    public async Task<WatchRun> CheckAsync(Guid? onlyShow, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CheckCoreAsync(onlyShow, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    public void Dispose()
    {
        lock (_timersLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var timer in _soon.Values)
            {
                timer.Dispose();
            }

            _soon.Clear();
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
        _running.Dispose();
    }

    private static TimeSpan DelayFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable(DelayVariable);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 86400
            ? TimeSpan.FromSeconds(seconds)
            : DefaultDelay;
    }

    private void Fire(Guid showId)
    {
        CancellationToken token;
        lock (_timersLock)
        {
            if (_soon.Remove(showId, out var timer))
            {
                timer.Dispose();
            }

            if (_disposed)
            {
                return;
            }

            token = _shutdown.Token;
        }

        _ = Task.Run(() => CheckSoonAsync(showId, token), token);
    }

    private async Task CheckSoonAsync(Guid showId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await CheckAsync(showId, null, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Watch mode checked show {ShowId}: {Jobs} jobs started{Problem}", showId, run.JobsStarted, run.Problem is null ? string.Empty : " (" + run.Problem + ")");
        }
        catch (OperationCanceledException)
        {
            // The server is going down.
        }
        catch (ObjectDisposedException)
        {
            // The same.
        }
#pragma warning disable CA1031 // A failed check of one show must not take the server's event down with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Watch mode could not check show {ShowId}", showId);
        }
    }

    private async Task<WatchRun> CheckCoreAsync(Guid? onlyShow, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var run = new WatchRun { Started = _now() };
        try
        {
            MarkNewShows(onlyShow);
            var shows = _store.Read(data => data.Shows
                .Where(pair => AnyWatched(pair.Value))
                .Select(pair => Guid.TryParseExact(pair.Key, "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty && (onlyShow is null || id == onlyShow))
                .ToList());

            if (shows.Count > 0 && _model.Model is null)
            {
                run.Problem = FileFinder.NoModel;
                return run;
            }

            for (var i = 0; i < shows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(100.0 * i / shows.Count);
                var problem = await CheckShowAsync(shows[i], run, cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    run.Problem = problem;
                    break;
                }
            }

            progress?.Report(100);
            return run;
        }
        finally
        {
            run.Finished = _now();
            if (onlyShow is null)
            {
                var saved = run.Clone();
                _store.Update(data => data.LastWatch = saved);
            }
        }
    }

    /// <summary>
    /// Marks the shows that came into the library since "watch new shows" was switched on as watched,
    /// unless someone switched them off already.
    /// </summary>
    private void MarkNewShows(Guid? onlyShow)
    {
        if (!_settings.Current.WatchNewShows)
        {
            return;
        }

        var since = _store.Read(data => data.WatchNewShowsSince);
        if (since is null)
        {
            return;
        }

        var now = _now();
        foreach (var id in _library.GetShowsAddedSince(since.Value).Where(id => onlyShow is null || id == onlyShow))
        {
            var record = _store.Show(id);
            if (record is not null && (record.Watch is not null || record.Seasons.Count > 0))
            {
                continue;
            }

            _store.UpdateShow(id, show =>
            {
                show.Watch = true;
                show.WatchSince = now;
            });
        }
    }

    /// <summary>
    /// Checks one show. Returns why the whole check has to stop, such as a daily limit, or <c>null</c>.
    /// </summary>
    private async Task<string?> CheckShowAsync(Guid showId, WatchRun run, CancellationToken cancellationToken)
    {
        var details = _library.GetShow(Guid.Empty, showId);
        if (details is null)
        {
            return null;
        }

        run.Shows++;
        var record = _store.Show(showId);
        var language = Languages.Normalize(record?.Language) ?? Languages.Normalize(_settings.Current.SubtitleLanguage) ?? Languages.Default;
        if (_sources.ReadyFor(language).Count == 0)
        {
            return null;
        }

        var days = _settings.Current.WatchDays;
        var now = _now();
        var active = _queue.Active();
        var unread = false;
        foreach (var season in details.Seasons)
        {
            var rule = Rule(record, season.Number);
            if (!rule.On)
            {
                continue;
            }

            foreach (var video in season.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (video.IsLocalFile && !video.StreamsRead)
                {
                    unread = true;
                    continue;
                }

                if (!Wanted(video, rule, language, days, now, active))
                {
                    continue;
                }

                run.LookedFor++;
                var refusal = _queue.Refusal();
                if (refusal is not null)
                {
                    return refusal;
                }

                var found = await _finder.FindAsync(video, language, _ => { }, cancellationToken).ConfigureAwait(false);
                _queue.AddCost(found.CostUsd);
                if (found.NeedsChoice)
                {
                    return found.ModelProblem is null || found.ModelProblem == FileFinder.NoModel
                        ? FileFinder.NoModel
                        : "The model gave no answer: " + found.ModelProblem;
                }

                var tried = (_store.Item(video.Id, language)?.Tried ?? [])
                    .Select(file => file.Source + "\n" + file.FileId)
                    .ToHashSet(StringComparer.Ordinal);
                if (!found.Candidates.Any(candidate => candidate.Auto && !tried.Contains(candidate.File.Source + "\n" + candidate.File.Id)))
                {
                    continue;
                }

                var job = new Job
                {
                    ItemId = video.Id,
                    ShowId = video.ShowId,
                    ShowName = video.ShowName,
                    ItemLabel = Api.PageService.Label(video),
                    Language = language,
                    Kind = JobKind.Get,
                    StartedById = Guid.Empty,
                    StartedBy = StartedByName
                };
                var (queued, refused) = _queue.Enqueue(job);
                if (queued is null)
                {
                    return refused;
                }

                if (queued.Id == job.Id)
                {
                    run.JobsStarted++;
                    _store.UpdateItem(video.Id, language, item => item.WatchQueuedAt = now);
                }
            }
        }

        AfterUnread(showId, unread);
        return null;
    }

    /// <summary>
    /// A new episode that Jellyfin has not read yet is looked at again a few minutes later, a few times,
    /// so it does not wait for the next scheduled check.
    /// </summary>
    private void AfterUnread(Guid showId, bool unread)
    {
        lock (_timersLock)
        {
            if (!unread)
            {
                _unreadRetries.Remove(showId);
                return;
            }

            var tries = _unreadRetries.GetValueOrDefault(showId);
            if (tries >= UnreadRetries)
            {
                return;
            }

            _unreadRetries[showId] = tries + 1;
        }

        Soon(showId);
    }

    /// <summary>
    /// Whether watch mode looks for this video's subtitle now: it is a file of this server that Jellyfin has read,
    /// it has no subtitle in the language, no job waits or runs for it, it is inside its days, and its last job
    /// from watch mode did not fail just now.
    /// </summary>
    private bool Wanted(VideoInfo video, WatchRule rule, string language, int days, DateTimeOffset now, IReadOnlyDictionary<string, (Job Job, int Position)> active)
    {
        if (!video.IsLocalFile || !video.StreamsRead || HasSubtitle(video, language) || active.ContainsKey(PluginStore.ItemKey(video.Id, language)))
        {
            return false;
        }

        if (Until(video, rule, days) is { } until && now > until)
        {
            return false;
        }

        var item = _store.Item(video.Id, language);
        return item is not { LastError: not null, WatchQueuedAt: { } queuedAt } || now - queuedAt >= RetryAfterFailure;
    }
}
