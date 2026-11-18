using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;

namespace Jellyfin.Plugin.Retimer.Sources;

/// <summary>
/// Jimaku as a subtitle source: one entry per show or film, Japanese subtitles only.
/// </summary>
internal sealed class JimakuSource : ISubtitleSource
{
    public const string SourceId = "jimaku";

    /// <summary>The largest file fetched from Jimaku. Packs of picture subtitles are the big ones.</summary>
    internal const long MaxDownloadBytes = 90L * 1024 * 1024;

    private readonly IJimakuClient _client;
    private readonly IPluginSettings _settings;

    public JimakuSource(IJimakuClient client, IPluginSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public string Id => SourceId;

    public string Name => "Jimaku";

    public bool IsReady => _settings.Current.JimakuEnabled && !string.IsNullOrWhiteSpace(_settings.Current.JimakuApiKey);

    public bool HasEntries => true;

    public bool Covers(string language) => language == "ja";

    public async Task<IReadOnlyList<SourceEntry>> SearchEntriesAsync(VideoQuery video, string? title, CancellationToken cancellationToken)
    {
        var found = new List<JimakuEntry>();
        try
        {
            if (title is not null)
            {
                await AddAsync(found, new JimakuEntryQuery { Title = title }, cancellationToken).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    // Jimaku searches anime unless told otherwise.
                    await AddAsync(found, new JimakuEntryQuery { Title = title, Anime = false }, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (video.AnilistId is { } anilistId)
                {
                    await AddAsync(found, new JimakuEntryQuery { AnilistId = anilistId }, cancellationToken).ConfigureAwait(false);
                }

                if (video.TmdbId is { } tmdbId)
                {
                    var id = video.IsMovie ? JimakuTmdbId.Movie(tmdbId) : JimakuTmdbId.Tv(tmdbId);
                    await AddAsync(found, new JimakuEntryQuery { TmdbId = id }, cancellationToken).ConfigureAwait(false);
                    await AddAsync(found, new JimakuEntryQuery { TmdbId = id, Anime = false }, cancellationToken).ConfigureAwait(false);
                }

                foreach (var name in new[] { video.Title, video.OriginalTitle }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var before = found.Count;
                    await AddAsync(found, new JimakuEntryQuery { Title = name }, cancellationToken).ConfigureAwait(false);
                    if (found.Count == before)
                    {
                        await AddAsync(found, new JimakuEntryQuery { Title = name, Anime = false }, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (JimakuException ex)
        {
            throw Translate(ex);
        }

        return found
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .Select(entry => new SourceEntry
            {
                Source = SourceId,
                Id = entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = entry.Name,
                EnglishName = entry.EnglishName,
                JapaneseName = entry.JapaneseName,
                AnilistId = entry.AnilistId,
                TmdbId = entry.TmdbId,
                IsMovie = entry.Flags.Movie,
                LastModified = entry.LastModified
            })
            .ToList();
    }

    public async Task<IReadOnlyList<SourceFile>> ListFilesAsync(VideoQuery video, string? entryId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(entryId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return [];
        }

        try
        {
            // All files, not only the ones Jimaku guesses for the episode: the picker reads the names itself.
            var files = await _client.GetFilesAsync(id, null, cancellationToken).ConfigureAwait(false);
            return files.Select(file => new SourceFile
            {
                Source = SourceId,
                Id = file.Name,
                EntryId = entryId,
                Name = file.Name,
                Size = file.Size,
                Modified = file.LastModified,
                Url = file.Url
            }).ToList();
        }
        catch (JimakuException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<DownloadedFile> DownloadAsync(SourceFile file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(file.Url))
        {
            throw new SourceException(SourceError.NotFound, "Jimaku gave no address for this file.");
        }

        try
        {
            return new DownloadedFile(file.Name, await _client.DownloadAsync(file.Url, MaxDownloadBytes, cancellationToken).ConfigureAwait(false));
        }
        catch (JimakuException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            // AniList id 1 exists on AniList, so the call is valid whether or not Jimaku has files for it.
            await _client.SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, cancellationToken).ConfigureAwait(false);
            return "Jimaku accepted the key.";
        }
        catch (JimakuException ex)
        {
            throw Translate(ex);
        }
    }

    private static SourceException Translate(JimakuException exception)
    {
        var error = exception.Error switch
        {
            JimakuError.NotConfigured => SourceError.NotConfigured,
            JimakuError.Unauthorized => SourceError.Unauthorized,
            JimakuError.RateLimited => SourceError.RateLimited,
            JimakuError.NotFound => SourceError.NotFound,
            _ => SourceError.Unavailable
        };

        return new SourceException(error, exception.Message, exception) { RetryAfter = exception.RetryAfter };
    }

    private async Task AddAsync(List<JimakuEntry> found, JimakuEntryQuery query, CancellationToken cancellationToken)
    {
        found.AddRange(await _client.SearchEntriesAsync(query, cancellationToken).ConfigureAwait(false));
    }
}
