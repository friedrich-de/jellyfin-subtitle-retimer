using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Library;

/// <summary>
/// Reads shows, episodes and tracks from Jellyfin, and saves subtitles through Jellyfin's own subtitle manager.
/// </summary>
internal sealed class JellyfinVideoLibrary : IVideoLibrary
{
    private static readonly TimeSpan _countsLifetime = TimeSpan.FromSeconds(45);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleManager _subtitleManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IPluginSettings _settings;
    private readonly ILogger<JellyfinVideoLibrary> _logger;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, HashSet<Guid> Missing)> _missingCache = [];

    public JellyfinVideoLibrary(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        ISubtitleManager subtitleManager,
        ISubtitleEncoder subtitleEncoder,
        IMediaEncoder mediaEncoder,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IPluginSettings settings,
        ILogger<JellyfinVideoLibrary> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleManager = subtitleManager;
        _subtitleEncoder = subtitleEncoder;
        _mediaEncoder = mediaEncoder;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<LibraryInfo> GetLibraries(Guid userId)
    {
        if (!TryFindUser(userId, out var user))
        {
            return [];
        }

        var hidden = _settings.Current.HiddenLibraryIds.ToHashSet();
        return ListLibraries(user).Where(library => !hidden.Contains(library.Id)).ToList();
    }

    public IReadOnlyList<LibraryInfo> GetAllLibraries() => ListLibraries(null);

    public IReadOnlyList<ShowSummary> GetShows(Guid userId, Guid libraryId, Func<Guid, string> languageOf)
    {
        if (!TryFindUser(userId, out var user))
        {
            return [];
        }

        var library = GetLibraries(userId).FirstOrDefault(l => l.Id == libraryId);
        if (library is null)
        {
            return [];
        }

        var summaries = new List<ShowSummary>();

        if (library.Kind != "movies")
        {
            var shows = _libraryManager.GetItemList(Query(user, libraryId, BaseItemKind.Series));
            var episodes = _libraryManager.GetItemList(Query(user, libraryId, BaseItemKind.Episode)).OfType<Episode>().ToList();
            var bySeries = episodes.GroupBy(e => e.SeriesId).ToDictionary(g => g.Key, g => g.ToList());
            var missingByLanguage = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

            foreach (var show in shows)
            {
                var language = languageOf(show.Id);
                if (!missingByLanguage.TryGetValue(language, out var missing))
                {
                    missing = Missing(user, libraryId, BaseItemKind.Episode, language);
                    missingByLanguage[language] = missing;
                }

                var own = bySeries.GetValueOrDefault(show.Id) ?? [];
                summaries.Add(new ShowSummary
                {
                    Id = show.Id,
                    Name = show.Name ?? string.Empty,
                    Year = show.ProductionYear,
                    ImageTag = ImageTag(show, ImageType.Primary),
                    Total = own.Count,
                    Have = own.Count(e => !missing.Contains(e.Id))
                });
            }
        }

        if (library.Kind != "tvshows")
        {
            var movies = _libraryManager.GetItemList(Query(user, libraryId, BaseItemKind.Movie));
            var missingByLanguage = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
            foreach (var movie in movies)
            {
                var language = languageOf(movie.Id);
                if (!missingByLanguage.TryGetValue(language, out var missing))
                {
                    missing = Missing(user, libraryId, BaseItemKind.Movie, language);
                    missingByLanguage[language] = missing;
                }

                summaries.Add(new ShowSummary
                {
                    Id = movie.Id,
                    Name = movie.Name ?? string.Empty,
                    Year = movie.ProductionYear,
                    IsMovie = true,
                    ImageTag = ImageTag(movie, ImageType.Primary),
                    Total = 1,
                    Have = missing.Contains(movie.Id) ? 0 : 1
                });
            }
        }

        return summaries.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ShowDetails? GetShow(Guid userId, Guid showId)
    {
        if (!TryFindUser(userId, out var user))
        {
            return null;
        }

        var item = FindItem(showId, user);
        if (item is Movie movie)
        {
            var video = Describe(movie, null);
            return new ShowDetails
            {
                Id = movie.Id,
                Name = movie.Name ?? string.Empty,
                Year = movie.ProductionYear,
                IsMovie = true,
                PosterTag = ImageTag(movie, ImageType.Primary),
                BackdropTag = ImageTag(movie, ImageType.Backdrop),
                Seasons = [new SeasonDetails(null, "Film", [video])]
            };
        }

        if (item is not Series series)
        {
            return null;
        }

        var episodes = EpisodesOf(series, user);
        var absolute = AbsoluteNumbers(episodes);
        var seasons = episodes
            .GroupBy(e => e.ParentIndexNumber)
            .OrderBy(g => g.Key is null or 0 ? int.MaxValue : g.Key.Value)
            .Select(g => new SeasonDetails(
                g.Key,
                g.Key switch
                {
                    null => "Other",
                    0 => "Specials",
                    _ => string.Create(CultureInfo.InvariantCulture, $"Season {g.Key}")
                },
                g.OrderBy(e => e.IndexNumber ?? int.MaxValue).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => Describe(e, absolute)).ToList()))
            .ToList();

        return new ShowDetails
        {
            Id = series.Id,
            Name = series.Name ?? string.Empty,
            Year = series.ProductionYear,
            PosterTag = ImageTag(series, ImageType.Primary),
            BackdropTag = ImageTag(series, ImageType.Backdrop),
            Seasons = seasons
        };
    }

    public VideoInfo? GetVideo(Guid userId, Guid itemId)
    {
        if (!TryFindUser(userId, out var user))
        {
            return null;
        }

        var item = FindItem(itemId, user);
        return item switch
        {
            Movie movie => Describe(movie, null),
            Episode episode => Describe(episode, episode.Series is { } series ? AbsoluteNumbers(EpisodesOf(series, null)) : null),
            _ => null
        };
    }

    public bool CanSee(Guid userId, Guid itemId) => TryFindUser(userId, out var user) && FindItem(itemId, user) is not null;

    public IReadOnlyList<SeasonEpisode> GetSeasonEpisodes(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is Movie movie)
        {
            return [new SeasonEpisode(1, null, movie.Name, FileNameOf(movie))];
        }

        if (item is not Episode episode || episode.Series is not { } series)
        {
            return [];
        }

        var all = EpisodesOf(series, null);
        var absolute = AbsoluteNumbers(all);
        return all
            .Where(e => e.ParentIndexNumber == episode.ParentIndexNumber && e.IndexNumber is not null)
            .GroupBy(e => e.IndexNumber!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new SeasonEpisode(g.Key, absolute.GetValueOrDefault(g.First().Id), g.First().Name, FileNameOf(g.First())))
            .ToList();
    }

    public async Task<IReadOnlyList<ReferenceUpload>> ReadTextTracksAsync(Guid itemId, string language, int max, IReadOnlyCollection<string> skipPaths, string scratchDirectory, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            return [];
        }

        var sources = _mediaSourceManager.GetStaticMediaSources(video, false, null);
        if (sources.Count == 0)
        {
            return [];
        }

        var source = sources[0];
        var tracks = source.MediaStreams
            .Where(stream => stream.Type == MediaStreamType.Subtitle && stream.IsTextSubtitleStream)
            .Where(stream => !stream.IsExternal || (stream.Path is not null && !skipPaths.Contains(stream.Path)))

            // A video can hold more tracks than are read. Tracks inside the file come first, forced tracks,
            // which hold only signs, last. Then the subtitle's own language, English, and the default track.
            .OrderBy(stream => stream.IsExternal)
            .ThenBy(stream => stream.IsForced)
            .ThenBy(stream => Languages.Matches(stream.Language, language) ? 0 : Languages.Matches(stream.Language, "en") ? 1 : 2)
            .ThenByDescending(stream => stream.IsDefault)
            .ThenBy(stream => stream.Index)
            .Take(max)
            .ToList();

        var cut = await CutOutTracksAsync(video, source.MediaStreams, tracks.Where(track => !track.IsExternal).ToList(), scratchDirectory, cancellationToken).ConfigureAwait(false);

        var uploads = new List<ReferenceUpload>();
        foreach (var track in tracks)
        {
            var format = FormatOf(track);
            try
            {
                byte[] content;
                if (cut.TryGetValue(track.Index, out var path))
                {
                    content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    File.Delete(path);
                }
                else if (track.IsExternal && track.Path is not null && File.Exists(track.Path)
                    && IsCodec(Path.GetExtension(track.Path), ".ass", ".ssa", ".srt"))
                {
                    // A subtitle file next to the video is taken as it is.
                    content = await File.ReadAllBytesAsync(track.Path, cancellationToken).ConfigureAwait(false);
                    format = IsCodec(Path.GetExtension(track.Path), ".srt") ? "srt" : "ass";
                }
                else
                {
                    var stream = await _subtitleEncoder.GetSubtitles(video, source.Id, track.Index, format, 0, 0, false, cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                        content = buffer.ToArray();
                    }
                }

                if (content.Length == 0)
                {
                    continue;
                }

                var name = string.Create(CultureInfo.InvariantCulture, $"track{track.Index}.{format}");
                uploads.Add(new ReferenceUpload(name, content, track.Language, track.Title, track.IsDefault, track.IsForced, track.IsHearingImpaired));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable track must not stop the others.
                _logger.LogWarning(ex, "Subtitle track {Index} of {Path} could not be read", track.Index, video.Path);
            }
        }

        return uploads;
    }

    private static string FormatOf(MediaStream track) => IsCodec(track.Codec, "ass", "ssa") ? "ass" : "srt";

    /// <summary>
    /// Cuts text tracks out of the video file with ffmpeg, all in one pass, and returns the files by Jellyfin's track index.
    /// Jellyfin's own extraction is not used for these: it keeps its results under the track's index, and that index
    /// moves when a subtitle file is saved next to the video, so it can hand out another track's text afterwards.
    /// </summary>
    private async Task<Dictionary<int, string>> CutOutTracksAsync(Video video, IReadOnlyList<MediaStream> streams, List<MediaStream> tracks, string scratchDirectory, CancellationToken cancellationToken)
    {
        var done = new Dictionary<int, string>();
        if (tracks.Count == 0 || !video.IsFileProtocol || !File.Exists(video.Path))
        {
            return done;
        }

        Directory.CreateDirectory(scratchDirectory);
        string PathOf(MediaStream track) => Path.Combine(scratchDirectory, string.Create(CultureInfo.InvariantCulture, $"cut{track.Index}.{FormatOf(track)}"));

        List<string> Arguments(IEnumerable<MediaStream> wanted)
        {
            var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", video.Path };
            foreach (var track in wanted)
            {
                var format = FormatOf(track);
                arguments.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"0:{IndexInFile(streams, track)}"), "-c:s", format, "-f", format, PathOf(track)]);
            }

            return arguments;
        }

        if (await RunFfmpegAsync(Arguments(tracks), TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false))
        {
            foreach (var track in tracks.Where(track => File.Exists(PathOf(track))))
            {
                done[track.Index] = PathOf(track);
            }

            return done;
        }

        // One track that cannot be converted fails the whole pass, so each track gets its own.
        foreach (var track in tracks)
        {
            if (tracks.Count > 1
                && await RunFfmpegAsync(Arguments([track]), TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false)
                && File.Exists(PathOf(track)))
            {
                done[track.Index] = PathOf(track);
            }
        }

        return done;
    }

    /// <summary>
    /// Jellyfin writes codec names in either case: "ass" for one track and "PGSSUB" for another.
    /// </summary>
    internal static bool IsCodec(string? codec, params string[] names) =>
        codec is not null && names.Contains(codec, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A stream's number inside the video file, which is what ffmpeg counts by. Jellyfin numbers the
    /// subtitle files next to a video before the streams inside it, so its own index is too high by
    /// the number of such files.
    /// </summary>
    internal static int IndexInFile(IReadOnlyList<MediaStream> streams, MediaStream stream) =>
        streams.Count(other => !other.IsExternal && other.Index < stream.Index);

    public async Task<ReferenceUpload?> ReadPictureTrackAsync(Guid itemId, string outputDirectory, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video || !video.IsFileProtocol || !File.Exists(video.Path))
        {
            return null;
        }

        var streams = video.GetMediaStreams();
        var track = streams
            .Where(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal && IsCodec(stream.Codec, "pgssub", "hdmv_pgs_subtitle", "pgs"))
            .OrderBy(stream => stream.IsForced)
            .ThenBy(stream => stream.Index)
            .FirstOrDefault();
        if (track is null)
        {
            return null;
        }

        Directory.CreateDirectory(outputDirectory);
        var raw = Path.Combine(outputDirectory, "track.sup");
        var arguments = new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", video.Path,
            "-map", string.Create(CultureInfo.InvariantCulture, $"0:{IndexInFile(streams, track)}"),
            "-c", "copy", "-f", "sup", raw
        };
        if (!await RunFfmpegAsync(arguments, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false) || !File.Exists(raw))
        {
            return null;
        }

        try
        {
            // A picture track is large and packs well, and Retimer API takes it gzipped.
            using var packed = new MemoryStream();
            var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true);
            await using (gzip.ConfigureAwait(false))
            {
                var input = File.OpenRead(raw);
                await using (input.ConfigureAwait(false))
                {
                    await input.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
                }
            }

            return new ReferenceUpload("track.sup.gz", packed.ToArray(), track.Language, track.Title, track.IsDefault, track.IsForced);
        }
        finally
        {
            File.Delete(raw);
        }
    }

    public async Task<bool> ExtractAudioAsync(Guid itemId, string? language, string outputPath, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video || !video.IsFileProtocol || !File.Exists(video.Path))
        {
            return false;
        }

        var streams = video.GetMediaStreams();
        var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio && !stream.IsExternal).ToList();
        if (audio.Count == 0)
        {
            return false;
        }

        // The subtitle is compared with what is spoken, so the audio in the subtitle's language is the one to send.
        var track = audio.FirstOrDefault(stream => language is not null && Languages.Matches(stream.Language, language))
            ?? audio.FirstOrDefault(stream => stream.IsDefault)
            ?? audio[0];

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var arguments = new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", video.Path,
            "-map", string.Create(CultureInfo.InvariantCulture, $"0:{IndexInFile(streams, track)}"),
            "-vn", "-sn", "-dn", "-ac", "1",
            "-c:a", "libopus", "-b:a", "24k", "-application", "voip",
            "-f", "ogg", outputPath
        };

        return await RunFfmpegAsync(arguments, TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false)
            && File.Exists(outputPath)
            && new FileInfo(outputPath).Length > 1024;
    }

    public async Task<SaveResult> SaveSubtitleAsync(Guid itemId, string language, string format, byte[] content, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            throw new InvalidOperationException("The video is no longer in the library.");
        }

        var before = ExternalSubtitlePaths(video);
        var filesBefore = SubtitleFilesNear(video);

        await _subtitleManager.UploadSubtitle(video, new SubtitleResponse
        {
            Language = language,
            Format = format,
            IsForced = false,
            IsHearingImpaired = false,
            Stream = new MemoryStream(content, writable: false)
        }).ConfigureAwait(false);

        // What Jellyfin's own upload call does next: look at this one video again, ahead of everything else queued.
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
        try
        {
            await _providerManager.RefreshSingleItem(video, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The refresh of {Path} after saving a subtitle failed; queueing one instead", video.Path);
            _providerManager.QueueRefresh(video.Id, options, RefreshPriority.High);
        }

        Invalidate();

        // "Saved" is only true once the video lists the track. A queued refresh may need a moment.
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (_libraryManager.GetItemById(itemId) is Video current)
            {
                var added = ExternalSubtitlePaths(current).Except(before, StringComparer.Ordinal).FirstOrDefault();
                if (added is not null)
                {
                    return new SaveResult(added, true);
                }
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(20))
            {
                break;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var written = SubtitleFilesNear(video).Except(filesBefore, StringComparer.Ordinal).FirstOrDefault();
        return new SaveResult(written, false);
    }

    public async Task<bool> RemoveSubtitleAsync(Guid itemId, string path, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video || string.IsNullOrEmpty(path))
        {
            return false;
        }

        var stream = video.GetMediaStreams().FirstOrDefault(s =>
            s.Type == MediaStreamType.Subtitle && s.IsExternal && string.Equals(s.Path, path, StringComparison.Ordinal));
        if (stream is not null)
        {
            await _subtitleManager.DeleteSubtitles(video, stream.Index).ConfigureAwait(false);
        }
        else if (File.Exists(path) && SubtitleFilesNear(video).Contains(path, StringComparer.Ordinal))
        {
            // Saved by the plugin but never listed by Jellyfin: the file is still the plugin's to remove.
            File.Delete(path);
        }
        else
        {
            return false;
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
        try
        {
            await _providerManager.RefreshSingleItem(video, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The refresh of {Path} after removing a subtitle failed; queueing one instead", video.Path);
            _providerManager.QueueRefresh(video.Id, options, RefreshPriority.High);
        }

        Invalidate();
        return true;
    }

    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _missingCache.Clear();
        }
    }

    private static InternalItemsQuery Query(User? user, Guid libraryId, BaseItemKind kind)
    {
        var query = user is null ? new InternalItemsQuery() : new InternalItemsQuery(user);
        query.ParentId = libraryId;
        query.Recursive = true;
        query.IncludeItemTypes = [kind];
        query.IsVirtualItem = false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = true };
        return query;
    }

    private static string? ImageTag(BaseItem item, ImageType type)
    {
        var image = item.GetImageInfo(type, 0);
        return image is null ? null : image.DateModified.Ticks.ToString("x", CultureInfo.InvariantCulture);
    }

    private static string? FileNameOf(BaseItem item) => string.IsNullOrEmpty(item.Path) ? null : Path.GetFileName(item.Path);

    private static int? IntId(BaseItem? item, string provider)
    {
        var value = TextId(item, provider);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static string? TextId(BaseItem? item, string provider)
    {
        if (item?.ProviderIds is null)
        {
            return null;
        }

        foreach (var (key, value) in item.ProviderIds)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static Dictionary<Guid, int> AbsoluteNumbers(IReadOnlyList<Episode> episodes)
    {
        // Counted from the first episode of the first season, skipping specials: how many releases number later seasons.
        var numbers = new Dictionary<Guid, int>();
        var count = 0;
        foreach (var season in episodes.Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber is not null).GroupBy(e => e.ParentIndexNumber!.Value).OrderBy(g => g.Key))
        {
            var last = 0;
            foreach (var group in season.GroupBy(e => e.IndexNumber!.Value).OrderBy(g => g.Key))
            {
                foreach (var episode in group)
                {
                    numbers[episode.Id] = count + group.Key;
                }

                last = Math.Max(last, group.Max(e => e.IndexNumberEnd ?? group.Key));
            }

            count += last;
        }

        return numbers;
    }

    private static List<string> ExternalSubtitlePaths(Video video) =>
        video.GetMediaStreams()
            .Where(stream => stream.Type == MediaStreamType.Subtitle && stream.IsExternal && !string.IsNullOrEmpty(stream.Path))
            .Select(stream => stream.Path)
            .ToList();

    private static List<string> SubtitleFilesNear(Video video)
    {
        var found = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(video.Path);
        foreach (var folder in new[] { video.ContainingFolderPath, video.GetInternalMetadataPath() })
        {
            try
            {
                if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(stem) && Directory.Exists(folder))
                {
                    found.AddRange(Directory.EnumerateFiles(folder).Where(file =>
                        Path.GetFileName(file).StartsWith(stem + ".", StringComparison.Ordinal) && SubtitleText.IsTextSubtitle(file)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed has nothing to offer.
            }
        }

        return found;
    }

    private List<LibraryInfo> ListLibraries(User? user)
    {
        var root = _libraryManager.GetUserRootFolder();
        IEnumerable<BaseItem> folders = user is null ? root.Children : root.GetChildren(user, true, new InternalItemsQuery(user));
        var libraries = new List<LibraryInfo>();
        foreach (var folder in folders.OfType<Folder>())
        {
            var kind = (folder as CollectionFolder)?.CollectionType ?? _libraryManager.GetContentType(folder);
            var name = kind switch
            {
                CollectionType.tvshows => "tvshows",
                CollectionType.movies => "movies",
                null or CollectionType.unknown => "mixed",
                _ => null
            };

            if (name is not null)
            {
                libraries.Add(new LibraryInfo(folder.Id, folder.Name ?? string.Empty, name));
            }
        }

        return libraries.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Resolves who asks. An empty id is the server itself, which sees everything (<paramref name="user"/> is then <c>null</c>).
    /// An id that belongs to nobody sees nothing.
    /// </summary>
    private bool TryFindUser(Guid userId, out User? user)
    {
        user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        return userId == Guid.Empty || user is not null;
    }

    private BaseItem? FindItem(Guid id, User? user)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null || (user is not null && !item.IsVisible(user, false)))
        {
            return null;
        }

        return item;
    }

    private List<Episode> EpisodesOf(Series series, User? user)
    {
        var query = user is null ? new InternalItemsQuery() : new InternalItemsQuery(user);
        query.AncestorIds = [series.Id];
        query.Recursive = true;
        query.IncludeItemTypes = [BaseItemKind.Episode];
        query.IsVirtualItem = false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = true };
        return _libraryManager.GetItemList(query).OfType<Episode>().ToList();
    }

    private HashSet<Guid> Missing(User? user, Guid libraryId, BaseItemKind kind, string language)
    {
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{user?.Id}|{libraryId}|{kind}|{language}");
        lock (_cacheLock)
        {
            if (_missingCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < _countsLifetime)
            {
                return cached.Missing;
            }
        }

        // Video tracks carry codes such as "jpn" or "ja". Only codes the library really holds need a query.
        var present = _libraryManager.GetMediaStreamLanguages(MediaStreamType.Subtitle);
        var codes = present.Where(code => Languages.Matches(code, language)).Distinct(StringComparer.Ordinal).ToList();

        HashSet<Guid>? missing = null;
        if (codes.Count == 0)
        {
            missing = _libraryManager.GetItemList(Query(user, libraryId, kind)).Select(item => item.Id).ToHashSet();
        }
        else
        {
            foreach (var code in codes)
            {
                var query = Query(user, libraryId, kind);
                query.HasNoSubtitleTrackWithLanguage = code;
                var without = _libraryManager.GetItemList(query).Select(item => item.Id).ToHashSet();
                if (missing is null)
                {
                    missing = without;
                }
                else
                {
                    missing.IntersectWith(without);
                }
            }
        }

        missing ??= [];
        lock (_cacheLock)
        {
            _missingCache[cacheKey] = (DateTimeOffset.UtcNow, missing);
        }

        return missing;
    }

    private static VideoInfo Describe(Video video, Dictionary<Guid, int>? absolute)
    {
        var streams = video.GetMediaStreams();
        var episode = video as Episode;
        var series = episode?.Series;
        BaseItem show = series ?? (BaseItem)video;

        return new VideoInfo
        {
            Id = video.Id,
            ShowId = show.Id,
            ShowName = show.Name ?? string.Empty,
            ShowOriginalName = string.IsNullOrWhiteSpace(show.OriginalTitle) ? null : show.OriginalTitle,
            Year = show.ProductionYear,
            IsMovie = episode is null,
            Season = episode?.ParentIndexNumber,
            Episode = episode?.IndexNumber,
            AbsoluteEpisode = absolute is not null && absolute.TryGetValue(video.Id, out var number) ? number : null,
            Name = video.Name ?? string.Empty,
            FileName = FileNameOf(video),
            IsLocalFile = video.IsFileProtocol && !string.IsNullOrEmpty(video.Path),
            RunTimeTicks = video.RunTimeTicks,
            ImageTag = ImageTag(video, ImageType.Primary),
            AnilistId = IntId(show, "AniList"),
            TmdbId = IntId(show, "Tmdb"),
            ImdbId = TextId(show, "Imdb"),
            EpisodeImdbId = episode is null ? null : TextId(episode, "Imdb"),
            Subtitles = streams
                .Where(stream => stream.Type == MediaStreamType.Subtitle)
                .Select(stream => new TrackInfo
                {
                    Index = stream.Index,
                    Language = stream.Language,
                    Title = stream.Title,
                    Codec = stream.Codec ?? string.Empty,
                    IsExternal = stream.IsExternal,
                    IsText = stream.IsTextSubtitleStream,
                    IsDefault = stream.IsDefault,
                    IsForced = stream.IsForced,
                    Path = stream.IsExternal ? stream.Path : null
                })
                .ToList(),
            AudioLanguages = streams
                .Where(stream => stream.Type == MediaStreamType.Audio)
                .Select(stream => Languages.NameOf(stream.Language))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    private async Task<bool> RunFfmpegAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return false;
        }

        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg ended with code {Code}: {Errors}", process.ExitCode, await errors.ConfigureAwait(false));
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended by itself in the meantime.
            }

            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("ffmpeg did not finish within {Minutes} minutes", limit.TotalMinutes);
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "ffmpeg could not be run");
            return false;
        }
    }
}
