using System.Security.Cryptography;
using Jellyfin.Plugin.Retimer.Configuration;

namespace Jellyfin.Plugin.Retimer.Tests.Support;

/// <summary>
/// Settings held in memory. Like the plugin, saving encrypts the keys and the services read them back with
/// <see cref="Reveal"/>; a key a test puts into <see cref="Current"/> in plain is read as it is.
/// </summary>
public sealed class StubSettings : IPluginSettings
{
    internal KeyProtector Keys { get; } = new(RandomNumberGenerator.GetBytes(32));

    public PluginConfiguration Current { get; set; } = new();

    public int Saves { get; private set; }

    public void Save(PluginConfiguration configuration)
    {
        Keys.ProtectAll(configuration);
        Current = configuration;
        Saves++;
    }

    public string Reveal(string? stored) => Keys.Reveal(stored);
}
