using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Retimer.Configuration;

namespace Jellyfin.Plugin.Retimer.Tests;

public class PluginTests
{
    private static string Resource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_settings_page_is_registered_and_embedded()
    {
        var page = Assert.Single(Plugin.Pages);

        Assert.Equal(Plugin.ConfigPageName, page.Name);
        Assert.Equal(Plugin.ConfigPageResource, page.EmbeddedResourcePath);
        Assert.Contains(page.EmbeddedResourcePath, typeof(Plugin).Assembly.GetManifestResourceNames());
        Assert.Contains("data-role=\"page\"", Resource(page.EmbeddedResourcePath), StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_page_uses_the_plugins_id()
    {
        Assert.Contains("'" + Plugin.PluginId.ToString("D") + "'", Resource(Plugin.ConfigPageResource), StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_page_has_one_field_for_every_setting()
    {
        var html = Resource(Plugin.ConfigPageResource);
        var settings = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["JimakuApiKey", "RetimerApiKey", "RetimerUrl"], settings.Order());
        foreach (var name in settings)
        {
            Assert.Contains("name=\"" + name + "\"", html, StringComparison.Ordinal);
            Assert.Contains(name + ": page.querySelector(", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Keys_are_typed_into_password_fields()
    {
        var html = Resource(Plugin.ConfigPageResource);

        Assert.Contains("name=\"JimakuApiKey\" type=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"RetimerApiKey\" type=\"password\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_page_links_to_the_settings_page_by_its_name()
    {
        Assert.Contains("configurationpage?name=" + Plugin.ConfigPageName + "'", Resource(Plugin.UserPageResource), StringComparison.Ordinal);
    }

    [Fact]
    public void New_settings_start_empty()
    {
        var configuration = new PluginConfiguration();

        Assert.Equal(string.Empty, configuration.JimakuApiKey);
        Assert.Equal(string.Empty, configuration.RetimerUrl);
        Assert.Equal(string.Empty, configuration.RetimerApiKey);
    }

    [Fact]
    public void Settings_survive_the_xml_file_Jellyfin_keeps_them_in()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, new PluginConfiguration { JimakuApiKey = "a&b<c>", RetimerUrl = "https://retimer.example.org", RetimerApiKey = "rt_x" });
        buffer.Position = 0;

        using var xml = XmlReader.Create(buffer);
        var read = Assert.IsType<PluginConfiguration>(serializer.Deserialize(xml));

        Assert.Equal("a&b<c>", read.JimakuApiKey);
        Assert.Equal("https://retimer.example.org", read.RetimerUrl);
        Assert.Equal("rt_x", read.RetimerApiKey);
    }

    [Fact]
    public void The_plugin_does_not_carry_copies_of_Jellyfins_own_assemblies()
    {
        var references = typeof(Plugin).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("MediaBrowser.Common", references);
        var pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(pluginDirectory, "Jellyfin.Plugin.Retimer.dll")));
    }
}
