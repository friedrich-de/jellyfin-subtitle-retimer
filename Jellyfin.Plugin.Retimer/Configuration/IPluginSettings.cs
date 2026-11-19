namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// Gives services the settings as they are right now, so a saved change applies without a restart.
/// </summary>
public interface IPluginSettings
{
    /// <summary>
    /// Gets the current settings. Keys and the password in them are encrypted: read them with <see cref="Reveal"/>.
    /// </summary>
    PluginConfiguration Current { get; }

    /// <summary>
    /// Stores new settings. A key or password typed in plain is encrypted on the way.
    /// </summary>
    /// <param name="configuration">The settings to store.</param>
    void Save(PluginConfiguration configuration);

    /// <summary>
    /// Turns a key or password as <see cref="Current"/> holds it back into the plain value, for the one call that sends it.
    /// </summary>
    /// <param name="stored">The value as stored.</param>
    /// <returns>The plain value, or an empty string when none is stored or the stored one cannot be read.</returns>
    string Reveal(string? stored);
}
