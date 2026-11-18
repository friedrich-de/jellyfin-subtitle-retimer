using Jellyfin.Plugin.Retimer.Configuration;

namespace Jellyfin.Plugin.Retimer.Tests.Support;

public sealed class StubSettings : IPluginSettings
{
    public PluginConfiguration Current { get; set; } = new();

    public int Saves { get; private set; }

    public void Save(PluginConfiguration configuration)
    {
        Current = configuration;
        Saves++;
    }
}
