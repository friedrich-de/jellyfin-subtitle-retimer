using System;

namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// Reads and stores the settings through the loaded plugin.
/// </summary>
public sealed class PluginSettings : IPluginSettings
{
    /// <inheritdoc />
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded.");
        plugin.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public string Reveal(string? stored) => Plugin.Instance?.Keys.Reveal(stored) ?? string.Empty;
}
