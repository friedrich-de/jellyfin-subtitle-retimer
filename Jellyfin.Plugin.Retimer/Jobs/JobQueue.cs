using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Jobs;

/// <summary>
/// What today's use of the plugin is, against the limits in the settings. A job counts from the moment it starts to work.
/// </summary>
internal sealed record UsageToday(int Jobs, int MaxJobs, double CostUsd, double MaxCostUsd);

/// <summary>
/// Runs the jobs one after the other, keeps the finished ones for the activity list, and holds the daily limits.
/// </summary>
internal sealed class JobQueue : IDisposable
{
    internal const string Interrupted = "The server restarted before this job was done. Start it again.";

    private readonly Lock _lock = new();
    private readonly List<Job> _queued = [];
    private readonly List<Job> _finished;
    private readonly JobRunner _runner;
    private readonly PluginStore _store;
    private readonly IPluginSettings _settings;
    private readonly ILogger<JobQueue> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Job? _working;
    private Task? _worker;
    private long _version;

    public JobQueue(JobRunner runner, PluginStore store, IPluginSettings settings, ILogger<JobQueue> logger)
    {
        _runner = runner;
        _store = store;
        _settings = settings;
        _logger = logger;

        // The counter starts at the clock or where it stopped, so it also grows over a restart and the page never misses a change.
        var (finished, pending, last) = store.Read(data => (data.Jobs.Select(j => j.Clone()).ToList(), data.Pending.Select(j => j.Clone()).ToList(), data.JobsVersion));
        _version = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), last);
        _finished = finished;
        foreach (var job in pending)
        {
            job.State = JobState.Failed;
            job.Detail = Interrupted;
            job.Finished = DateTimeOffset.UtcNow;
            _finished.Add(job);
        }

        foreach (var job in _finished)
        {
            job.Version = ++_version;
        }

        if (pending.Count > 0)
        {
            Persist();
        }
    }

    /// <summary>Gets a counter that grows with every change to any job.</summary>
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <summary>Gets a task that ends when the queue is empty. For tests.</summary>
    internal Task Idle
    {
        get
        {
            lock (_lock)
            {
                return _worker ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// The jobs that changed after the given counter: the running one, the waiting ones in order, then the finished ones, newest first.
    /// </summary>
    public IReadOnlyList<(Job Job, int Position)> Since(long version)
    {
        lock (_lock)
        {
            var jobs = new List<(Job Job, int Position)>();
            if (_working is not null && _working.Version > version)
            {
                jobs.Add((_working.Clone(), 0));
            }

            for (var i = 0; i < _queued.Count; i++)
            {
                if (_queued[i].Version > version)
                {
                    jobs.Add((_queued[i].Clone(), i + 1));
                }
            }

            for (var i = _finished.Count - 1; i >= 0; i--)
            {
                if (_finished[i].Version > version)
                {
                    jobs.Add((_finished[i].Clone(), 0));
                }
            }

            return jobs;
        }
    }

    /// <summary>The waiting and running jobs by video and language, with each job's place in the line (0 = running).</summary>
    public IReadOnlyDictionary<string, (Job Job, int Position)> Active()
    {
        lock (_lock)
        {
            var active = new Dictionary<string, (Job Job, int Position)>(StringComparer.Ordinal);
            if (_working is not null)
            {
                active[PluginStore.ItemKey(_working.ItemId, _working.Language)] = (_working.Clone(), 0);
            }

            for (var i = 0; i < _queued.Count; i++)
            {
                active[PluginStore.ItemKey(_queued[i].ItemId, _queued[i].Language)] = (_queued[i].Clone(), i + 1);
            }

            return active;
        }
    }

    public Job? Find(string id)
    {
        lock (_lock)
        {
            var job = _working?.Id == id ? _working : _queued.Find(j => j.Id == id) ?? _finished.Find(j => j.Id == id);
            return job?.Clone();
        }
    }

    public UsageToday Usage()
    {
        var configuration = _settings.Current;
        var today = _store.Read(data => data.Usage.GetValueOrDefault(PluginStore.Today()));
        return new UsageToday(today?.Jobs ?? 0, Math.Max(0, configuration.MaxJobsPerDay), today?.CostUsd ?? 0, Math.Max(0, configuration.MaxCostPerDay));
    }

    /// <summary>
    /// Counts what a search outside a job cost, such as the picker's.
    /// </summary>
    public void AddCost(double costUsd)
    {
        if (costUsd <= 0)
        {
            return;
        }

        var today = PluginStore.Today();
        _store.Update(data =>
        {
            if (!data.Usage.TryGetValue(today, out var usage))
            {
                usage = new DayUsage();
                data.Usage[today] = usage;
            }

            usage.CostUsd += costUsd;
        });
    }

    /// <summary>
    /// Why no new job may be added now, or <c>null</c>. The jobs that wait have their place in today's limit already.
    /// </summary>
    public string? Refusal()
    {
        // Read together, so a job that starts in between is counted once: as waiting, or as started.
        UsageToday usage;
        int waiting;
        lock (_lock)
        {
            usage = Usage();
            waiting = _queued.Count;
        }

        var limit = string.Create(CultureInfo.InvariantCulture, $"Today's limit of {usage.MaxJobs} {(usage.MaxJobs == 1 ? "job" : "jobs")}");
        if (usage.MaxJobs > 0 && usage.Jobs >= usage.MaxJobs)
        {
            return limit + " is reached. It starts again at midnight (UTC), or an administrator raises it in the settings.";
        }

        if (usage.MaxJobs > 0 && usage.Jobs + waiting >= usage.MaxJobs)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{limit} is taken: {usage.Jobs} started and {waiting} waiting. It starts again at midnight (UTC), or an administrator raises it in the settings.");
        }

        if (usage.MaxCostUsd > 0 && usage.CostUsd >= usage.MaxCostUsd)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Today's cost limit of ${usage.MaxCostUsd:0.00} is reached (${usage.CostUsd:0.00} used). It starts again at midnight (UTC), or an administrator raises it in the settings.");
        }

        return null;
    }

    /// <summary>
    /// Puts a job at the end of the line. A video that already waits or runs for the same language keeps its job.
    /// </summary>
    /// <param name="job">The new job.</param>
    /// <returns>The job that now stands for the video, or the reason no job was started.</returns>
    public (Job? Job, string? Refused) Enqueue(Job job)
    {
        lock (_lock)
        {
            var existing = _working is not null && Same(_working, job) ? _working : _queued.Find(j => Same(j, job));
            if (existing is not null)
            {
                return (existing.Clone(), null);
            }
        }

        var refusal = Refusal();
        if (refusal is not null)
        {
            return (null, refusal);
        }

        Job clone;
        lock (_lock)
        {
            job.State = JobState.Queued;
            job.Created = DateTimeOffset.UtcNow;
            job.Version = ++_version;
            _queued.Add(job);
            clone = job.Clone();
            _worker ??= Task.Run(WorkAsync);
        }

        Persist();
        return (clone, null);
    }

    /// <summary>
    /// Takes a waiting job out of the line, or stops the running one.
    /// </summary>
    /// <returns><c>false</c> when the job is not waiting or running any more.</returns>
    public bool Cancel(string id)
    {
        lock (_lock)
        {
            if (_working?.Id == id)
            {
                _working.Cancellation?.Cancel();
                return true;
            }

            var job = _queued.Find(j => j.Id == id);
            if (job is null)
            {
                return false;
            }

            _queued.Remove(job);
            job.State = JobState.Cancelled;
            job.Detail = "Cancelled before it started.";
            job.Finished = DateTimeOffset.UtcNow;
            AddFinished(job);

            // The jobs behind it move up, and the page shows their place.
            foreach (var waiting in _queued)
            {
                waiting.Version = ++_version;
            }
        }

        Persist();
        return true;
    }

    /// <summary>Removes the finished jobs from the activity list.</summary>
    public void ClearFinished()
    {
        lock (_lock)
        {
            _finished.Clear();
            _version++;
        }

        Persist();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            Idle.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The worker's own errors are logged where they happen.
        }

        _shutdown.Dispose();
    }

    private static bool Same(Job a, Job b) => a.ItemId == b.ItemId && string.Equals(a.Language, b.Language, StringComparison.Ordinal);

    /// <summary>
    /// Counts a job that starts to work toward today's limit. One that was cancelled while it waited never gets here.
    /// </summary>
    private void CountStarted()
    {
        var today = PluginStore.Today();
        _store.Update(data =>
        {
            if (!data.Usage.TryGetValue(today, out var usage))
            {
                usage = new DayUsage();
                data.Usage[today] = usage;
            }

            usage.Jobs++;
            foreach (var old in data.Usage.Keys.Where(day => string.CompareOrdinal(day, today) < 0).OrderByDescending(day => day, StringComparer.Ordinal).Skip(60).ToList())
            {
                data.Usage.Remove(old);
            }
        });
    }

    private void Touch(Job job)
    {
        lock (_lock)
        {
            job.Version = ++_version;
        }
    }

    private void AddFinished(Job job)
    {
        job.Cancellation = null;
        job.Version = ++_version;
        _finished.Add(job);
        if (_finished.Count > PluginStore.MaxFinishedJobs)
        {
            _finished.RemoveRange(0, _finished.Count - PluginStore.MaxFinishedJobs);
        }
    }

    private void Persist()
    {
        List<Job> finished;
        List<Job> pending;
        long version;
        lock (_lock)
        {
            version = _version;
            finished = _finished.Select(j => j.Clone()).ToList();
            pending = _queued.Select(j => j.Clone()).ToList();
            if (_working is not null)
            {
                pending.Insert(0, _working.Clone());
            }
        }

        // What waits or runs is stored without what it tried so far: that list is still being written.
        foreach (var job in pending)
        {
            job.Tried = [];
        }

        _store.Update(data =>
        {
            data.Jobs = finished;
            data.Pending = pending;
            data.JobsVersion = Math.Max(data.JobsVersion, version);
        });
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Job job;
            CancellationTokenSource cancellation;
            var refusal = CostRefusal();
            lock (_lock)
            {
                if (_queued.Count == 0 || _shutdown.IsCancellationRequested)
                {
                    _worker = null;
                    return;
                }

                job = _queued[0];
                _queued.RemoveAt(0);
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                job.Cancellation = cancellation;
                job.State = JobState.Working;
                job.Started = DateTimeOffset.UtcNow;
                job.Version = ++_version;
                _working = job;
                foreach (var waiting in _queued)
                {
                    waiting.Version = ++_version;
                }

                if (refusal is null)
                {
                    // Counted while the line is held, so the job is never neither waiting nor started.
                    CountStarted();
                }
            }

            Persist();
            var interrupted = false;
            try
            {
                if (refusal is not null)
                {
                    job.State = JobState.Failed;
                    job.Detail = refusal;
                }
                else
                {
                    await _runner.RunAsync(job, () => Touch(job), cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                interrupted = _shutdown.IsCancellationRequested;
                job.State = interrupted ? JobState.Failed : JobState.Cancelled;
                job.Detail = interrupted ? Interrupted : "Cancelled.";
            }
#pragma warning disable CA1031 // One job's bug must not stop the jobs behind it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "The job for {Show} {Item} failed unexpectedly", job.ShowName, job.ItemLabel);
                job.State = JobState.Failed;
                job.Detail = "It failed on the server: " + ex.Message;
            }

            if (JobState.IsActive(job.State))
            {
                // A job always ends in a final state, whatever its runner did.
                job.State = JobState.Failed;
                job.Detail ??= "It ended without a result.";
            }

            job.StepNote = null;
            job.Finished = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                _working = null;
                AddFinished(job);
            }

            cancellation.Dispose();
            AddCost(job.CostUsd);

            Persist();
        }
    }

    private string? CostRefusal()
    {
        var usage = Usage();
        return usage.MaxCostUsd > 0 && usage.CostUsd >= usage.MaxCostUsd
            ? string.Create(CultureInfo.InvariantCulture, $"Not started: today's cost limit of ${usage.MaxCostUsd:0.00} is reached (${usage.CostUsd:0.00} used). It starts again at midnight (UTC), or an administrator raises it in the settings.")
            : null;
    }
}
