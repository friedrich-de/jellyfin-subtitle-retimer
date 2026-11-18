using System;
using System.IO;
using System.Linq;
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
    public void The_dashboard_page_is_registered_and_embedded_and_leads_to_the_settings()
    {
        var page = Assert.Single(Plugin.Pages);

        Assert.Equal(Plugin.ConfigPageName, page.Name);
        Assert.Equal(Plugin.ConfigPageResource, page.EmbeddedResourcePath);
        var html = Resource(page.EmbeddedResourcePath);
        Assert.Contains("data-role=\"page\"", html, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('Retimer')", html, StringComparison.Ordinal);
        Assert.Contains("'#/settings'", html, StringComparison.Ordinal);

        // The dashboard page holds no field for a key: the keys are typed on the plugin's own page.
        Assert.DoesNotContain("<input", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_and_its_files_are_embedded()
    {
        var names = typeof(Plugin).Assembly.GetManifestResourceNames();

        Assert.Contains(Plugin.UserPageResource, names);
        Assert.Contains(Plugin.UserScriptResource, names);
        Assert.Contains(Plugin.UserStylesResource, names);
    }

    [Fact]
    public void The_service_is_called_Retimer_API_on_the_page()
    {
        var script = Resource(Plugin.UserScriptResource);

        Assert.Contains("Retimer API", script, StringComparison.Ordinal);
        Assert.DoesNotContain("the retimer", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What is Retimer API? See it on GitHub", script, StringComparison.Ordinal);
    }

    [Fact]
    public void New_settings_start_safe()
    {
        var configuration = new PluginConfiguration();

        Assert.Equal(string.Empty, configuration.JimakuApiKey);
        Assert.Equal(string.Empty, configuration.RetimerUrl);
        Assert.Equal(string.Empty, configuration.RetimerApiKey);
        Assert.Equal(string.Empty, configuration.OpenRouterApiKey);
        Assert.False(configuration.RetimerEnabled);
        Assert.Equal(PluginConfiguration.AudioNever, configuration.SendAudio);
        Assert.False(configuration.ConvertPictureSubtitles);
        Assert.Equal("ja", configuration.SubtitleLanguage);
        Assert.Equal("managers", configuration.FetchAccess);
        Assert.Equal(PluginConfiguration.DefaultModelId, Assert.Single(configuration.Models).Id);
        Assert.Equal(PluginConfiguration.DefaultModelId, configuration.ModelInUse);
    }

    [Fact]
    public void Settings_survive_the_xml_file_Jellyfin_keeps_them_in()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();
        var library = Guid.NewGuid();
        serializer.Serialize(buffer, new PluginConfiguration
        {
            JimakuApiKey = "a&b<c>",
            RetimerUrl = "https://retimer.example.org",
            RetimerApiKey = "rt_x",
            RetimerEnabled = true,
            SendAudio = PluginConfiguration.AudioFallback,
            Models = [new ModelEntry { Id = "a/b", Name = "B" }, new ModelEntry { Id = "c/d", Name = "D" }],
            ModelInUse = "c/d",
            HiddenLibraryIds = [library],
            MaxCostPerDay = 1.5
        });
        buffer.Position = 0;

        using var xml = XmlReader.Create(buffer);
        var read = Assert.IsType<PluginConfiguration>(serializer.Deserialize(xml));

        Assert.Equal("a&b<c>", read.JimakuApiKey);
        Assert.Equal("https://retimer.example.org", read.RetimerUrl);
        Assert.Equal("rt_x", read.RetimerApiKey);
        Assert.True(read.RetimerEnabled);
        Assert.Equal(PluginConfiguration.AudioFallback, read.SendAudio);
        Assert.Equal(["a/b", "c/d"], read.Models.Select(m => m.Id));
        Assert.Equal("c/d", read.ModelInUse);
        Assert.Equal([library], read.HiddenLibraryIds);
        Assert.Equal(1.5, read.MaxCostPerDay);
    }

    [Fact]
    public void Reading_the_default_settings_back_does_not_double_the_list_of_models()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, new PluginConfiguration());
        buffer.Position = 0;

        using var xml = XmlReader.Create(buffer);
        var read = Assert.IsType<PluginConfiguration>(serializer.Deserialize(xml));

        Assert.Single(read.Models);
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
