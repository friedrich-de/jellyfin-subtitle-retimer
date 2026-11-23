using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Sources;
using Jellyfin.Plugin.Retimer.Watch;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests.Support;

/// <summary>
/// A library in memory: one library, shows made of the videos that were added, and a record of what was saved.
/// </summary>
internal sealed class FakeLibrary : IVideoLibrary
{
    public static readonly Guid LibraryId = Guid.Parse("11111111-0000-0000-0000-000000000001");

    public Dictionary<Guid, VideoInfo> Videos { get; } = [];

    /// <summary>Gets the users who see nothing.</summary>
    public HashSet<Guid> Blind { get; } = [];

    public List<ReferenceUpload> Tracks { get; set; } = [];

    public ReferenceUpload? PictureTrack { get; set; }

    public bool AudioWorks { get; set; } = true;

    public bool Listed { get; set; } = true;

    public List<(Guid Item, string Language, string Format, byte[] Content)> Saved { get; } = [];

    public List<(Guid Item, string Path)> Removed { get; } = [];

    public List<IReadOnlyCollection<string>> SkippedPaths { get; } = [];

    public int AudioExtractions { get; private set; }

    public int Invalidations { get; private set; }

    public VideoInfo AddEpisode(Guid showId, string showName, int season, int episode, string name, string? fileName = null)
    {
        var video = new VideoInfo
        {
            Id = Guid.NewGuid(),
            ShowId = showId,
            ShowName = showName,
            Season = season,
            Episode = episode,
            AbsoluteEpisode = episode,
            Name = name,
            FileName = fileName ?? $"{showName} - S{season:00}E{episode:00}.mkv",
            IsLocalFile = true,
            RunTimeTicks = TimeSpan.FromMinutes(24).Ticks,
            Year = 2011
        };
        Videos[video.Id] = video;
        return video;
    }

    public IReadOnlyList<LibraryInfo> GetLibraries(Guid userId) => Blind.Contains(userId) ? [] : GetAllLibraries();

    public IReadOnlyList<LibraryInfo> GetAllLibraries() => [new LibraryInfo(LibraryId, "Anime", "tvshows")];

    public IReadOnlyList<ShowSummary> GetShows(Guid userId, Guid libraryId, Func<Guid, string> languageOf) =>
        Blind.Contains(userId) || libraryId != LibraryId
            ? []
            : Videos.Values.GroupBy(v => v.ShowId).Select(g => new ShowSummary
            {
                Id = g.Key,
                Name = g.First().ShowName,
                Total = g.Count(),
                Have = g.Count(v => v.Subtitles.Any(t => Languages.Matches(t.Language, languageOf(g.Key))))
            }).ToList();

    public ShowDetails? GetShow(Guid userId, Guid showId)
    {
        var videos = Blind.Contains(userId) ? [] : Videos.Values.Where(v => v.ShowId == showId).ToList();
        if (videos.Count == 0)
        {
            return null;
        }

        return new ShowDetails
        {
            Id = showId,
            Name = videos[0].ShowName,
            IsMovie = videos[0].IsMovie,
            Seasons = videos.GroupBy(v => v.Season).OrderBy(g => g.Key)
                .Select(g => new SeasonDetails(g.Key, "Season " + g.Key, g.OrderBy(v => v.Episode).ToList()))
                .ToList()
        };
    }

    public VideoInfo? GetVideo(Guid userId, Guid itemId) => Blind.Contains(userId) ? null : Videos.GetValueOrDefault(itemId);

    public bool CanSee(Guid userId, Guid itemId) =>
        !Blind.Contains(userId) && (Videos.ContainsKey(itemId) || Videos.Values.Any(v => v.ShowId == itemId));

    public IReadOnlyList<SeasonEpisode> GetSeasonEpisodes(Guid itemId)
    {
        var video = Videos[itemId];
        return Videos.Values
            .Where(v => v.ShowId == video.ShowId && v.Season == video.Season)
            .OrderBy(v => v.Episode)
            .Select(v => new SeasonEpisode(v.Episode ?? 1, v.AbsoluteEpisode, v.Name, v.FileName))
            .ToList();
    }

    public Task<IReadOnlyList<ReferenceUpload>> ReadTextTracksAsync(Guid itemId, string language, int max, IReadOnlyCollection<string> skipPaths, string scratchDirectory, CancellationToken cancellationToken)
    {
        SkippedPaths.Add(skipPaths);
        return Task.FromResult<IReadOnlyList<ReferenceUpload>>(Tracks.Take(max).ToList());
    }

    public Task<ReferenceUpload?> ReadPictureTrackAsync(Guid itemId, string outputDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(PictureTrack);

    public async Task<bool> ExtractAudioAsync(Guid itemId, string? language, string outputPath, CancellationToken cancellationToken)
    {
        AudioExtractions++;
        if (!AudioWorks)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, new byte[4000], cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<SaveResult> SaveSubtitleAsync(Guid itemId, string language, string format, byte[] content, CancellationToken cancellationToken)
    {
        Saved.Add((itemId, language, format, content));
        var path = Path.Combine(Path.GetTempPath(), "fake-media", itemId.ToString("N") + "." + language + "." + format);
        var video = Videos[itemId];
        Videos[itemId] = video with
        {
            Subtitles = [.. video.Subtitles, new TrackInfo { Index = 10 + video.Subtitles.Count, Language = language, Codec = format, IsExternal = true, IsText = true, Path = path }]
        };
        return Task.FromResult(new SaveResult(path, Listed));
    }

    public Task<bool> RemoveSubtitleAsync(Guid itemId, string path, CancellationToken cancellationToken)
    {
        Removed.Add((itemId, path));
        var video = Videos[itemId];
        var had = video.Subtitles.Any(t => t.Path == path);
        Videos[itemId] = video with { Subtitles = video.Subtitles.Where(t => t.Path != path).ToList() };
        return Task.FromResult(had);
    }

    public void Invalidate() => Invalidations++;

    /// <summary>Gets when each show came into the library, for "watch new shows".</summary>
    public Dictionary<Guid, DateTimeOffset> ShowsAdded { get; } = [];

    public IReadOnlyList<Guid> GetShowsAddedSince(DateTimeOffset since) =>
        ShowsAdded.Where(pair => pair.Value >= since).Select(pair => pair.Key).ToList();
}

/// <summary>
/// A subtitle source that lists and hands out what the test gave it.
/// </summary>
internal sealed class FakeSource : ISubtitleSource
{
    public string Id { get; set; } = "jimaku";

    public string Name { get; set; } = "Jimaku";

    public bool IsReady { get; set; } = true;

    public bool HasEntries { get; set; } = true;

    public string? OnlyLanguage { get; set; }

    public List<SourceEntry> Entries { get; } = [];

    public List<SourceFile> Files { get; } = [];

    public Dictionary<string, DownloadedFile> Content { get; } = [];

    public Dictionary<string, SourceException> DownloadFailures { get; } = [];

    public SourceException? SearchFailure { get; set; }

    public List<string?> Searches { get; } = [];

    public List<string?> Listings { get; } = [];

    public List<string> Downloads { get; } = [];

    public bool Covers(string language) => OnlyLanguage is null || OnlyLanguage == language;

    public SourceEntry AddEntry(string id, string name, int? anilistId = null)
    {
        var entry = new SourceEntry { Source = Id, Id = id, Name = name, AnilistId = anilistId };
        Entries.Add(entry);
        return entry;
    }

    public SourceFile AddFile(string name, string entryId, string? text = null, byte[]? bytes = null)
    {
        var file = new SourceFile { Source = Id, Id = name, EntryId = HasEntries ? entryId : null, Name = name, Size = 30000, ForThisVideo = !HasEntries };
        Files.Add(file);
        Content[name] = new DownloadedFile(name, bytes ?? Encoding.UTF8.GetBytes(text ?? Subtitles.Japanese));
        return file;
    }

    public Task<IReadOnlyList<SourceEntry>> SearchEntriesAsync(VideoQuery video, string? title, CancellationToken cancellationToken)
    {
        Searches.Add(title);
        return SearchFailure is null
            ? Task.FromResult<IReadOnlyList<SourceEntry>>(Entries.ToList())
            : Task.FromException<IReadOnlyList<SourceEntry>>(SearchFailure);
    }

    public Task<IReadOnlyList<SourceFile>> ListFilesAsync(VideoQuery video, string? entryId, CancellationToken cancellationToken)
    {
        Listings.Add(entryId);
        return Task.FromResult<IReadOnlyList<SourceFile>>(Files.Where(f => !HasEntries || f.EntryId == entryId).ToList());
    }

    public Task<DownloadedFile> DownloadAsync(SourceFile file, CancellationToken cancellationToken)
    {
        Downloads.Add(file.Id);
        return DownloadFailures.TryGetValue(file.Id, out var failure)
            ? Task.FromException<DownloadedFile>(failure)
            : Task.FromResult(Content[file.Id]);
    }

    public Task<string> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(Name + " accepted the key.");
}

/// <summary>
/// A model that answers each tool it is forced or allowed to call with what the test set for that tool.
/// </summary>
internal sealed class FakeModel : IModelClient
{
    public ModelEntry? Current { get; set; } = new() { Id = "test/model", Name = "Test Model" };

    public Dictionary<string, Func<JsonArray, JsonObject>> Tools { get; } = [];

    public ModelException? Failure { get; set; }

    public double CostPerCall { get; set; } = 0.001;

    public List<(string Model, string? Forced, string Prompt)> Calls { get; } = [];

    public int CallsOf(string tool) => Calls.Count(c => c.Forced == tool);

    public Task<ChatAnswer> ChatAsync(string modelId, JsonArray messages, JsonArray tools, string? forcedTool, int maxTokens, CancellationToken cancellationToken)
    {
        var offered = tools.Select(t => t!["function"]!["name"]!.ToString()).ToList();
        Calls.Add((modelId, forcedTool, string.Join("\n", messages.Select(m => m!["content"]?.ToString()))));
        if (Failure is not null)
        {
            return Task.FromException<ChatAnswer>(Failure);
        }

        // The forced tool, else the first offered tool the test has an answer for (the final one first).
        var name = forcedTool ?? offered.AsEnumerable().Reverse().FirstOrDefault(Tools.ContainsKey);
        if (name is null || !Tools.TryGetValue(name, out var answer))
        {
            return Task.FromResult(new ChatAnswer([], "I have no tool to call.", CostPerCall, new JsonObject { ["role"] = "assistant" }));
        }

        var call = new ToolCall("call_1", name, answer(messages));
        return Task.FromResult(new ChatAnswer([call], null, CostPerCall, new JsonObject { ["role"] = "assistant" }));
    }

    /// <summary>Lets the model map every listed file whose name holds " - NN" or "ENN" to that episode.</summary>
    public FakeModel MapsByNumber()
    {
        Tools["map_files"] = messages =>
        {
            var user = messages[1]!["content"]!.ToString();
            var files = user[user.IndexOf("Files:\n", StringComparison.Ordinal)..].Split('\n').Skip(1).Where(l => l.Contains(" | ", StringComparison.Ordinal)).ToList();
            var episodes = new Dictionary<int, JsonArray>();
            foreach (var line in files)
            {
                var index = int.Parse(line[..line.IndexOf(' ', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(?: - |E)(\d{2})\b");
                if (match.Success)
                {
                    var episode = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    (episodes.TryGetValue(episode, out var list) ? list : episodes[episode] = []).Add(index);
                }
            }

            return new JsonObject
            {
                ["episodes"] = new JsonArray(episodes.Select(pair => (JsonNode)new JsonObject { ["episode"] = pair.Key, ["files"] = pair.Value, ["reason"] = "the name carries the episode" }).ToArray())
            };
        };
        return this;
    }

    public FakeModel Language(string language, string? second = null)
    {
        Tools["language_verdict"] = _ => new JsonObject { ["language"] = language, ["second_language"] = second, ["reason"] = "read from the sample" };
        return this;
    }

    public FakeModel ChoosesEntry(string? id) => id is null ? ChoosesEntries() : ChoosesEntries(id);

    /// <summary>Lets the model choose these entries, by id, in this order. None for an empty list.</summary>
    public FakeModel ChoosesEntries(params string[] ids)
    {
        Tools["choose_entries"] = _ => new JsonObject
        {
            ["reason"] = "the title and the season fit",
            ["entries"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = string.Empty }).ToArray())
        };
        return this;
    }
}

/// <summary>
/// A Retimer API that finishes every job at once with the outcome the test queued.
/// </summary>
internal sealed class FakeRetimerApi : IRetimerApi
{
    private readonly Dictionary<string, RetimerJob> _jobs = [];
    private int _next;

    public RetimerFeatures Features { get; set; } = new() { ReferenceTracks = true, RemoveSignsAndCredits = true, Ocr = true, MaxReferenceSubtitles = 6 };

    /// <summary>Gets the answers for the jobs to come, first in, first out. Without one, a job ends as synced with the first track chosen.</summary>
    public Queue<Func<RetimerSubmission, RetimerJob>> Outcomes { get; } = new();

    public List<RetimerSubmission> Submissions { get; } = [];

    public List<(string Name, byte[] Content, string? Language)> OcrRequests { get; } = [];

    public List<string> Deleted { get; } = [];

    public byte[] Result { get; set; } = Encoding.UTF8.GetBytes("1\n00:00:02,000 --> 00:00:03,000\n retimed こんにちは\n");

    public byte[]? OcrResult { get; set; } = Encoding.UTF8.GetBytes(Subtitles.Japanese);

    public RetimerException? Failure { get; set; }

    public static RetimerJob Synced(int? chosen = 0, double cost = 0.02) => new()
    {
        Status = "finished",
        Outcome = "synced",
        CostUsd = cost,
        Fit = "constant",
        ShiftsMs = [900],
        HasReferenceReport = true,
        ChosenReference = chosen,
        Candidates = chosen is null ? [] : [new RetimerCandidate(chosen.Value, "en", null, "chosen", null)]
    };

    public static RetimerJob NoMatch(params string[] verdicts) => new()
    {
        Status = "finished",
        Outcome = "rejected_no_match",
        CostUsd = 0.01,
        HasReferenceReport = true,
        ChosenReference = null,
        ReferenceReason = "No track shares enough lines with the subtitle.",
        Candidates = verdicts.Select((v, i) => new RetimerCandidate(i, "en", null, v, null)).ToList()
    };

    public Task<RetimerFeatures> GetFeaturesAsync(CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(Features) : Task.FromException<RetimerFeatures>(Failure);

    public Task<RetimerJob> SubmitAsync(RetimerSubmission submission, CancellationToken cancellationToken)
    {
        Submissions.Add(submission);
        var made = Outcomes.Count > 0 ? Outcomes.Dequeue()(submission) : Synced(submission.References.Count > 0 ? 0 : null);
        return Task.FromResult(Keep(made));
    }

    public Task<RetimerJob> SubmitOcrAsync(string fileName, byte[] content, string? language, CancellationToken cancellationToken)
    {
        OcrRequests.Add((fileName, content, language));
        return Task.FromResult(Keep(new RetimerJob { Status = "finished", Outcome = OcrResult is null ? "failed" : "transcribed", CostUsd = 0.005, ErrorMessage = OcrResult is null ? "no pictures" : null }));
    }

    public Task<RetimerJob> GetJobAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_jobs[id]);

    public Task<byte[]> GetResultAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs[id].Outcome == "transcribed" ? OcrResult! : Result);

    public Task DeleteJobAsync(string id, CancellationToken cancellationToken)
    {
        Deleted.Add(id);
        return Task.CompletedTask;
    }

    private RetimerJob Keep(RetimerJob job)
    {
        var id = "job" + (++_next);
        var stored = new RetimerJob
        {
            Id = id,
            Status = job.Status,
            Stage = job.Stage,
            Outcome = job.Outcome,
            ErrorCode = job.ErrorCode,
            ErrorMessage = job.ErrorMessage,
            CostUsd = job.CostUsd,
            Confidence = job.Confidence,
            Warnings = job.Warnings,
            Fit = job.Fit,
            ShiftsMs = job.ShiftsMs,
            HasReferenceReport = job.HasReferenceReport,
            ChosenReference = job.ChosenReference,
            ReferenceReason = job.ReferenceReason,
            Candidates = job.Candidates
        };
        _jobs[id] = stored;
        return stored;
    }
}

internal sealed class FakeCallers : ICallerResolver
{
    public Caller? Caller { get; set; }

    public Task<Caller?> ResolveAsync(HttpContext context) => Task.FromResult(Caller);
}

internal static class Subtitles
{
    public const string Japanese =
        "1\n00:00:01,000 --> 00:00:03,000\nこんにちは、今日はいい天気ですね。\n\n"
        + "2\n00:00:04,000 --> 00:00:06,000\nそうですね。散歩に行きましょうか。\n\n"
        + "3\n00:00:07,000 --> 00:00:09,000\nはい、公園まで歩いていきましょう。\n\n"
        + "4\n00:00:10,000 --> 00:00:12,000\nあそこに猫がいますよ。かわいいですね。\n";

    public const string English =
        "1\n00:00:01,000 --> 00:00:03,000\nHello, the weather is lovely today, is it not?\n\n"
        + "2\n00:00:04,000 --> 00:00:06,000\nIt is. Shall we go for a walk together?\n\n"
        + "3\n00:00:07,000 --> 00:00:09,000\nYes, let us walk as far as the park.\n";

    public static ReferenceUpload Track(string language, string? title = null, bool isDefault = false, bool forced = false, bool hearingImpaired = false) =>
        new("track.srt", Encoding.UTF8.GetBytes(English), language, title, isDefault, forced, hearingImpaired);

    /// <summary>Japanese lines with a Chinese line under each, as a file with two languages has them.</summary>
    public const string JapaneseWithChinese =
        "1\n00:00:01,000 --> 00:00:03,000\nこんにちは、今日はいい天気ですね。\n你好，今天天气真好。\n\n"
        + "2\n00:00:04,000 --> 00:00:06,000\nそうですね。散歩に行きましょうか。\n是啊，我们去散步吧。\n\n"
        + "3\n00:00:07,000 --> 00:00:09,000\nはい、公園まで歩いていきましょう。\n好的，我们走到公园去吧。\n";
}

/// <summary>
/// Jellyfin's schedule for the check of the watched shows, as a test sets it.
/// </summary>
internal sealed class FakeSchedule : IWatchSchedule
{
    public TimeSpan? Interval { get; set; } = TimeSpan.FromHours(3);

    public bool Scheduled { get; set; } = true;

    public int Runs { get; private set; }

    public void RunNow() => Runs++;
}

/// <summary>
/// The plugin's services wired to fakes, with a store in a folder of its own.
/// </summary>
internal sealed class World : IDisposable
{
    public World()
    {
        Directory = Path.Combine(Path.GetTempPath(), "retimer-tests-" + Guid.NewGuid().ToString("N"));
        Store = new PluginStore(Directory, NullLogger.Instance);
        Sources = new SubtitleSources([Source]);
        Picker = new ModelPicker(Model);
        Finder = new FileFinder(Sources, Picker, Store, Settings, Library, NullLogger<FileFinder>.Instance);
        Runner = new JobRunner(Finder, Sources, Picker, Retimer, Library, Store, Settings, NullLogger<JobRunner>.Instance, TimeSpan.FromMilliseconds(1));
        Queue = new JobQueue(Runner, Store, Settings, NullLogger<JobQueue>.Instance);
        Watch = new WatchService(Settings, Library, Sources, Finder, Queue, Store, Picker, Schedule, NullLogger<WatchService>.Instance, () => Now ?? DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50));
        Page = new PageService(Settings, Library, Sources, Finder, Queue, Store, Picker, Watch);
    }

    /// <summary>Gets or sets the time watch mode sees, or <c>null</c> for the real time.</summary>
    public DateTimeOffset? Now { get; set; }

    public FakeSchedule Schedule { get; } = new();

    public WatchService Watch { get; }

    public string Directory { get; }

    public StubSettings Settings { get; } = new();

    public FakeLibrary Library { get; } = new();

    public FakeSource Source { get; } = new();

    public FakeModel Model { get; } = new FakeModel().MapsByNumber().Language("ja").ChoosesEntry("384");

    public FakeRetimerApi Retimer { get; } = new();

    public PluginStore Store { get; }

    public SubtitleSources Sources { get; }

    public ModelPicker Picker { get; }

    public FileFinder Finder { get; }

    public JobRunner Runner { get; }

    public JobQueue Queue { get; }

    public PageService Page { get; }

    public Caller Admin { get; } = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "admin", true, true);

    public Caller Viewer { get; } = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "viewer", false, false);

    public Caller Manager { get; } = new(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "manager", false, true);

    /// <summary>Fate/Zero with one entry at the source and a Japanese file for each of the given episodes.</summary>
    public IReadOnlyList<VideoInfo> FateZero(int episodes = 3)
    {
        Settings.Current.OpenRouterApiKey = "or-key";
        Settings.Current.JimakuApiKey = "jimaku-key";
        Source.AddEntry("384", "Fate/Zero", 10087);
        var showId = Guid.Parse("f0f0f0f0-0000-0000-0000-000000000001");
        var videos = new List<VideoInfo>();
        for (var i = 1; i <= episodes; i++)
        {
            videos.Add(Library.AddEpisode(showId, "Fate/Zero", 1, i, "Episode " + i));
            Source.AddFile($"[Group] Fate Zero - {i:00} [BD].ja.srt", "384");
        }

        return videos;
    }

    public void RetimerOn(string audio = PluginConfiguration.AudioNever)
    {
        Settings.Current.RetimerEnabled = true;
        Settings.Current.RetimerUrl = "https://retimer.example.org";
        Settings.Current.RetimerApiKey = "rt-key";
        Settings.Current.SendAudio = audio;
    }

    public async Task<Job> RunAsync(Job job, CancellationToken cancellationToken = default)
    {
        await Runner.RunAsync(job, () => { }, cancellationToken).ConfigureAwait(false);
        return job;
    }

    public Job JobFor(VideoInfo video, string kind = JobKind.Get) => new()
    {
        ItemId = video.Id,
        ShowId = video.ShowId,
        ShowName = video.ShowName,
        ItemLabel = PageService.Label(video),
        Language = "ja",
        Kind = kind,
        StartedBy = "admin",
        StartedById = Admin.UserId
    };

    public void Dispose()
    {
        Watch.Dispose();
        Queue.Dispose();
        Store.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover test folder in the temp directory does no harm.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
