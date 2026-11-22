using System;
using System.Linq;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Retimer.Watch;

/// <summary>
/// Reads the schedule of <see cref="WatchTask"/> from Jellyfin's task manager, and starts the task there.
/// </summary>
internal sealed class JellyfinWatchSchedule : IWatchSchedule
{
    private readonly ITaskManager _tasks;

    public JellyfinWatchSchedule(ITaskManager tasks)
    {
        _tasks = tasks;
    }

    public TimeSpan? Interval
    {
        get
        {
            var intervals = (Worker()?.Triggers ?? [])
                .Where(trigger => trigger.Type == TaskTriggerInfoType.IntervalTrigger && trigger.IntervalTicks is > 0)
                .Select(trigger => TimeSpan.FromTicks(trigger.IntervalTicks!.Value))
                .ToList();
            return intervals.Count == 0 ? null : intervals.Min();
        }
    }

    public bool Scheduled => (Worker()?.Triggers ?? []).Any();

    public void RunNow() => _tasks.QueueIfNotRunning<WatchTask>();

    private IScheduledTaskWorker? Worker() => _tasks.ScheduledTasks.FirstOrDefault(worker => worker.ScheduledTask is WatchTask);
}
