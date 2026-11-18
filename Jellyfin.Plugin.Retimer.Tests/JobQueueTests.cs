using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests;

public sealed class JobQueueTests : IDisposable
{
    private readonly World _w = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task Jobs_run_one_after_the_other_in_the_order_they_came()
    {
        var videos = _w.FateZero(3);

        foreach (var video in videos)
        {
            Assert.Null(_w.Queue.Enqueue(_w.JobFor(video)).Refused);
        }

        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(videos.Select(v => v.Id), _w.Library.Saved.Select(s => s.Item));
        var jobs = _w.Queue.Since(0);
        Assert.Equal(3, jobs.Count);
        Assert.All(jobs, entry => Assert.Equal(JobState.SavedRaw, entry.Job.State));
        Assert.All(jobs, entry => Assert.NotNull(entry.Job.Finished));
        Assert.Empty(_w.Queue.Active());
    }

    [Fact]
    public async Task A_video_that_already_waits_gets_no_second_job()
    {
        var videos = _w.FateZero(1);
        var gate = new TaskCompletionSource();
        _w.Model.Tools["map_files"] = _ => { gate.Task.Wait(Token); return new System.Text.Json.Nodes.JsonObject(); };

        var first = _w.Queue.Enqueue(_w.JobFor(videos[0])).Job!;
        var second = _w.Queue.Enqueue(_w.JobFor(videos[0])).Job!;
        gate.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_w.Queue.Since(0));
        Assert.Equal(1, _w.Queue.Usage().Jobs);
    }

    [Fact]
    public async Task A_waiting_job_can_be_cancelled_and_never_runs()
    {
        var videos = _w.FateZero(2);
        var gate = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages => { gate.Task.Wait(Token); return map(messages); };

        _w.Queue.Enqueue(_w.JobFor(videos[0]));
        var waiting = _w.Queue.Enqueue(_w.JobFor(videos[1])).Job!;
        Assert.True(_w.Queue.Cancel(waiting.Id));
        gate.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(JobState.Cancelled, _w.Queue.Find(waiting.Id)!.State);
        Assert.Equal([videos[0].Id], _w.Library.Saved.Select(s => s.Item));
        Assert.False(_w.Queue.Cancel(waiting.Id));
    }

    [Fact]
    public async Task The_running_job_can_be_cancelled()
    {
        var videos = _w.FateZero(1);
        var started = new TaskCompletionSource();
        _w.Model.Tools["map_files"] = _ =>
        {
            started.TrySetResult();
            Thread.Sleep(300);
            return new System.Text.Json.Nodes.JsonObject();
        };

        var job = _w.Queue.Enqueue(_w.JobFor(videos[0])).Job!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.True(_w.Queue.Cancel(job.Id));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(JobState.Cancelled, _w.Queue.Find(job.Id)!.State);
        Assert.Empty(_w.Library.Saved);
    }

    [Fact]
    public async Task The_limit_of_subtitles_for_a_day_refuses_further_jobs_with_a_sentence()
    {
        var videos = _w.FateZero(3);
        _w.Settings.Current.MaxJobsPerDay = 2;

        Assert.NotNull(_w.Queue.Enqueue(_w.JobFor(videos[0])).Job);
        Assert.NotNull(_w.Queue.Enqueue(_w.JobFor(videos[1])).Job);
        var (job, refused) = _w.Queue.Enqueue(_w.JobFor(videos[2]));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Null(job);
        Assert.Contains("limit of 2 jobs", refused, StringComparison.Ordinal);
        Assert.Equal(2, _w.Library.Saved.Count);
        Assert.Equal(2, _w.Queue.Usage().Jobs);
        Assert.Contains("limit of 2 jobs is reached", _w.Queue.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_counts_toward_the_day_when_it_starts_and_not_while_it_waits()
    {
        var videos = _w.FateZero(3);
        _w.Settings.Current.MaxJobsPerDay = 3;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages =>
        {
            started.TrySetResult();
            release.Task.Wait(Token);
            return map(messages);
        };

        _w.Queue.Enqueue(_w.JobFor(videos[0]));
        var second = _w.Queue.Enqueue(_w.JobFor(videos[1])).Job!;
        var third = _w.Queue.Enqueue(_w.JobFor(videos[2])).Job!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);

        // One runs and two wait: the day's use is one job, and the line still has no room for a fourth.
        Assert.Equal(1, _w.Queue.Usage().Jobs);
        Assert.Contains("is taken: 1 started and 2 waiting", _w.Queue.Refusal(), StringComparison.Ordinal);

        // A job that is cancelled while it waits gives its place back and is never counted.
        Assert.True(_w.Queue.Cancel(third.Id));
        Assert.Null(_w.Queue.Refusal());
        release.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(2, _w.Queue.Usage().Jobs);
        Assert.Equal(JobState.SavedRaw, _w.Queue.Find(second.Id)!.State);
        Assert.Equal(JobState.Cancelled, _w.Queue.Find(third.Id)!.State);
    }

    [Fact]
    public async Task The_cost_limit_stops_the_jobs_that_still_wait()
    {
        var videos = _w.FateZero(3);
        _w.Settings.Current.MaxCostPerDay = 0.05;
        _w.Model.CostPerCall = 0.03;

        // The first job is held until the others wait behind it. Left to itself it can be through,
        // and the day's limit spent, before they are queued, and then they are refused at the door.
        var queued = new TaskCompletionSource();
        var map = _w.Model.Tools["map_files"];
        _w.Model.Tools["map_files"] = messages =>
        {
            queued.Task.Wait(Token);
            return map(messages);
        };

        foreach (var video in videos)
        {
            Assert.Null(_w.Queue.Enqueue(_w.JobFor(video)).Refused);
        }

        queued.SetResult();
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        var states = _w.Queue.Since(0).OrderBy(e => e.Job.Created).Select(e => e.Job.State).ToList();
        Assert.Equal(JobState.SavedRaw, states[0]);
        Assert.Contains(JobState.Failed, states);
        var failed = _w.Queue.Since(0).First(e => e.Job.State == JobState.Failed).Job;
        Assert.Contains("cost limit of $0.05", failed.Detail, StringComparison.Ordinal);
        Assert.True(_w.Library.Saved.Count < 3);
        Assert.Contains("cost limit", _w.Queue.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_means_no_limit()
    {
        var videos = _w.FateZero(3);
        _w.Settings.Current.MaxJobsPerDay = 0;
        _w.Settings.Current.MaxCostPerDay = 0;
        _w.Model.CostPerCall = 5;

        foreach (var video in videos)
        {
            Assert.Null(_w.Queue.Enqueue(_w.JobFor(video)).Refused);
        }

        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Equal(3, _w.Library.Saved.Count);
        Assert.Null(_w.Queue.Refusal());
    }

    [Fact]
    public async Task Finished_jobs_and_the_days_use_survive_a_restart()
    {
        var videos = _w.FateZero(2);
        _w.Queue.Enqueue(_w.JobFor(videos[0]));
        _w.Queue.Enqueue(_w.JobFor(videos[1]));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        _w.Store.Flush();

        using var store = new PluginStore(_w.Directory, NullLogger.Instance);
        using var queue = new JobQueue(_w.Runner, store, _w.Settings, NullLogger<JobQueue>.Instance);

        var jobs = queue.Since(0);
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, entry => Assert.Equal(JobState.SavedRaw, entry.Job.State));
        Assert.Equal(2, queue.Usage().Jobs);
        Assert.True(queue.Usage().CostUsd > 0);
        Assert.True(queue.Version > _w.Queue.Version, "the counter keeps growing over a restart");
    }

    [Fact]
    public void A_job_that_a_restart_interrupted_is_shown_as_failed_with_the_reason()
    {
        var videos = _w.FateZero(1);
        _w.Store.Update(data => data.Pending.Add(_w.JobFor(videos[0])));
        _w.Store.Flush();

        using var store = new PluginStore(_w.Directory, NullLogger.Instance);
        using var queue = new JobQueue(_w.Runner, store, _w.Settings, NullLogger<JobQueue>.Instance);

        var job = Assert.Single(queue.Since(0)).Job;
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(JobQueue.Interrupted, job.Detail);
        Assert.Empty(queue.Active());
    }

    [Fact]
    public async Task Only_what_changed_since_the_last_look_is_returned()
    {
        var videos = _w.FateZero(2);
        _w.Queue.Enqueue(_w.JobFor(videos[0]));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        var seen = _w.Queue.Version;

        Assert.Empty(_w.Queue.Since(seen));
        _w.Queue.Enqueue(_w.JobFor(videos[1]));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(videos[1].Id, Assert.Single(_w.Queue.Since(seen)).Job.ItemId);
    }
}
