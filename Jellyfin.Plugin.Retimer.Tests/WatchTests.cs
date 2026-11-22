using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Jellyfin.Plugin.Retimer.Watch;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// Watch mode: shows, seasons and films that get their subtitles by themselves, once a source has them.
/// </summary>
public sealed class WatchTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly World _w = new() { Now = T0 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    /// <summary>Fate/Zero with three episodes in season 1 and two in season 2, each with a file at the source.</summary>
    private (Guid Show, IReadOnlyList<VideoInfo> First, IReadOnlyList<VideoInfo> Second) TwoSeasons()
    {
        var first = _w.FateZero(3);
        var show = first[0].ShowId;
        var second = new List<VideoInfo>();
        for (var i = 4; i <= 5; i++)
        {
            second.Add(_w.Library.AddEpisode(show, "Fate/Zero", 2, i, "Episode " + i));
            _w.Source.AddFile($"[Group] Fate Zero - {i:00} [BD].ja.srt", "384");
        }

        return (show, first, second);
    }

    private async Task<WatchRun> CheckAsync(Guid? only = null)
    {
        var run = await _w.Watch.CheckAsync(only, null, Token);
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        return run;
    }

    private List<Job> Jobs() => _w.Queue.Since(0).Select(entry => entry.Job).ToList();

    [Fact]
    public void A_season_follows_the_whole_show_unless_it_was_switched_on_its_own()
    {
        var (show, _, _) = TwoSeasons();
        Assert.False(WatchService.Rule(_w.Store.Show(show), 1).On);

        _w.Watch.SetSeason(show, 2, true);
        Assert.False(WatchService.Rule(_w.Store.Show(show), 1).On);
        Assert.Equal(new WatchRule(true, T0), WatchService.Rule(_w.Store.Show(show), 2));
        Assert.True(WatchService.AnyWatched(_w.Store.Show(show)));

        // The whole show: every season, and the seasons switched on their own follow it again.
        _w.Now = T0.AddDays(1);
        _w.Watch.SetShow(show, true);
        Assert.Empty(_w.Store.Show(show)!.Seasons);
        Assert.Equal(new WatchRule(true, T0.AddDays(1)), WatchService.Rule(_w.Store.Show(show), 1));
        Assert.True(WatchService.Rule(_w.Store.Show(show), 7).On, "a season added later is watched too");

        // One season off, and on again: then it agrees with the show and keeps no rule of its own.
        _w.Watch.SetSeason(show, 1, false);
        Assert.False(WatchService.Rule(_w.Store.Show(show), 1).On);
        Assert.True(WatchService.Rule(_w.Store.Show(show), 2).On);
        _w.Watch.SetSeason(show, 1, true);
        Assert.Empty(_w.Store.Show(show)!.Seasons);

        _w.Watch.SetShow(show, false);
        Assert.False(WatchService.AnyWatched(_w.Store.Show(show)));
    }

    [Theory]
    [InlineData("1", true, 1)]
    [InlineData("-", true, null)]
    [InlineData("12", true, 12)]
    [InlineData("x", false, null)]
    [InlineData("-1", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void Season_keys_are_numbers_or_a_dash(string? key, bool valid, int? season)
    {
        Assert.Equal(valid, WatchService.TryParseSeasonKey(key, out var read));
        Assert.Equal(season, read);
        if (valid)
        {
            Assert.Equal(key, WatchService.SeasonKey(read));
        }
    }

    [Fact]
    public async Task A_check_starts_a_job_only_for_the_watched_episodes_without_a_subtitle()
    {
        var (show, first, second) = TwoSeasons();
        _w.Library.Videos[second[0].Id] = second[0] with { Subtitles = [new TrackInfo { Index = 3, Language = "jpn", Codec = "ass", IsText = true }] };
        _w.Watch.SetSeason(show, 2, true);

        var run = await CheckAsync();

        Assert.Null(run.Problem);
        Assert.Equal(1, run.Shows);
        Assert.Equal(1, run.LookedFor);
        Assert.Equal(1, run.JobsStarted);
        var job = Assert.Single(Jobs());
        Assert.Equal(second[1].Id, job.ItemId);
        Assert.Equal(WatchService.StartedByName, job.StartedBy);
        Assert.Equal(Guid.Empty, job.StartedById);
        Assert.Equal(JobKind.Get, job.Kind);
        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Equal(second[1].Id, Assert.Single(_w.Library.Saved).Item);
        Assert.Equal(T0, _w.Store.Item(second[1].Id, "ja")!.WatchQueuedAt);
        Assert.DoesNotContain(_w.Library.Saved, saved => first.Any(video => video.Id == saved.Item));

        // The full check is remembered for the Watching tab.
        var last = _w.Watch.LastRun();
        Assert.NotNull(last);
        Assert.Equal(1, last.JobsStarted);
        Assert.Equal(T0, last.Started);
    }

    [Fact]
    public async Task A_check_that_finds_no_new_file_starts_nothing()
    {
        var (show, first, _) = TwoSeasons();
        var lonely = _w.Library.AddEpisode(show, "Fate/Zero", 1, 9, "Episode 9");
        _w.Watch.SetShow(show, true);

        var run = await CheckAsync();
        Assert.Equal(6, run.LookedFor);
        Assert.Equal(5, run.JobsStarted);
        Assert.Equal(5, _w.Library.Saved.Count);

        // Episode 9 has no file at the source, and the others have their subtitles now.
        var again = await CheckAsync();
        Assert.Equal(1, again.LookedFor);
        Assert.Equal(0, again.JobsStarted);
        Assert.Equal(5, Jobs().Count);
        Assert.Null(_w.Store.Item(lonely.Id, "ja")?.WatchQueuedAt);

        // A file that was tried and did not fit is not tried again by watch mode.
        _w.Store.UpdateItem(first[0].Id, "ja", item =>
        {
            item.SavedPath = string.Empty;
            item.Tried.Add(new TriedFile { Source = "jimaku", FileId = "[Group] Fate Zero - 01 [BD].ja.srt", Name = "[Group] Fate Zero - 01 [BD].ja.srt", Reason = "did not fit" });
        });
        _w.Library.Videos[first[0].Id] = _w.Library.Videos[first[0].Id] with { Subtitles = [] };
        var third = await CheckAsync();
        Assert.Equal(2, third.LookedFor);
        Assert.Equal(0, third.JobsStarted);
    }

    [Fact]
    public async Task Episodes_are_looked_for_for_the_set_number_of_days_after_they_came_or_were_watched()
    {
        var (show, first, _) = TwoSeasons();
        foreach (var video in first)
        {
            _w.Library.Videos[video.Id] = video with { Added = T0.AddDays(-100) };
        }

        _w.Library.Videos[first[2].Id] = first[2] with { Added = T0.AddDays(10) };
        _w.Settings.Current.WatchDays = 30;
        _w.Watch.SetSeason(show, 1, true);

        // Switched on today: old episodes get their 30 days from today, a newer one from the day it came.
        _w.Now = T0.AddDays(31);
        var view = _w.Page.Show(_w.Admin, show)!;
        var episodes = view.Seasons.Single(season => season.Number == 1).Episodes;
        Assert.All(episodes.Take(2), episode => Assert.True(episode.Watched && episode.WatchOver));
        Assert.Equal(T0.AddDays(30), episodes[0].WatchUntil);
        Assert.False(episodes[2].WatchOver);
        Assert.Equal(T0.AddDays(40), episodes[2].WatchUntil);
        Assert.All(view.Seasons.Single(season => season.Number == 2).Episodes, episode => Assert.False(episode.Watched));

        var watching = Assert.Single(_w.Page.Watching(_w.Admin).Shows);
        Assert.Equal(2, watching.Over);
        Assert.Equal(first[2].Id, Assert.Single(watching.Waiting).Id);

        var run = await CheckAsync();
        Assert.Equal(1, run.LookedFor);
        Assert.Equal(first[2].Id, Assert.Single(Jobs()).ItemId);

        // 0 days: for as long as the season is watched.
        _w.Settings.Current.WatchDays = 0;
        Assert.Null(_w.Page.Show(_w.Admin, show)!.Seasons[0].Episodes[0].WatchUntil);
        Assert.Equal(2, (await CheckAsync()).JobsStarted);
    }

    [Fact]
    public async Task After_a_failed_job_watch_mode_waits_before_it_tries_the_episode_again()
    {
        var (show, first, _) = TwoSeasons();
        _w.Watch.SetSeason(show, 1, true);
        _w.Library.Videos[first[1].Id] = first[1] with { Subtitles = [new TrackInfo { Index = 3, Language = "ja", Codec = "srt", IsText = true }] };
        _w.Library.Videos[first[2].Id] = first[2] with { Subtitles = [new TrackInfo { Index = 3, Language = "ja", Codec = "srt", IsText = true }] };
        _w.Store.UpdateItem(first[0].Id, "ja", item =>
        {
            item.LastError = "Jimaku did not answer.";
            item.WatchQueuedAt = T0.AddHours(-2);
        });

        Assert.Equal(0, (await CheckAsync()).LookedFor);

        _w.Now = T0.AddHours(19);
        Assert.Equal(1, (await CheckAsync()).JobsStarted);
    }

    [Fact]
    public async Task An_episode_Jellyfin_has_not_read_yet_or_that_waits_in_the_queue_is_left_for_later()
    {
        var (show, first, _) = TwoSeasons();
        _w.Watch.SetSeason(show, 1, true);
        _w.Library.Videos[first[0].Id] = first[0] with { StreamsRead = false };
        _w.Library.Videos[first[1].Id] = first[1] with { IsLocalFile = false };
        var gate = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages => { gate.Task.Wait(Token); return map(messages); };
        _w.Queue.Enqueue(_w.JobFor(first[2]));

        var run = await _w.Watch.CheckAsync(null, null, Token);
        gate.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(0, run.LookedFor);
        Assert.Single(Jobs());
    }

    [Fact]
    public async Task A_new_episode_Jellyfin_reads_a_little_later_is_looked_at_again_by_itself()
    {
        var (show, _, second) = TwoSeasons();
        _w.Watch.SetSeason(show, 2, true);
        _w.Library.Videos[second[0].Id] = second[0] with { StreamsRead = false };
        _w.Library.Videos[second[1].Id] = second[1] with { Subtitles = [new TrackInfo { Index = 3, Language = "ja", Codec = "srt", IsText = true }] };

        var first = await _w.Watch.CheckAsync(show, null, Token);
        Assert.Equal(0, first.LookedFor);
        _w.Library.Videos[second[0].Id] = second[0];

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!Jobs().Any(job => job.State is JobState.SavedRaw) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Token);
        }

        var job = Assert.Single(Jobs());
        Assert.Equal(second[0].Id, job.ItemId);
        Assert.Equal(WatchService.StartedByName, job.StartedBy);
    }

    [Fact]
    public async Task The_days_limit_stops_the_check_and_says_why()
    {
        var (show, _, _) = TwoSeasons();
        _w.Settings.Current.MaxJobsPerDay = 1;
        _w.Watch.SetShow(show, true);

        var run = await CheckAsync();

        Assert.Equal(1, run.JobsStarted);
        Assert.Contains("limit of 1 job", run.Problem, StringComparison.Ordinal);
        Assert.Equal(run.Problem, _w.Watch.LastRun()!.Problem);
        Assert.Single(Jobs());
    }

    [Fact]
    public async Task Without_a_model_the_check_says_so_and_starts_nothing()
    {
        var (show, _, _) = TwoSeasons();
        _w.Model.Current = null;
        _w.Watch.SetShow(show, true);

        var run = await CheckAsync();

        Assert.Equal(FileFinder.NoModel, run.Problem);
        Assert.Empty(Jobs());
        Assert.Empty(_w.Source.Listings);
    }

    [Fact]
    public async Task Nothing_watched_costs_nothing()
    {
        TwoSeasons();

        var run = await CheckAsync();

        Assert.Equal(0, run.Shows);
        Assert.Null(run.Problem);
        Assert.Empty(_w.Model.Calls);
        Assert.Empty(_w.Source.Searches);
    }

    [Fact]
    public async Task Watch_new_shows_takes_in_the_shows_that_come_after_it_was_switched_on()
    {
        var (show, _, _) = TwoSeasons();
        var older = Guid.NewGuid();
        var refused = Guid.NewGuid();
        _w.Library.AddEpisode(older, "Older Show", 1, 1, "Pilot");
        _w.Library.AddEpisode(refused, "Refused Show", 1, 1, "Pilot");
        _w.Settings.Current.WatchNewShows = true;
        _w.Store.Update(data => data.WatchNewShowsSince = T0);
        _w.Library.ShowsAdded[show] = T0.AddHours(1);
        _w.Library.ShowsAdded[older] = T0.AddHours(-1);
        _w.Library.ShowsAdded[refused] = T0.AddHours(2);
        _w.Watch.SetShow(refused, false);

        var run = await CheckAsync();

        Assert.True(_w.Store.Show(show)!.Watch);
        Assert.Null(_w.Store.Show(older)?.Watch);
        Assert.False(_w.Store.Show(refused)!.Watch);
        Assert.Equal(1, run.Shows);
        Assert.Equal(5, run.JobsStarted);

        // Switched off, the setting takes in nothing more.
        _w.Settings.Current.WatchNewShows = false;
        _w.Library.ShowsAdded[older] = T0.AddHours(3);
        await CheckAsync();
        Assert.Null(_w.Store.Show(older)?.Watch);
    }

    [Fact]
    public async Task A_watched_show_with_a_new_episode_is_checked_soon_by_itself()
    {
        var (show, _, _) = TwoSeasons();
        var other = Guid.NewGuid();
        _w.Library.AddEpisode(other, "Fate/Zero", 1, 1, "Unwatched");
        _w.Watch.SetSeason(show, 2, true);

        _w.Watch.Soon(other);
        _w.Watch.Soon(show);
        _w.Watch.Soon(show);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Jobs().Count(job => job.State is JobState.SavedRaw or JobState.Saved) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Token);
        }

        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        var jobs = Jobs();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Equal(WatchService.StartedByName, job.StartedBy));
        Assert.All(jobs, job => Assert.Equal(show, job.ShowId));

        // A check of one show is not the scheduled check of all.
        Assert.Null(_w.Watch.LastRun());
    }

    [Fact]
    public void Watching_is_switched_by_those_who_may_fetch_for_a_show_or_a_season_it_has()
    {
        var (show, _, _) = TwoSeasons();
        _w.Model.Current = null;

        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.SetWatch(_w.Viewer, show, new WatchRequest { Watch = true })!.Status);
        Assert.Equal(StatusCodes.Status404NotFound, _w.Page.SetWatch(_w.Manager, Guid.NewGuid(), new WatchRequest { Watch = true })!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.SetWatch(_w.Manager, show, new WatchRequest { Watch = true, Season = "7" })!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.SetWatch(_w.Manager, show, new WatchRequest { Watch = true, Season = "two" })!.Status);
        Assert.False(WatchService.AnyWatched(_w.Store.Show(show)));

        Assert.Null(_w.Page.SetWatch(_w.Manager, show, new WatchRequest { Watch = true, Season = "2" }));
        Assert.True(WatchService.Rule(_w.Store.Show(show), 2).On);
        Assert.False(WatchService.Rule(_w.Store.Show(show), 1).On);

        Assert.Null(_w.Page.SetWatch(_w.Manager, show, new WatchRequest { Watch = true }));
        Assert.Null(_w.Page.SetWatch(_w.Manager, show, new WatchRequest { Watch = false, Season = "1" }));
        var view = _w.Page.Show(_w.Manager, show)!;
        Assert.True(view.WatchWhole);
        Assert.Equal(["1", "2"], view.Seasons.Select(season => season.Key));
        Assert.Equal([false, true], view.Seasons.Select(season => season.Watched));
        Assert.True(Assert.Single(_w.Page.Shows(_w.Manager, FakeLibrary.LibraryId)!).Watched);

        _w.Settings.Current.FetchAccess = "everyone";
        Assert.Null(_w.Page.SetWatch(_w.Viewer, show, new WatchRequest { Watch = false }));
        Assert.False(Assert.Single(_w.Page.Shows(_w.Manager, FakeLibrary.LibraryId)!).Watched);
    }

    [Fact]
    public async Task The_watching_list_says_what_is_still_looked_for_and_how_the_last_check_went()
    {
        var (show, first, second) = TwoSeasons();
        var lonely = _w.Library.AddEpisode(show, "Fate/Zero", 1, 9, "Episode 9");
        _w.Watch.SetShow(show, true);
        _w.Watch.SetSeason(show, 2, false);
        _w.Store.UpdateItem(first[0].Id, "ja", item => item.LastError = "Jimaku did not answer.");
        _w.Store.UpdateItem(first[1].Id, "ja", item => item.NoFit = true);

        var before = _w.Page.Watching(_w.Manager);
        Assert.Null(before.LastCheck);
        Assert.False(before.MayCheck);
        Assert.True(before.Scheduled);
        Assert.Equal(3.0, before.EveryHours);
        Assert.Equal(30, before.Days);
        var watched = Assert.Single(before.Shows);
        Assert.True(watched.Whole);
        Assert.Equal(["Season 1"], watched.Seasons);
        Assert.Equal(["Season 2"], watched.Skipped);
        Assert.Equal("Japanese", watched.LanguageName);
        Assert.Equal(["failed", "nofit", "none", "none"], watched.Waiting.Select(video => video.State));
        Assert.DoesNotContain(watched.Waiting, video => second.Any(s => s.Id == video.Id));

        await CheckAsync();

        var after = _w.Page.Watching(_w.Admin);
        Assert.True(after.MayCheck);
        Assert.NotNull(after.LastCheck);
        Assert.Equal(lonely.Id, Assert.Single(Assert.Single(after.Shows).Waiting).Id);

        // Someone who cannot see the show does not see it here either.
        _w.Library.Blind.Add(_w.Viewer.UserId);
        Assert.Empty(_w.Page.Watching(_w.Viewer).Shows);
    }

    [Fact]
    public void Only_an_administrator_starts_the_check_of_all_watched_shows()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.CheckWatchedNow(_w.Manager)!.Status);
        Assert.Equal(0, _w.Schedule.Runs);

        Assert.Null(_w.Page.CheckWatchedNow(_w.Admin));
        Assert.Equal(1, _w.Schedule.Runs);
    }

    [Fact]
    public async Task A_job_of_watch_mode_may_be_cancelled_by_anyone_who_may_fetch()
    {
        var (_, first, _) = TwoSeasons();
        var gate = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages => { gate.Task.Wait(Token); return map(messages); };
        _w.Queue.Enqueue(_w.JobFor(first[0]));
        var job = _w.JobFor(first[1]);
        job.StartedById = Guid.Empty;
        job.StartedBy = WatchService.StartedByName;
        _w.Queue.Enqueue(job);

        Assert.True(_w.Page.Jobs(_w.Manager, 0).Jobs.Single(view => view.Id == job.Id).MayCancel);
        Assert.False(_w.Page.Jobs(_w.Viewer, 0).Jobs.Single(view => view.Id == job.Id).MayCancel);
        Assert.Null(_w.Page.Cancel(_w.Manager, job.Id));

        gate.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Equal(JobState.Cancelled, _w.Queue.Find(job.Id)!.State);
    }

    [Fact]
    public void The_scheduled_task_runs_every_three_hours_until_an_administrator_changes_it()
    {
        var task = new WatchTask(null!);

        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.IntervalTicks);
        Assert.Equal("SubtitleRetimerWatch", task.Key);
        Assert.Equal(Plugin.PluginName, task.Category);
    }
}
