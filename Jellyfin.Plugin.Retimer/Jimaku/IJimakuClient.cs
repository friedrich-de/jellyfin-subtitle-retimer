using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// Searches jimaku.cc with the user's own API key.
/// </summary>
public interface IJimakuClient
{
    /// <summary>
    /// Runs one search exactly as given.
    /// </summary>
    /// <param name="query">What to search for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching entries, possibly none.</returns>
    /// <exception cref="JimakuException">The call failed.</exception>
    Task<IReadOnlyList<JimakuEntry>> SearchEntriesAsync(JimakuEntryQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the entries for a series or movie: by AniList id first, then by TMDB id, then by title.
    /// The first step that finds something wins; steps without a value are skipped.
    /// </summary>
    /// <param name="lookup">The ids and the title that are known.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching entries, possibly none.</returns>
    /// <exception cref="JimakuException">A call failed.</exception>
    Task<IReadOnlyList<JimakuEntry>> FindEntriesAsync(JimakuEntryQuery lookup, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the files of an entry.
    /// </summary>
    /// <param name="entryId">The entry's id on Jimaku.</param>
    /// <param name="episode">Keep only files for this episode. Jimaku guesses the episode from the
    /// file name and ignores this for movies.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The files, possibly none.</returns>
    /// <exception cref="JimakuException">The call failed.</exception>
    Task<IReadOnlyList<JimakuFile>> GetFilesAsync(long entryId, int? episode, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a file from the address Jimaku listed for it. Only addresses at Jimaku itself are fetched.
    /// </summary>
    /// <param name="url">The file's address, as <see cref="JimakuFile.Url"/> gave it.</param>
    /// <param name="maxBytes">The largest file to accept.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="JimakuException">The call failed, or the file is too large.</exception>
    Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken);
}
