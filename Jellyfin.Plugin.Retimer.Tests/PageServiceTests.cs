using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Sources;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// What the page can ask and do, and who may.
/// </summary>
public sealed class PageServiceTests : IDisposable
{
    private readonly World _w = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    private EpisodeView Episode(Guid showId, Guid itemId, Caller? caller = null) =>
        _w.Page.Show(caller ?? _w.Admin, showId)!.Seasons.SelectMany(s => s.Episodes).Single(e => e.Id == itemId);

    [Fact]
    public void Administrators_and_subtitle_managers_may_fetch_and_viewers_only_when_the_settings_say_everyone()
    {
        Assert.True(_w.Page.MayFetch(_w.Admin));
        Assert.True(_w.Page.MayFetch(_w.Manager));
        Assert.False(_w.Page.MayFetch(_w.Viewer));

        _w.Settings.Current.FetchAccess = "everyone";

        Assert.True(_w.Page.MayFetch(_w.Viewer));
    }

    [Fact]
    public void The_status_says_what_is_set_up_and_holds_no_key()
    {
        _w.FateZero();
        _w.RetimerOn();

        var status = _w.Page.Status(_w.Viewer);
        var json = System.Text.Json.JsonSerializer.Serialize(status);

        Assert.Equal("viewer", status.UserName);
        Assert.False(status.IsAdministrator);
        Assert.False(status.MayFetch);
        Assert.Equal("ja", status.Language);
        Assert.True(status.RetimerReady);
        Assert.Equal("Test Model", status.ModelName);
        Assert.Contains(status.Languages, l => l.Code == "en");
        Assert.DoesNotContain("rt-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("or-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("jimaku-key", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Retimer_API_is_off_on_a_fresh_install()
    {
        var status = _w.Page.Status(_w.Admin);

        Assert.False(status.RetimerEnabled);
        Assert.False(status.RetimerReady);
    }

    [Fact]
    public void A_user_sees_only_what_Jellyfin_lets_them_see()
    {
        var videos = _w.FateZero();
        _w.Library.Blind.Add(_w.Viewer.UserId);

        Assert.Empty(_w.Page.Libraries(_w.Viewer));
        Assert.Null(_w.Page.Shows(_w.Viewer, FakeLibrary.LibraryId));
        Assert.Null(_w.Page.Show(_w.Viewer, videos[0].ShowId));
        Assert.NotNull(_w.Page.Show(_w.Admin, videos[0].ShowId));
    }

    [Fact]
    public async Task An_episode_goes_from_no_subtitle_to_saved_and_can_then_be_retimed()
    {
        var videos = _w.FateZero();
        var show = videos[0].ShowId;

        var before = Episode(show, videos[0].Id);
        Assert.Equal("none", before.State);
        Assert.Equal("No Japanese subtitle", before.Text);

        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        var raw = Episode(show, videos[0].Id);
        Assert.Equal("raw", raw.State);
        Assert.Equal("Japanese subtitle, not retimed", raw.Text);
        Assert.True(raw.FromPlugin);
        Assert.False(raw.CanRetime, "Retimer API is off");
        Assert.Equal("[Group] Fate Zero - 01 [BD].ja.srt", raw.FileName);

        _w.RetimerOn();
        Assert.True(Episode(show, videos[0].Id).CanRetime);

        _w.Library.Tracks = [Subtitles.Track("en")];
        await _w.RunAsync(_w.JobFor(videos[0], JobKind.Retime), Token);
        var retimed = Episode(show, videos[0].Id);
        Assert.Equal("retimed", retimed.State);
        Assert.Equal("Japanese subtitle, retimed", retimed.Text);
        Assert.False(retimed.CanRetime);
    }

    [Fact]
    public void A_subtitle_that_is_already_in_the_video_is_shown_as_present_and_not_as_the_plugins()
    {
        var videos = _w.FateZero();
        _w.Library.Videos[videos[0].Id] = videos[0] with { Subtitles = [new TrackInfo { Index = 2, Language = "jpn", Codec = "ass", IsText = true }] };

        var episode = Episode(videos[0].ShowId, videos[0].Id);

        Assert.Equal("present", episode.State);
        Assert.Equal("In the video file", episode.Detail);
        Assert.False(episode.FromPlugin);
    }

    [Fact]
    public async Task Failed_and_no_fit_are_remembered_for_the_episode()
    {
        var videos = _w.FateZero();
        var extra = _w.Library.AddEpisode(videos[0].ShowId, "Fate/Zero", 1, 9, "Episode 9");
        await _w.RunAsync(_w.JobFor(extra), Token);
        _w.Store.UpdateItem(videos[1].Id, "ja", item => item.LastError = "Jimaku did not answer.");

        var nofit = Episode(extra.ShowId, extra.Id);
        var failed = Episode(extra.ShowId, videos[1].Id);

        Assert.Equal("nofit", nofit.State);
        Assert.Equal("No file fits this episode", nofit.Text);
        Assert.Equal("failed", failed.State);
        Assert.Equal("Jimaku did not answer.", failed.Detail);
    }

    [Fact]
    public async Task A_show_can_have_its_own_language_and_the_servers_language_is_no_override()
    {
        var videos = _w.FateZero();
        var show = videos[0].ShowId;

        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.SetLanguage(_w.Viewer, show, "en")!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.SetLanguage(_w.Admin, show, "klingon")!.Status);
        Assert.Equal(StatusCodes.Status404NotFound, _w.Page.SetLanguage(_w.Admin, Guid.NewGuid(), "en")!.Status);

        Assert.Null(_w.Page.SetLanguage(_w.Manager, show, "eng"));
        Assert.Equal("en", _w.Page.LanguageOf(show));
        var view = _w.Page.Show(_w.Admin, show)!;
        Assert.True(view.OwnLanguage);
        Assert.Equal("English", view.LanguageName);
        Assert.Equal("No English subtitle", view.Seasons[0].Episodes[0].Text);
        Assert.True(_w.Page.Shows(_w.Admin, FakeLibrary.LibraryId)!.Single().OwnLanguage);
        Assert.True(_w.Library.Invalidations > 0);

        // A job for the show now asks for English.
        var (started, _) = _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id] });
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Equal(1, started!.Started);
        Assert.Equal("en", _w.Queue.Since(0).Single().Job.Language);

        Assert.Null(_w.Page.SetLanguage(_w.Admin, show, "ja"));
        Assert.False(_w.Page.Show(_w.Admin, show)!.OwnLanguage);
        Assert.Null(_w.Store.Show(show)!.Language);
    }

    [Fact]
    public void Starting_jobs_is_refused_for_viewers_and_for_requests_that_make_no_sense()
    {
        var videos = _w.FateZero();

        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.Start(_w.Viewer, new StartJobsRequest { ItemIds = [videos[0].Id] }).Error!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [] }).Error!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id], Kind = "delete-everything" }).Error!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id], Kind = "chosen" }).Error!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id, videos[1].Id], Kind = "chosen", Source = "jimaku", FileId = "x" }).Error!.Status);
        Assert.Equal(StatusCodes.Status409Conflict, _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id], Kind = "retime" }).Error!.Status);
        Assert.Equal(
            StatusCodes.Status400BadRequest,
            _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = Enumerable.Range(0, PageService.MaxItemsPerRequest + 1).Select(_ => Guid.NewGuid()).ToArray() }).Error!.Status);
        Assert.Empty(_w.Queue.Since(0));
    }

    [Fact]
    public async Task Get_all_missing_starts_one_job_for_each_video_and_says_what_it_left_out()
    {
        var videos = _w.FateZero(3);
        var notHere = _w.Library.AddEpisode(videos[0].ShowId, "Fate/Zero", 1, 4, "Episode 4");
        _w.Library.Videos[notHere.Id] = notHere with { IsLocalFile = false };

        var (view, error) = _w.Page.Start(_w.Manager, new StartJobsRequest { ItemIds = [videos[0].Id, videos[1].Id, videos[1].Id, Guid.NewGuid(), notHere.Id] });
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Null(error);
        Assert.Equal(2, view!.Started);
        Assert.Equal(2, view.Skipped);
        Assert.Contains("not in a library you can see", view.SkippedBecause, StringComparison.Ordinal);
        Assert.Null(view.Refused);
        Assert.Equal(2, _w.Library.Saved.Count);
        Assert.All(_w.Queue.Since(0), entry => Assert.Equal("manager", entry.Job.StartedBy));
    }

    [Fact]
    public void A_language_no_source_has_starts_nothing_and_says_so()
    {
        var videos = _w.FateZero();
        _w.Source.OnlyLanguage = "ja";
        _w.Page.SetLanguage(_w.Admin, videos[0].ShowId, "en");

        var (view, _) = _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id] });

        Assert.Equal(0, view!.Started);
        Assert.Equal("No subtitle source is set up for English.", view.SkippedBecause);
        Assert.Empty(_w.Page.Show(_w.Admin, videos[0].ShowId)!.Sources);
    }

    [Fact]
    public async Task The_limit_for_a_day_is_passed_on_in_the_answer()
    {
        var videos = _w.FateZero(3);
        _w.Settings.Current.MaxJobsPerDay = 1;

        var (view, _) = _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = videos.Select(v => v.Id).ToArray() });
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(1, view!.Started);
        Assert.Contains("limit of 1 job ", view.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_are_listed_for_those_who_may_see_the_show_and_cancelled_only_by_their_starter_or_an_administrator()
    {
        var videos = _w.FateZero(2);
        _w.Settings.Current.FetchAccess = "everyone";
        var gate = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages => { gate.Task.Wait(Token); return map(messages); };
        _w.Page.Start(_w.Manager, new StartJobsRequest { ItemIds = [videos[0].Id, videos[1].Id] });
        var waiting = _w.Queue.Since(0).Single(e => e.Position == 1).Job;

        var forViewer = _w.Page.Jobs(_w.Viewer, 0);
        Assert.Equal(2, forViewer.Jobs.Count);
        Assert.All(forViewer.Jobs, job => Assert.False(job.MayCancel));
        Assert.True(_w.Page.Jobs(_w.Manager, 0).Jobs.All(job => job.MayCancel));
        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.Cancel(_w.Viewer, waiting.Id)!.Status);

        _w.Library.Blind.Add(_w.Viewer.UserId);
        var stranger = _w.Viewer with { UserId = Guid.NewGuid() };
        _w.Library.Blind.Add(stranger.UserId);
        Assert.Empty(_w.Page.Jobs(stranger, 0).Jobs);
        Assert.Equal(StatusCodes.Status404NotFound, _w.Page.Cancel(stranger, waiting.Id)!.Status);

        Assert.Null(_w.Page.Cancel(_w.Admin, waiting.Id));
        gate.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(StatusCodes.Status409Conflict, _w.Page.Cancel(_w.Admin, waiting.Id)!.Status);
        Assert.Equal(StatusCodes.Status404NotFound, _w.Page.Cancel(_w.Admin, "nope")!.Status);
        Assert.Equal(JobState.Cancelled, _w.Queue.Find(waiting.Id)!.State);
    }

    [Fact]
    public async Task While_a_job_runs_the_episode_shows_its_step_and_then_its_result()
    {
        var videos = _w.FateZero(2);
        var gate = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages => { reached.TrySetResult(); gate.Task.Wait(Token); return map(messages); };

        _w.Page.Start(_w.Admin, new StartJobsRequest { ItemIds = [videos[0].Id, videos[1].Id] });
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);

        var working = Episode(videos[0].ShowId, videos[0].Id);
        var queued = Episode(videos[0].ShowId, videos[1].Id);
        Assert.Equal("working", working.State);
        Assert.Equal(JobRunner.StepSearch, working.Text);
        Assert.True(working.Steps >= 4);
        Assert.True(working.MayCancel);
        Assert.Equal("queued", queued.State);
        Assert.Equal("Starts when the one before it is done", queued.Detail);

        gate.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Equal("raw", Episode(videos[0].ShowId, videos[0].Id).State);
        Assert.Equal("raw", Episode(videos[0].ShowId, videos[1].Id).State);
    }

    [Fact]
    public async Task A_finished_job_can_be_started_again_and_only_an_administrator_clears_the_list()
    {
        var videos = _w.FateZero(1);
        _w.Source.DownloadFailures["[Group] Fate Zero - 01 [BD].ja.srt"] = new SourceException(SourceError.Unavailable, "Jimaku did not answer.");
        _w.Page.Start(_w.Manager, new StartJobsRequest { ItemIds = [videos[0].Id] });
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        var failed = _w.Queue.Since(0).Single().Job;
        Assert.Equal(JobState.Failed, failed.State);

        _w.Source.DownloadFailures.Clear();
        var (view, error) = _w.Page.Retry(_w.Manager, failed.Id);
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Null(error);
        Assert.Equal(1, view!.Started);
        Assert.Single(_w.Library.Saved);
        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.ClearFinished(_w.Manager)!.Status);
        Assert.Null(_w.Page.ClearFinished(_w.Admin));
        Assert.Empty(_w.Queue.Since(0));
    }

    [Fact]
    public async Task The_picker_lists_the_episodes_files_first_then_every_other_file_of_the_entry()
    {
        var videos = _w.FateZero(3);
        _w.Source.AddFile("Fate Zero extras.rar", "384");
        await _w.RunAsync(_w.JobFor(videos[1]), Token);
        _w.Store.UpdateItem(videos[1].Id, "ja", item => item.Tried.Add(new TriedFile { Source = "jimaku", FileId = "[Group] Fate Zero - 03 [BD].ja.srt", Name = "x", Reason = "it belongs to other content" }));

        var (view, error) = await _w.Page.FilesAsync(_w.Manager, videos[1].Id, Token);

        Assert.Null(error);
        Assert.Equal("Fate/Zero", view!.ShowName);
        Assert.Equal("Season 1 · 2. Episode 2", view.ItemLabel);
        Assert.Equal(4, view.Files.Count);
        var first = view.Files[0];
        Assert.Equal("[Group] Fate Zero - 02 [BD].ja.srt", first.Name);
        Assert.True(first.Fits);
        Assert.True(first.Current);
        Assert.Equal("Test Model", first.SuggestedBy);
        Assert.Equal("384", first.EntryId);
        Assert.All(view.Files.Skip(1), file => Assert.False(file.Fits));
        Assert.Equal("it belongs to other content", view.Files.Single(f => f.Name.Contains("- 03", StringComparison.Ordinal)).Tried);
        Assert.NotNull(view.Files.Single(f => f.Name.EndsWith(".rar", StringComparison.Ordinal)).Unusable);
        var source = Assert.Single(view.Sources);
        Assert.Equal("Fate/Zero", source.EntryName);
        Assert.Equal(4, source.Files);
        Assert.True(view.CanSearchEntries);

        Assert.Equal(StatusCodes.Status403Forbidden, (await _w.Page.FilesAsync(_w.Viewer, videos[1].Id, Token)).Error!.Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await _w.Page.FilesAsync(_w.Admin, Guid.NewGuid(), Token)).Error!.Status);
    }

    [Fact]
    public async Task Wrong_show_lets_the_user_choose_another_entry_from_a_search_and_nothing_else()
    {
        var videos = _w.FateZero(2);
        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        _w.Store.UpdateItem(videos[1].Id, "ja", item => item.NoFit = true);
        _w.Source.AddEntry("5724", "Fate/Zero 2nd Season", 11741);
        _w.Source.AddFile("[Group] Fate Zero S2 - 02 [BD].ja.srt", "5724", "1\n00:00:01,000 --> 00:00:02,000\n二期のファイルです。こんにちは、皆さん。今日もよろしくお願いします。それでは始めましょう。\n");

        Assert.Equal(StatusCodes.Status404NotFound, _w.Page.PinEntry(_w.Admin, videos[1].Id, new PinEntryRequest { Source = "jimaku", EntryId = "5724" })!.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await _w.Page.EntriesAsync(_w.Admin, videos[1].Id, "  ", Token)).Error!.Status);

        var (entries, error) = await _w.Page.EntriesAsync(_w.Manager, videos[1].Id, "Fate Zero", Token);
        Assert.Null(error);
        Assert.Equal(2, entries!.Count);
        Assert.True(entries.Single(e => e.Id == "384").Current);
        Assert.False(entries.Single(e => e.Id == "5724").Current);

        Assert.Equal(StatusCodes.Status403Forbidden, _w.Page.PinEntry(_w.Viewer, videos[1].Id, new PinEntryRequest { Source = "jimaku", EntryId = "5724" })!.Status);
        Assert.Null(_w.Page.PinEntry(_w.Manager, videos[1].Id, new PinEntryRequest { Source = "jimaku", EntryId = "5724" }));

        Assert.False(_w.Store.Item(videos[1].Id, "ja")!.NoFit);
        var job = await _w.RunAsync(_w.JobFor(videos[1]), Token);
        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Equal("[Group] Fate Zero S2 - 02 [BD].ja.srt", _w.Store.Item(videos[1].Id, "ja")!.FileName);
        Assert.Equal("5724", _w.Source.Listings.Last());
    }

    [Fact]
    public async Task Only_a_subtitle_the_plugin_saved_can_be_removed_here()
    {
        var videos = _w.FateZero(2);
        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        _w.Library.Videos[videos[1].Id] = videos[1] with { Subtitles = [new TrackInfo { Index = 2, Language = "jpn", IsExternal = true, Path = "/media/own.ja.srt" }] };

        Assert.Equal(StatusCodes.Status403Forbidden, (await _w.Page.RemoveSubtitleAsync(_w.Viewer, videos[0].Id, Token))!.Status);
        Assert.Equal(StatusCodes.Status409Conflict, (await _w.Page.RemoveSubtitleAsync(_w.Admin, videos[1].Id, Token))!.Status);
        Assert.Empty(_w.Library.Removed);

        Assert.Null(await _w.Page.RemoveSubtitleAsync(_w.Manager, videos[0].Id, Token));

        Assert.Single(_w.Library.Removed);
        Assert.Equal("none", Episode(videos[0].ShowId, videos[0].Id).State);
        Assert.Equal(string.Empty, _w.Store.Item(videos[0].Id, "ja")!.SavedPath);
        Assert.Equal("present", Episode(videos[0].ShowId, videos[1].Id).State);
    }

    [Fact]
    public void The_line_for_a_video_reads_like_the_show_page()
    {
        Assert.Equal("Season 1 · 5. Howl of the Mad Beast", PageService.Label(new VideoInfo { Id = Guid.NewGuid(), ShowId = Guid.NewGuid(), ShowName = "Fate/Zero", Season = 1, Episode = 5, Name = "Howl of the Mad Beast" }));
        Assert.Equal("Specials · 2. Picture Drama", PageService.Label(new VideoInfo { Id = Guid.NewGuid(), ShowId = Guid.NewGuid(), ShowName = "x", Season = 0, Episode = 2, Name = "Picture Drama" }));
        Assert.Equal("Film · 2016", PageService.Label(new VideoInfo { Id = Guid.NewGuid(), ShowId = Guid.NewGuid(), ShowName = "Your Name", IsMovie = true, Year = 2016, Name = "Your Name" }));
    }
}
