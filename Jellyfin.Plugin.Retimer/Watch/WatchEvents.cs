using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Retimer.Watch;

/// <summary>
/// Hears when Jellyfin adds an episode or a show to the library, and has watch mode check that show soon,
/// so a new episode does not wait for the next scheduled check.
/// </summary>
internal sealed class WatchEvents : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly WatchService _watch;

    public WatchEvents(ILibraryManager libraryManager, WatchService watch)
    {
        _libraryManager = libraryManager;
        _watch = watch;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Called in the middle of a library scan: only a note is taken here, the check runs later on its own.
        // A new episode gets its series id only when Jellyfin refreshes its metadata, after this event:
        // until then the series is the folder above it.
        switch (e.Item)
        {
            case Episode episode:
                var seriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.FindSeriesId();
                if (seriesId != Guid.Empty)
                {
                    _watch.Soon(seriesId);
                }

                break;
            case Series series:
                _watch.Soon(series.Id);
                break;
        }
    }
}
