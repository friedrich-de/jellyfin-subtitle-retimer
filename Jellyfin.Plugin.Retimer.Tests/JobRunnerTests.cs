using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Sources;
using Jellyfin.Plugin.Retimer.Tests.Support;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// One job from start to end, with a library, a source, a model and a Retimer API that are stand-ins.
/// </summary>
public sealed class JobRunnerTests : IDisposable
{
    private readonly World _w = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Without_Retimer_API_the_file_for_the_episode_is_saved_as_downloaded()
    {
        var videos = _w.FateZero();

        var job = await _w.RunAsync(_w.JobFor(videos[1]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        var saved = Assert.Single(_w.Library.Saved);
        Assert.Equal((videos[1].Id, "ja", "srt"), (saved.Item, saved.Language, saved.Format));
        Assert.Equal(Subtitles.Japanese, Encoding.UTF8.GetString(saved.Content));
        Assert.Empty(_w.Retimer.Submissions);

        var record = _w.Store.Item(videos[1].Id, "ja")!;
        Assert.Equal("jimaku", record.Source);
        Assert.Equal("[Group] Fate Zero - 02 [BD].ja.srt", record.FileName);
        Assert.Equal("Test Model", record.PickedBy);
        Assert.False(record.Retimed);
        Assert.NotNull(record.KeptDownload);
        Assert.True(job.CostUsd > 0, "the model's calls are counted");
        Assert.Equal(["[Group] Fate Zero - 02 [BD].ja.srt"], _w.Source.Downloads);
    }

    [Fact]
    public async Task With_Retimer_API_every_text_track_of_the_video_is_sent_and_the_result_is_saved()
    {
        var videos = _w.FateZero();
        _w.RetimerOn();
        _w.Library.Tracks = [Subtitles.Track("en", "Full Subtitles", isDefault: true), Subtitles.Track("en", "Signs & Songs", forced: true)];
        _w.Retimer.Outcomes.Enqueue(_ => FakeRetimerApi.Synced(chosen: 0));

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.Saved, job.State);
        var sent = Assert.Single(_w.Retimer.Submissions);
        Assert.Equal(2, sent.References.Count);
        Assert.Null(sent.AudioPath);
        var labels = Assert.IsType<JsonArray>(sent.Options["reference_tracks"]);
        Assert.Equal(2, labels.Count);
        Assert.Equal("en", labels[0]!["language"]!.ToString());
        Assert.Equal("Full Subtitles", labels[0]!["title"]!.ToString());
        Assert.True(labels[0]!["default"]!.GetValue<bool>());
        Assert.True(labels[1]!["forced"]!.GetValue<bool>());

        Assert.Equal(_w.Retimer.Result, Assert.Single(_w.Library.Saved).Content);
        Assert.Equal("Retimed against the English track “Full Subtitles” of the video. Moved 0.9 s later.", job.Detail);
        Assert.True(_w.Store.Item(videos[0].Id, "ja")!.Retimed);
        Assert.Single(_w.Retimer.Deleted);
        Assert.True(job.CostUsd >= 0.02, "the cost Retimer API reports is counted");
    }

    [Fact]
    public async Task The_clean_up_options_follow_the_settings()
    {
        var videos = _w.FateZero();
        _w.RetimerOn();
        _w.Settings.Current.RemoveHearingImpaired = true;
        _w.Settings.Current.RemoveSignsAndCredits = true;
        _w.Settings.Current.RemoveStyling = true;
        _w.Settings.Current.OutputFormat = "ass";
        _w.Library.Tracks = [Subtitles.Track("en")];

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        var options = Assert.Single(_w.Retimer.Submissions).Options;
        Assert.True(options["remove_hearing_impaired"]!.GetValue<bool>());
        Assert.True(options["remove_signs_and_credits"]!.GetValue<bool>());
        Assert.True(options["strip_formatting"]!.GetValue<bool>());
        Assert.False(options["remove_karaoke"]!.GetValue<bool>());
        Assert.False(options["remove_symbols"]!.GetValue<bool>());
        Assert.Equal("ass", options["output_format"]!.ToString());
        Assert.Equal("ass", Assert.Single(_w.Library.Saved).Format);
        Assert.Equal(JobState.Saved, job.State);
    }

    [Fact]
    public async Task An_older_Retimer_API_gets_one_track_and_no_option_it_does_not_know()
    {
        var videos = _w.FateZero();
        _w.RetimerOn();
        _w.Settings.Current.RemoveSignsAndCredits = true;
        _w.Retimer.Features = new RetimerFeatures { Ocr = true };
        _w.Library.Tracks = [Subtitles.Track("en", "Signs", forced: true), Subtitles.Track("en", "Full", isDefault: true)];

        await _w.RunAsync(_w.JobFor(videos[0]), Token);

        var sent = Assert.Single(_w.Retimer.Submissions);
        Assert.Equal("Full", Assert.Single(sent.References).Title);
        Assert.Null(sent.Options["reference_tracks"]);
        Assert.Null(sent.Options["remove_signs_and_credits"]);
    }

    [Fact]
    public async Task A_video_without_a_text_track_is_saved_without_retiming_when_audio_is_never_sent()
    {
        var videos = _w.FateZero();
        _w.RetimerOn(PluginConfiguration.AudioNever);

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Contains("no text subtitle track", job.Detail, StringComparison.Ordinal);
        Assert.Empty(_w.Retimer.Submissions);
        Assert.Equal(0, _w.Library.AudioExtractions);
    }

    [Fact]
    public async Task The_audio_is_sent_when_the_video_has_no_text_track_and_the_setting_allows_it()
    {
        var videos = _w.FateZero();
        _w.RetimerOn(PluginConfiguration.AudioFallback);

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.Saved, job.State);
        var sent = Assert.Single(_w.Retimer.Submissions);
        Assert.Empty(sent.References);
        Assert.NotNull(sent.AudioPath);
        Assert.Equal("ja", sent.Options["audio_language"]!.ToString());
        Assert.Contains("audio", job.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_no_track_fits_the_audio_is_tried_in_a_second_job()
    {
        var videos = _w.FateZero();
        _w.RetimerOn(PluginConfiguration.AudioFallback);
        _w.Library.Tracks = [Subtitles.Track("en", "Signs")];
        _w.Retimer.Outcomes.Enqueue(_ => FakeRetimerApi.NoMatch("too_little_dialogue"));
        _w.Retimer.Outcomes.Enqueue(_ => FakeRetimerApi.Synced(chosen: null));

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.Saved, job.State);
        Assert.Equal(2, _w.Retimer.Submissions.Count);
        Assert.Single(_w.Retimer.Submissions[0].References);
        Assert.Null(_w.Retimer.Submissions[0].AudioPath);
        Assert.Empty(_w.Retimer.Submissions[1].References);
        Assert.NotNull(_w.Retimer.Submissions[1].AudioPath);
        Assert.Contains("audio", job.Detail, StringComparison.Ordinal);
        Assert.Empty(job.Tried);
    }

    [Fact]
    public async Task When_no_track_has_enough_dialogue_and_audio_is_never_sent_the_file_is_saved_as_downloaded()
    {
        var videos = _w.FateZero();
        _w.RetimerOn(PluginConfiguration.AudioNever);
        _w.Library.Tracks = [Subtitles.Track("en", "Signs")];
        _w.Retimer.Outcomes.Enqueue(_ => FakeRetimerApi.NoMatch("too_little_dialogue"));

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Contains("too little dialogue", job.Detail, StringComparison.Ordinal);
        Assert.Single(_w.Library.Saved);
        Assert.Empty(job.Tried);
    }

    [Fact]
    public async Task With_audio_always_the_tracks_and_the_audio_go_in_one_job()
    {
        var videos = _w.FateZero();
        _w.RetimerOn(PluginConfiguration.AudioAlways);
        _w.Library.Tracks = [Subtitles.Track("en")];

        await _w.RunAsync(_w.JobFor(videos[0]), Token);

        var sent = Assert.Single(_w.Retimer.Submissions);
        Assert.Single(sent.References);
        Assert.NotNull(sent.AudioPath);
    }

    [Fact]
    public async Task A_file_that_belongs_to_other_content_is_rejected_and_the_next_one_is_tried()
    {
        var videos = _w.FateZero();
        _w.Source.AddFile("[Other] Fate Zero - 01 [TV].ja.srt", "384");
        _w.RetimerOn();
        _w.Library.Tracks = [Subtitles.Track("en")];
        _w.Retimer.Outcomes.Enqueue(_ => FakeRetimerApi.NoMatch("no_match"));

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.Saved, job.State);
        var tried = Assert.Single(job.Tried);
        Assert.Equal("[Group] Fate Zero - 01 [BD].ja.srt", tried.Name);
        Assert.Equal("it belongs to other content", tried.Reason);
        var record = _w.Store.Item(videos[0].Id, "ja")!;
        Assert.Equal("[Other] Fate Zero - 01 [TV].ja.srt", record.FileName);
        Assert.Single(record.Tried);
    }

    [Fact]
    public async Task When_every_file_is_rejected_the_job_ends_with_no_file_fits()
    {
        var videos = _w.FateZero();
        _w.RetimerOn();
        _w.Library.Tracks = [Subtitles.Track("en")];
        _w.Retimer.Outcomes.Enqueue(_ => FakeRetimerApi.NoMatch("no_match"));

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.NoFit, job.State);
        Assert.Contains("1 file tried", job.Detail, StringComparison.Ordinal);
        Assert.Empty(_w.Library.Saved);
        Assert.True(_w.Store.Item(videos[0].Id, "ja")!.NoFit);
    }

    [Fact]
    public async Task A_file_whose_letters_are_another_language_is_rejected_before_anything_is_sent()
    {
        var videos = _w.FateZero();
        _w.Source.Content["[Group] Fate Zero - 01 [BD].ja.srt"] = new DownloadedFile("[Group] Fate Zero - 01 [BD].ja.srt", Encoding.UTF8.GetBytes(Subtitles.English));
        _w.RetimerOn();
        _w.Library.Tracks = [Subtitles.Track("en")];

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.NoFit, job.State);
        Assert.Contains("not Japanese", Assert.Single(job.Tried).Reason, StringComparison.Ordinal);
        Assert.Empty(_w.Retimer.Submissions);
        Assert.Equal(0, _w.Model.CallsOf("language_verdict"));
    }

    [Fact]
    public async Task The_model_rejects_a_file_with_a_second_language()
    {
        var videos = _w.FateZero();
        _w.Model.Language("ja", "zh");

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.NoFit, job.State);
        Assert.Contains("Japanese with Chinese", Assert.Single(job.Tried).Reason, StringComparison.Ordinal);
        Assert.Equal(1, _w.Model.CallsOf("language_verdict"));
    }

    [Fact]
    public async Task A_file_the_user_chose_is_saved_even_when_its_language_looks_wrong()
    {
        var videos = _w.FateZero();
        _w.Source.Content["[Group] Fate Zero - 03 [BD].ja.srt"] = new DownloadedFile("x.srt", Encoding.UTF8.GetBytes(Subtitles.English));
        var job = _w.JobFor(videos[0], JobKind.Chosen);
        job.ChosenSource = "jimaku";
        job.ChosenEntryId = "384";
        job.ChosenFileId = "[Group] Fate Zero - 03 [BD].ja.srt";
        job.Retime = false;

        await _w.RunAsync(job, Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Contains("Mind:", job.Detail, StringComparison.Ordinal);
        Assert.Equal("you", _w.Store.Item(videos[0].Id, "ja")!.PickedBy);
        Assert.Equal(0, _w.Model.CallsOf("map_files"));
    }

    [Fact]
    public async Task A_chosen_file_that_the_source_no_longer_lists_fails_the_job()
    {
        var videos = _w.FateZero();
        var job = _w.JobFor(videos[0], JobKind.Chosen);
        job.ChosenSource = "jimaku";
        job.ChosenEntryId = "384";
        job.ChosenFileId = "https://evil.example.org/x.srt";

        await _w.RunAsync(job, Token);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no longer lists", job.Detail, StringComparison.Ordinal);
        Assert.Empty(_w.Source.Downloads);
    }

    [Fact]
    public async Task A_picture_subtitle_is_converted_when_that_is_switched_on()
    {
        var videos = _w.FateZero(1);
        _w.Source.Files.Clear();
        _w.Source.AddFile("[Raws] Fate Zero - 01 [BDRip].sup", "384", bytes: [0x50, 0x47, 1, 2, 3]);
        _w.RetimerOn();
        _w.Settings.Current.ConvertPictureSubtitles = true;
        _w.Library.Tracks = [Subtitles.Track("en")];

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.Saved, job.State);
        var ocr = Assert.Single(_w.Retimer.OcrRequests);
        Assert.Equal("ja", ocr.Language);
        Assert.EndsWith(".srt", Assert.Single(_w.Retimer.Submissions).TargetName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_subtitle_is_left_alone_when_converting_is_off()
    {
        var videos = _w.FateZero(1);
        _w.Source.Files.Clear();
        _w.Source.AddFile("[Raws] Fate Zero - 01 [BDRip].sup", "384", bytes: [0x50, 0x47, 1, 2, 3]);
        _w.RetimerOn();

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.NoFit, job.State);
        Assert.Empty(_w.Retimer.OcrRequests);
        Assert.Empty(_w.Library.Saved);
    }

    [Fact]
    public async Task The_episodes_file_is_taken_out_of_a_zip_pack()
    {
        var videos = _w.FateZero(2);
        _w.Model.Current = null;
        _w.Source.Files.Clear();
        _w.Source.AddFile("Fate Zero 01-13 [BD].zip", "384", bytes: Zip(("Fate Zero - 01.srt", Subtitles.English), ("Fate Zero - 02.srt", Subtitles.Japanese)));

        var job = await _w.RunAsync(_w.JobFor(videos[1]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Equal(Subtitles.Japanese, Encoding.UTF8.GetString(Assert.Single(_w.Library.Saved).Content));
        Assert.Equal("the rules", _w.Store.Item(videos[1].Id, "ja")!.PickedBy);
    }

    [Fact]
    public async Task A_subtitle_saved_as_downloaded_can_be_retimed_later_from_the_kept_file()
    {
        var videos = _w.FateZero();
        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        var first = _w.Store.Item(videos[0].Id, "ja")!;
        _w.RetimerOn();
        _w.Library.Tracks = [Subtitles.Track("en")];

        var job = await _w.RunAsync(_w.JobFor(videos[0], JobKind.Retime), Token);

        Assert.Equal(JobState.Saved, job.State);
        Assert.Equal(Subtitles.Japanese, Encoding.UTF8.GetString(Assert.Single(_w.Retimer.Submissions).Target));
        Assert.Equal(first.SavedPath, Assert.Single(_w.Library.Removed).Path);
        Assert.Equal(2, _w.Library.Saved.Count);
        var record = _w.Store.Item(videos[0].Id, "ja")!;
        Assert.True(record.Retimed);
        Assert.Equal(first.FileName, record.FileName);
        Assert.Single(_w.Source.Downloads);
    }

    [Fact]
    public async Task The_plugins_own_subtitle_is_never_sent_as_a_reference_track()
    {
        var videos = _w.FateZero();
        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        var path = _w.Store.Item(videos[0].Id, "ja")!.SavedPath;
        _w.RetimerOn();
        _w.Library.Tracks = [Subtitles.Track("en")];

        await _w.RunAsync(_w.JobFor(videos[0], JobKind.Retime), Token);

        Assert.Contains(path, _w.Library.SkippedPaths.Last());
    }

    [Fact]
    public async Task Try_another_file_skips_the_file_in_use()
    {
        var videos = _w.FateZero();
        _w.Source.AddFile("[Other] Fate Zero - 01 [TV].ja.srt", "384");
        await _w.RunAsync(_w.JobFor(videos[0]), Token);

        var job = await _w.RunAsync(_w.JobFor(videos[0], JobKind.Another), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Equal("[Other] Fate Zero - 01 [TV].ja.srt", _w.Store.Item(videos[0].Id, "ja")!.FileName);
        Assert.Single(_w.Library.Removed);
    }

    [Fact]
    public async Task An_episode_the_source_has_no_file_for_ends_with_no_file_fits()
    {
        var videos = _w.FateZero();
        var extra = _w.Library.AddEpisode(videos[0].ShowId, "Fate/Zero", 1, 9, "Episode 9");

        var job = await _w.RunAsync(_w.JobFor(extra), Token);

        Assert.Equal(JobState.NoFit, job.State);
        Assert.Contains("none of them for this episode", job.Detail, StringComparison.Ordinal);
        Assert.Empty(_w.Source.Downloads);
    }

    [Fact]
    public async Task A_refusal_by_Retimer_API_fails_the_job_with_its_sentence()
    {
        var videos = _w.FateZero();
        _w.RetimerOn();
        _w.Retimer.Failure = new RetimerException(RetimerError.LimitReached, "Retimer API's limit for this key is reached. Try again in 10 minutes.");

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("limit for this key", job.Detail, StringComparison.Ordinal);
        Assert.Equal(job.Detail, _w.Store.Item(videos[0].Id, "ja")!.LastError);
        Assert.Empty(_w.Library.Saved);
    }

    [Fact]
    public async Task Without_an_answer_from_the_model_the_rules_pick_when_the_settings_say_so()
    {
        var videos = _w.FateZero();
        _w.Model.Failure = new ModelException(ModelError.Unavailable, "OpenRouter did not answer.");

        var job = await _w.RunAsync(_w.JobFor(videos[2]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Equal("the rules", _w.Store.Item(videos[2].Id, "ja")!.PickedBy);
        Assert.Equal("[Group] Fate Zero - 03 [BD].ja.srt", _w.Store.Item(videos[2].Id, "ja")!.FileName);
    }

    [Fact]
    public async Task Without_an_answer_from_the_model_the_job_asks_when_the_settings_say_so()
    {
        var videos = _w.FateZero();
        _w.Settings.Current.WithoutModel = "ask";
        _w.Model.Tools.Remove("map_files");

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.NoFit, job.State);
        Assert.Contains("Choose a file yourself", job.Detail, StringComparison.Ordinal);
        Assert.Empty(_w.Library.Saved);
    }

    [Fact]
    public async Task The_model_is_asked_once_for_a_season_not_once_for_each_episode()
    {
        var videos = _w.FateZero();

        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        await _w.RunAsync(_w.JobFor(videos[1]), Token);
        await _w.RunAsync(_w.JobFor(videos[2]), Token);

        Assert.Equal(3, _w.Library.Saved.Count);
        Assert.Equal(1, _w.Model.CallsOf("map_files"));
        Assert.Equal(3, _w.Model.CallsOf("language_verdict"));
        Assert.Single(_w.Source.Searches);
    }

    [Fact]
    public async Task A_subtitle_Jellyfin_does_not_list_yet_is_said_so()
    {
        var videos = _w.FateZero();
        _w.Library.Listed = false;

        var job = await _w.RunAsync(_w.JobFor(videos[0]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Contains("has not listed the new subtitle yet", job.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_job_stops_and_saves_nothing()
    {
        var videos = _w.FateZero();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _w.RunAsync(_w.JobFor(videos[0]), cancel.Token));

        Assert.Empty(_w.Library.Saved);
    }

    [Fact]
    public async Task A_video_that_left_the_library_fails_the_job()
    {
        var videos = _w.FateZero();
        var job = _w.JobFor(videos[0]);
        _w.Library.Videos.Remove(videos[0].Id);

        await _w.RunAsync(job, Token);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no longer in the library", job.Detail, StringComparison.Ordinal);
    }
}
