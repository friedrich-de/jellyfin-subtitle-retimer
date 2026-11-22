using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Watch;

namespace Jellyfin.Plugin.Retimer;

/// <summary>
/// The plugin's own services in one object, so the controllers Jellyfin creates can ask for them.
/// </summary>
public sealed class PluginServices
{
    internal PluginServices(PageService page, SettingsService settings, ICallerResolver callers, WatchService watch)
    {
        Page = page;
        Settings = settings;
        Callers = callers;
        Watch = watch;
    }

    internal PageService Page { get; }

    internal SettingsService Settings { get; }

    internal ICallerResolver Callers { get; }

    internal WatchService Watch { get; }
}
