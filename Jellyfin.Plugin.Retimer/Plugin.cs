using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Retimer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Retimer;

/// <summary>
/// The Subtitle Retimer plugin: finds subtitles for the shows and films of the library, retimes them with Retimer API
/// when that is switched on, and saves them through Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The name Jellyfin shows for the plugin.
    /// </summary>
    public const string PluginName = "Subtitle Retimer";

    /// <summary>
    /// The name of the settings page, as used in <c>/web/ConfigurationPage?name=</c>.
    /// </summary>
    public const string ConfigPageName = "SubtitleRetimer";

    /// <summary>
    /// The embedded resource that holds the settings page.
    /// </summary>
    public const string ConfigPageResource = "Jellyfin.Plugin.Retimer.Configuration.configPage.html";

    /// <summary>
    /// The embedded resource that holds the page for all signed-in users.
    /// </summary>
    public const string UserPageResource = "Jellyfin.Plugin.Retimer.Web.index.html";

    /// <summary>
    /// The embedded resource that holds the page's script.
    /// </summary>
    public const string UserScriptResource = "Jellyfin.Plugin.Retimer.Web.app.js";

    /// <summary>
    /// The embedded resource that holds the page's styles.
    /// </summary>
    public const string UserStylesResource = "Jellyfin.Plugin.Retimer.Web.app.css";

    /// <summary>
    /// The plugin's fixed id.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("e9fdba44-78af-4384-bb85-6df44aa841d8");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Finds subtitles for your shows and films, retimes them with Retimer API, and saves them where Jellyfin lists them.";

    /// <summary>
    /// Gets the pages Jellyfin's dashboard serves for this plugin. Only administrators can open them.
    /// </summary>
    /// <returns>The settings page.</returns>
    public static IReadOnlyList<PluginPageInfo> Pages { get; } =
    [
        new PluginPageInfo
        {
            Name = ConfigPageName,
            DisplayName = PluginName,
            EmbeddedResourcePath = ConfigPageResource,
            EnableInMainMenu = true,
            MenuIcon = "subtitles"
        }
    ];

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => Pages;
}
