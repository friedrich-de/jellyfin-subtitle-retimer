using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Retimer.Watch;

/// <summary>
/// The check of the watched shows as a task of Jellyfin's own: it is listed in the dashboard under
/// Scheduled Tasks, where an administrator sees when it ran last and changes how often it runs.
/// </summary>
public sealed class WatchTask : IScheduledTask
{
    /// <summary>The task's key in Jellyfin.</summary>
    public const string TaskKey = "SubtitleRetimerWatch";

    /// <summary>How often the check runs until an administrator changes it.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(3);

    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTask"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public WatchTask(PluginServices services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public string Name => "Check watched shows for subtitles";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Looks for subtitles for the episodes of the shows and seasons that are watched on the Subtitle Retimer page, and fetches the ones the sources have now.";

    /// <inheritdoc />
    public string Category => Plugin.PluginName;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _services.Watch.CheckAsync(null, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = DefaultInterval.Ticks }
    ];
}
