using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Tests.Support;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// The calls a job makes to Retimer API, and how the plugin reads a video's tracks for it.
/// </summary>
public class RetimerApiTests
{
    private readonly StubHandler _handler = new();
    private readonly StubSettings _settings = new() { Current = new PluginConfiguration { RetimerUrl = "https://retimer.example.org", RetimerApiKey = "rt_test" } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // The job's calls are on the interface only, so the client is used through it.
#pragma warning disable CA1859
    private IRetimerApi Api() => new RetimerClient(new StubHttpClientFactory(_handler), _settings, NullLogger<RetimerClient>.Instance);
#pragma warning restore CA1859

    [Fact]
    public async Task The_features_say_how_many_tracks_a_job_takes_and_are_asked_for_once()
    {
        _handler.Json("""{"features":["matched_reference","ocr","reference_tracks","remove_signs_and_credits"],"limits":{"reference_subtitles":6,"audio_minutes":180}}""");
        var api = Api();

        var features = await api.GetFeaturesAsync(Token);
        var again = await api.GetFeaturesAsync(Token);

        Assert.True(features.ReferenceTracks);
        Assert.True(features.RemoveSignsAndCredits);
        Assert.True(features.Ocr);
        Assert.Equal(6, features.MaxReferenceSubtitles);
        Assert.Equal(180, features.AudioMinutes);
        Assert.Same(features, again);
        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("https://retimer.example.org/v1/features", sent.Url.AbsoluteUri);
        Assert.Equal("Bearer rt_test", sent.Authorization);
    }

    [Fact]
    public async Task An_older_server_without_the_features_call_takes_one_track()
    {
        _handler.Json("""{"detail":"Not Found"}""", HttpStatusCode.NotFound);

        var features = await Api().GetFeaturesAsync(Token);

        Assert.False(features.ReferenceTracks);
        Assert.False(features.RemoveSignsAndCredits);
        Assert.Equal(1, features.MaxReferenceSubtitles);
    }

    [Fact]
    public async Task A_job_carries_the_subtitle_every_track_and_the_options()
    {
        _handler.Json("""{"id":"job_1","status":"queued"}""", HttpStatusCode.Accepted);
        var submission = new RetimerSubmission
        {
            TargetName = "[Group] 番組 - 03 (BD).ja.srt",
            Target = Encoding.UTF8.GetBytes("target text"),
            References =
            [
                new ReferenceUpload("track2.ass", Encoding.UTF8.GetBytes("first track"), "eng", "Full", true, false),
                new ReferenceUpload("track3.ass", Encoding.UTF8.GetBytes("second track"), "eng", "Signs", false, true)
            ],
            Options = new JsonObject { ["reference_tracks"] = new JsonArray(new JsonObject { ["language"] = "eng" }) }
        };

        var job = await Api().SubmitAsync(submission, Token);

        Assert.Equal("job_1", job.Id);
        Assert.False(job.IsFinished);
        var sent = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://retimer.example.org/v1/jobs", sent.Url.AbsoluteUri);
        var body = _handler.Bodies[0];
        Assert.Equal(1, Count(body, "name=target"));
        Assert.Equal(2, Count(body, "name=reference_subtitle"));
        Assert.Equal(1, Count(body, "name=options"));
        Assert.Equal(0, Count(body, "name=reference_audio"));
        Assert.Contains("first track", body, StringComparison.Ordinal);
        Assert.Contains("second track", body, StringComparison.Ordinal);
        Assert.Contains("\"reference_tracks\"", body, StringComparison.Ordinal);

        // The upload's name keeps its extension and drops what could trouble a header.
        Assert.DoesNotContain("番組", body, StringComparison.Ordinal);
        Assert.Contains(".srt", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_job_says_which_track_was_chosen_and_what_it_made_of_each()
    {
        var job = RetimerClient.ReadJob(JsonNode.Parse("""
            {"id":"job_1","status":"finished","stage":"done","outcome":"synced",
             "summary":{"cost_usd":0.012,"confidence":0.91,"warnings":["one"],"fit":"blocks",
              "blocks":[{"from_ms":null,"to_ms":239724,"shift_ms":0,"lines":17,"confidence":0.79},
                        {"from_ms":239724,"to_ms":null,"shift_ms":-40901,"lines":59,"confidence":0.95}],
              "reference":{"chosen":1,"reason":"fits best","candidates":[
               {"index":0,"language":"eng","title":"Signs","verdict":"too_little_dialogue","reason":"12 lines"},
               {"index":1,"language":"eng","title":"Full","verdict":"chosen","reason":null}]}}}
            """)!);

        Assert.True(job.IsFinished);
        Assert.Equal("synced", job.Outcome);
        Assert.Equal(0.012, job.CostUsd, 6);
        Assert.True(job.HasReferenceReport);
        Assert.Equal(1, job.ChosenReference);
        Assert.Equal(["too_little_dialogue", "chosen"], job.Candidates.Select(c => c.Verdict));
        Assert.Equal("Signs", job.Candidates[0].Title);
        Assert.Equal("blocks", job.Fit);
        Assert.Equal([0, -40901], job.ShiftsMs);
    }

    [Theory]
    [InlineData("constant", new[] { 881 }, " Moved 0.9 s later.")]
    [InlineData("constant", new[] { -12400 }, " Moved 12.4 s earlier.")]
    [InlineData("constant", new[] { 30 }, " Moved by less than 0.1 s.")]
    [InlineData("blocks", new[] { 0, -40901 }, " Moved in 2 parts, by up to 40.9 s.")]
    [InlineData("rate", new[] { 1210 }, " Adjusted for another frame rate.")]
    [InlineData("rate_blocks", new[] { 1210, 14830 }, " Adjusted for another frame rate and moved in 2 parts, by up to 14.8 s.")]
    [InlineData(null, new int[0], "")]
    public void How_far_a_subtitle_was_moved_is_said_in_one_sentence(string? fit, int[] shifts, string expected)
    {
        Assert.Equal(expected, JobRunner.Moved(new RetimerJob { Fit = fit, ShiftsMs = shifts }));
    }

    [Fact]
    public void A_job_where_no_track_fits_has_a_report_and_no_choice()
    {
        var job = RetimerClient.ReadJob(JsonNode.Parse("""
            {"id":"job_2","status":"finished","outcome":"rejected_no_match",
             "summary":{"reference":{"chosen":null,"reason":"No track fits.","candidates":[{"index":0,"verdict":"no_match"}]}}}
            """)!);

        Assert.True(job.HasReferenceReport);
        Assert.Null(job.ChosenReference);
        Assert.Equal("No track fits.", job.ReferenceReason);
    }

    [Fact]
    public void An_older_servers_job_has_no_report()
    {
        var job = RetimerClient.ReadJob(JsonNode.Parse("""{"id":"job_3","status":"running","stage":"matching"}""")!);

        Assert.False(job.HasReferenceReport);
        Assert.Null(job.ChosenReference);
        Assert.Empty(job.Candidates);
        Assert.Equal("matching", job.Stage);
    }

    [Fact]
    public void A_track_is_counted_by_its_place_in_the_file_not_by_Jellyfins_index()
    {
        // Jellyfin numbers a subtitle file next to the video before the streams inside it.
        var external = new MediaStream { Index = 0, Type = MediaStreamType.Subtitle, IsExternal = true };
        var video = new MediaStream { Index = 1, Type = MediaStreamType.Video };
        var audio = new MediaStream { Index = 2, Type = MediaStreamType.Audio };
        var full = new MediaStream { Index = 3, Type = MediaStreamType.Subtitle };
        var signs = new MediaStream { Index = 4, Type = MediaStreamType.Subtitle };
        List<MediaStream> streams = [external, video, audio, full, signs];

        Assert.Equal(1, JellyfinVideoLibrary.IndexInFile(streams, audio));
        Assert.Equal(2, JellyfinVideoLibrary.IndexInFile(streams, full));
        Assert.Equal(3, JellyfinVideoLibrary.IndexInFile(streams, signs));

        // Without such a file both numbers agree.
        List<MediaStream> plain = [new MediaStream { Index = 0 }, new MediaStream { Index = 1 }, new MediaStream { Index = 2 }];
        Assert.Equal(2, JellyfinVideoLibrary.IndexInFile(plain, plain[2]));
    }

    [Theory]
    [InlineData("PGSSUB", true)]
    [InlineData("pgssub", true)]
    [InlineData("hdmv_pgs_subtitle", true)]
    [InlineData("ass", false)]
    [InlineData(null, false)]
    public void A_codec_name_is_matched_in_either_case(string? codec, bool expected)
    {
        Assert.Equal(expected, JellyfinVideoLibrary.IsCodec(codec, "pgssub", "hdmv_pgs_subtitle", "pgs"));
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;
}
