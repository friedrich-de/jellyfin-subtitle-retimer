using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Retimer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Retimer;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPluginSettings, PluginSettings>();
        serviceCollection.AddSingleton<IJimakuClient, JimakuClient>();
        serviceCollection.AddSingleton<IRetimerClient, RetimerClient>();
    }
}
