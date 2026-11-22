using System.IO;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Sources;
using Jellyfin.Plugin.Retimer.Watch;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// The folder inside Jellyfin's data folder where the plugin keeps what it remembers.
    /// </summary>
    internal const string DataFolderName = "subtitle-retimer";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPluginSettings, PluginSettings>();
        serviceCollection.AddSingleton<IJimakuClient, JimakuClient>();
        serviceCollection.AddSingleton<RetimerClient>();
        serviceCollection.AddSingleton<IRetimerClient>(provider => provider.GetRequiredService<RetimerClient>());
        serviceCollection.AddSingleton<IRetimerApi>(provider => provider.GetRequiredService<RetimerClient>());

        serviceCollection.AddSingleton<ISubtitleSource, JimakuSource>();
        serviceCollection.AddSingleton<ISubtitleSource, OpenSubtitlesSource>();
        serviceCollection.AddSingleton<SubtitleSources>();
        serviceCollection.AddSingleton<IModelClient, OpenRouterClient>();
        serviceCollection.AddSingleton<ModelPicker>();

        serviceCollection.AddSingleton(provider => new PluginStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, DataFolderName),
            provider.GetRequiredService<ILogger<PluginStore>>()));
        serviceCollection.AddSingleton<IVideoLibrary, JellyfinVideoLibrary>();
        serviceCollection.AddSingleton<FileFinder>();
        serviceCollection.AddSingleton(provider => new JobRunner(
            provider.GetRequiredService<FileFinder>(),
            provider.GetRequiredService<SubtitleSources>(),
            provider.GetRequiredService<ModelPicker>(),
            provider.GetRequiredService<IRetimerApi>(),
            provider.GetRequiredService<IVideoLibrary>(),
            provider.GetRequiredService<PluginStore>(),
            provider.GetRequiredService<IPluginSettings>(),
            provider.GetRequiredService<ILogger<JobRunner>>()));
        serviceCollection.AddSingleton<JobQueue>();
        serviceCollection.AddSingleton<IWatchSchedule>(provider => new JellyfinWatchSchedule(provider.GetRequiredService<ITaskManager>()));
        serviceCollection.AddSingleton(provider => new WatchService(
            provider.GetRequiredService<IPluginSettings>(),
            provider.GetRequiredService<IVideoLibrary>(),
            provider.GetRequiredService<SubtitleSources>(),
            provider.GetRequiredService<FileFinder>(),
            provider.GetRequiredService<JobQueue>(),
            provider.GetRequiredService<PluginStore>(),
            provider.GetRequiredService<ModelPicker>(),
            provider.GetRequiredService<IWatchSchedule>(),
            provider.GetRequiredService<ILogger<WatchService>>()));
        serviceCollection.AddHostedService<WatchEvents>();

        serviceCollection.AddSingleton<ICallerResolver, CallerResolver>();
        serviceCollection.AddSingleton<PageService>();
        serviceCollection.AddSingleton<SettingsService>();
        serviceCollection.AddSingleton(provider => new PluginServices(
            provider.GetRequiredService<PageService>(),
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<ICallerResolver>(),
            provider.GetRequiredService<WatchService>()));
    }
}
