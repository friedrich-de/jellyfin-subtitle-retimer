using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.Retimer;

/// <summary>
/// Talks to the retimer: the hosted service or a self-hosted retimer-api, which share one HTTP contract.
/// </summary>
public interface IRetimerClient
{
    /// <summary>
    /// Checks that the retimer at the configured address answers and accepts the configured key.
    /// It submits no job and costs nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome.</returns>
    Task<RetimerConnectionStatus> CheckConnectionAsync(CancellationToken cancellationToken);
}
