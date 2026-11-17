namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// Reads the settings from the loaded plugin.
/// </summary>
public sealed class PluginSettings : IPluginSettings
{
    /// <inheritdoc />
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
