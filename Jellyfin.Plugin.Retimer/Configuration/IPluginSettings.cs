namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// Gives services the settings as they are right now, so a saved change applies without a restart.
/// </summary>
public interface IPluginSettings
{
    /// <summary>
    /// Gets the current settings.
    /// </summary>
    PluginConfiguration Current { get; }

    /// <summary>
    /// Stores new settings.
    /// </summary>
    /// <param name="configuration">The settings to store.</param>
    void Save(PluginConfiguration configuration);
}
