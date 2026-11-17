using System;

namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// A failed call to Jimaku.
/// </summary>
public sealed class JimakuException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JimakuException"/> class.
    /// </summary>
    /// <param name="error">Why the call failed.</param>
    /// <param name="message">A message for the log and the user.</param>
    /// <param name="retryAfter">For a rate limit, how long to wait.</param>
    /// <param name="innerException">The cause, if any.</param>
    public JimakuException(JimakuError error, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets why the call failed.
    /// </summary>
    public JimakuError Error { get; }

    /// <summary>
    /// Gets how long to wait before the next call, when Jimaku's rate limit was hit and it said so.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
