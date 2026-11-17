using Jellyfin.Plugin.Retimer.Configuration;

namespace Jellyfin.Plugin.Retimer.Tests.Support;

public sealed class StubSettings : IPluginSettings
{
    public PluginConfiguration Current { get; set; } = new();
}
