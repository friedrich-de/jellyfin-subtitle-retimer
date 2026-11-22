using System.Collections.Generic;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// A server of its own for watch mode, whose checks start jobs by themselves. The stand-in Jimaku
/// also lists a file for episode 6, which the test adds to the library later.
/// </summary>
public sealed class JellyfinServerForWatch : JellyfinServer
{
    protected override IReadOnlyList<int> ListedEpisodes => [3, 4, 5, 6];
}
