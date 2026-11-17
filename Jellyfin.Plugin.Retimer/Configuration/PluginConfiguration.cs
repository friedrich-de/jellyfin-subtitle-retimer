using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// The plugin's settings. Jellyfin stores them as XML on the host, readable by its administrator.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the user's own Jimaku API key, from the account page on jimaku.cc.
    /// </summary>
    public string JimakuApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address of the retimer: the hosted service or a self-hosted retimer-api.
    /// </summary>
    public string RetimerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key for the retimer at <see cref="RetimerUrl"/>.
    /// </summary>
    public string RetimerApiKey { get; set; } = string.Empty;
}
