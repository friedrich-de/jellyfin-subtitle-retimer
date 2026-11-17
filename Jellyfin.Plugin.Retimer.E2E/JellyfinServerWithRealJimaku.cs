using System;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// The same throwaway server, with the plugin talking to the real jimaku.cc.
/// It starts only where a real key is at hand.
/// </summary>
public sealed class JellyfinServerWithRealJimaku : JellyfinServer
{
    public static string? RealKey => Environment.GetEnvironmentVariable("JIMAKU_API_KEY")?.Trim();

    public static bool HasRealKey => !string.IsNullOrEmpty(RealKey);

    protected override bool UseStandInJimaku => false;

    protected override bool ShouldStart => Enabled && HasRealKey;
}
