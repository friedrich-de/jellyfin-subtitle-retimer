using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Retimer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests;

public sealed class KeyProtectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retimer-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string KeyFile(string name = "a") => Path.Combine(_root, name, KeyProtector.FileName);

    private static PluginConfiguration PlainSettings() => new()
    {
        JimakuApiKey = "jimaku-secret-0123",
        OpenSubtitlesApiKey = "os-secret-4567",
        OpenSubtitlesUsername = "al",
        OpenSubtitlesPassword = "os-password-89",
        RetimerUrl = "https://retimer.example.org",
        RetimerApiKey = "rt_secret-key",
        OpenRouterApiKey = "sk-or-v1-secret"
    };

    [Fact]
    public void A_key_is_stored_encrypted_and_read_back_as_typed()
    {
        var keys = new KeyProtector(KeyFile());

        var stored = keys.Protect("sk-or-v1-0123456789abcdef");

        Assert.StartsWith(KeyProtector.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", stored, StringComparison.Ordinal);
        Assert.Equal("sk-or-v1-0123456789abcdef", keys.Reveal(stored));

        // The same key twice looks different each time, so equal keys cannot be spotted in the file.
        Assert.NotEqual(stored, keys.Protect("sk-or-v1-0123456789abcdef"));

        // Encrypting what is already encrypted changes nothing, and an empty value stays empty.
        Assert.Equal(stored, keys.Protect(stored));
        Assert.Equal(string.Empty, keys.Protect(string.Empty));
        Assert.Equal(string.Empty, keys.Reveal(string.Empty));
        Assert.Equal("ключ & <ü>", keys.Reveal(keys.Protect("ключ & <ü>")));
    }

    [Fact]
    public void The_secret_is_a_file_only_Jellyfins_user_may_read_and_survives_a_restart()
    {
        var stored = new KeyProtector(KeyFile()).Protect("jimaku-secret");

        Assert.Equal(32, new FileInfo(KeyFile()).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile()));
        }

        // A new start of the server reads the same file.
        Assert.Equal("jimaku-secret", new KeyProtector(KeyFile()).Reveal(stored));
    }

    [Fact]
    public void A_key_encrypted_elsewhere_or_changed_is_not_read()
    {
        var stored = new KeyProtector(KeyFile("a")).Protect("jimaku-secret");
        var other = new KeyProtector(KeyFile("b"));
        other.Protect("make the second secret");

        Assert.Equal(string.Empty, other.Reveal(stored));
        Assert.False(other.CanReveal(stored));

        var keys = new KeyProtector(KeyFile("a"));
        var bytes = Convert.FromBase64String(stored[KeyProtector.Prefix.Length..]);
        bytes[20] ^= 1;
        Assert.Equal(string.Empty, keys.Reveal(KeyProtector.Prefix + Convert.ToBase64String(bytes)));
        Assert.Equal(string.Empty, keys.Reveal(KeyProtector.Prefix + "not base64!"));
        Assert.Equal(string.Empty, keys.Reveal(KeyProtector.Prefix + Convert.ToBase64String(new byte[10])));
    }

    [Fact]
    public void Without_its_file_an_encrypted_key_is_not_read_and_no_new_secret_is_made()
    {
        var stored = new KeyProtector(KeyFile("a")).Protect("jimaku-secret");
        File.Delete(KeyFile("a"));

        var keys = new KeyProtector(KeyFile("a"));

        Assert.Equal(string.Empty, keys.Reveal(stored));
        Assert.False(File.Exists(KeyFile("a")));
    }

    [Fact]
    public void A_key_stored_in_plain_by_an_older_version_is_still_read()
    {
        var keys = new KeyProtector(KeyFile());

        Assert.Equal("old-plain-key", keys.Reveal("old-plain-key"));
        Assert.True(keys.CanReveal("old-plain-key"));
        Assert.False(File.Exists(KeyFile()));
    }

    [Fact]
    public void Every_key_and_the_password_are_encrypted_and_nothing_else()
    {
        var keys = new KeyProtector(KeyFile());
        var settings = PlainSettings();
        settings.OpenRouterApiKey = string.Empty;

        Assert.True(keys.ProtectAll(settings));

        foreach (var value in new[] { settings.JimakuApiKey, settings.OpenSubtitlesApiKey, settings.OpenSubtitlesPassword, settings.RetimerApiKey })
        {
            Assert.StartsWith(KeyProtector.Prefix, value, StringComparison.Ordinal);
        }

        Assert.Equal(string.Empty, settings.OpenRouterApiKey);
        Assert.Equal("al", settings.OpenSubtitlesUsername);
        Assert.Equal("https://retimer.example.org", settings.RetimerUrl);
        Assert.Equal("os-password-89", keys.Reveal(settings.OpenSubtitlesPassword));
        Assert.False(keys.ProtectAll(settings));
    }

    [Fact]
    public void Keys_another_server_encrypted_are_emptied_so_the_settings_ask_for_them_again()
    {
        var foreign = PlainSettings();
        new KeyProtector(KeyFile("a")).ProtectAll(foreign);
        var keys = new KeyProtector(KeyFile("b"));
        foreign.RetimerApiKey = keys.Protect("rt_readable");

        Assert.True(keys.DropUnreadable(foreign));

        Assert.Equal(string.Empty, foreign.JimakuApiKey);
        Assert.Equal(string.Empty, foreign.OpenRouterApiKey);
        Assert.Equal("rt_readable", keys.Reveal(foreign.RetimerApiKey));
        Assert.False(keys.DropUnreadable(foreign));
    }

    [Fact]
    public void At_start_the_plugin_encrypts_the_keys_an_older_version_left_in_plain()
    {
        var paths = new Paths(_root);
        var file = Path.Combine(paths.PluginConfigurationsPath, typeof(Plugin).Assembly.GetName().Name + ".xml");
        Directory.CreateDirectory(paths.PluginConfigurationsPath);
        using (var stream = File.Create(file))
        {
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(stream, PlainSettings());
        }

        var plugin = new Plugin(paths, new Xml(), NullLogger<Plugin>.Instance);

        var xml = File.ReadAllText(file);
        foreach (var secret in new[] { "jimaku-secret-0123", "os-secret-4567", "os-password-89", "rt_secret-key", "sk-or-v1-secret" })
        {
            Assert.DoesNotContain(secret, xml, StringComparison.Ordinal);
        }

        Assert.Contains("<OpenSubtitlesUsername>al</OpenSubtitlesUsername>", xml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(paths.DataPath, PluginServiceRegistrator.DataFolderName, KeyProtector.FileName)));
        Assert.Equal("sk-or-v1-secret", new PluginSettings().Reveal(plugin.Configuration.OpenRouterApiKey));

        // A key that reaches the plugin in plain, through Jellyfin's own call for plugin settings too, is encrypted before it is written.
        var update = PlainSettings();
        update.JimakuApiKey = "typed-in-plain";
        plugin.UpdateConfiguration(update);
        Assert.DoesNotContain("typed-in-plain", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal("typed-in-plain", plugin.Keys.Reveal(plugin.Configuration.JimakuApiKey));

        // The next start finds nothing left to do and still reads every key.
        var again = new Plugin(paths, new Xml(), NullLogger<Plugin>.Instance);
        Assert.Equal("typed-in-plain", again.Keys.Reveal(again.Configuration.JimakuApiKey));
        Assert.Equal("os-password-89", again.Keys.Reveal(again.Configuration.OpenSubtitlesPassword));
    }

    private sealed class Paths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;

        public string WebPath => Path.Combine(root, "web");

        public string ProgramSystemPath => Path.Combine(root, "system");

        public string DataPath => Path.Combine(root, "data");

        public string ImageCachePath => Path.Combine(root, "cache", "images");

        public string PluginsPath => Path.Combine(root, "plugins");

        public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

        public string LogDirectoryPath => Path.Combine(root, "log");

        public string ConfigurationDirectoryPath => Path.Combine(root, "config");

        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");

        public string CachePath => Path.Combine(root, "cache");

        public string TempDirectory => Path.Combine(root, "temp");

        public string VirtualDataPath => "%AppDataPath%";

        public string TrickplayPath => Path.Combine(root, "trickplay");

        public string BackupPath => Path.Combine(root, "backup");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }

    private sealed class Xml : IXmlSerializer
    {
        public object? DeserializeFromStream(Type type, Stream stream)
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return new XmlSerializer(type).Deserialize(reader);
        }

        public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = File.Create(file);
            SerializeToStream(obj, stream);
        }

        public object? DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object? DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var stream = new MemoryStream(buffer);
            return DeserializeFromStream(type, stream);
        }
    }
}
