using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.Retimer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

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
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Keys = new KeyProtector(Path.Combine(applicationPaths.DataPath, PluginServiceRegistrator.DataFolderName, KeyProtector.FileName));
        Instance = this;
        ProtectStoredKeys(logger);
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

    /// <summary>
    /// Gets what encrypts the keys and the password in the settings file.
    /// </summary>
    internal KeyProtector Keys { get; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => Pages;

    /// <summary>
    /// Stores new settings, with every key and the password encrypted. Jellyfin's own call for plugin settings
    /// comes through here too, so a key typed in plain never reaches the file.
    /// </summary>
    /// <param name="configuration">The new settings.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration settings)
        {
            Keys.ProtectAll(settings);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Encrypts the keys a settings file from before 0.4.1.0 holds in plain, and empties the ones this server
    /// cannot read, such as those of a settings file copied from another server.
    /// </summary>
    private void ProtectStoredKeys(ILogger logger)
    {
        try
        {
            var settings = Configuration;
            var dropped = Keys.DropUnreadable(settings);
            var encrypted = Keys.ProtectAll(settings);
            if (dropped || encrypted)
            {
                SaveConfiguration();
            }

            if (encrypted)
            {
                logger.LogInformation("The keys in the plugin's settings are now stored encrypted");
            }

            if (dropped)
            {
                logger.LogWarning("A key in the plugin's settings could not be read with {Path} and was removed; enter it again in the settings", Keys.KeyFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogError(ex, "The keys in the plugin's settings could not be encrypted with {Path}; they stay as they are", Keys.KeyFilePath);
        }
    }
}
