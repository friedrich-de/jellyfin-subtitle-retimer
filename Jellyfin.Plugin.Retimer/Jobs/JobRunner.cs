using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Jobs;

/// <summary>
/// A job that ends without a subtitle for a reason the user should read, not a bug.
/// </summary>
internal sealed class JobFailedException : Exception
{
    public JobFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Does the work of one job: search, pick, download, check the language, retime when Retimer API is on, save.
/// </summary>
internal sealed class JobRunner
{
    /// <summary>How many files are tried for one video before the job gives up.</summary>
    internal const int MaxAttempts = 3;

    internal const string StepSearch = "Searching the sources";
    internal const string StepChoose = "Choosing a file";
    internal const string StepDownload = "Downloading";
    internal const string StepLanguage = "Checking the language";
    internal const string StepTracks = "Reading the video's tracks";
    internal const string StepRetime = "Retiming";
    internal const string StepSave = "Saving";

    private readonly FileFinder _finder;
    private readonly SubtitleSources _sources;
    private readonly ModelPicker _model;
    private readonly IRetimerApi _retimer;
    private readonly IVideoLibrary _library;
    private readonly PluginStore _store;
    private readonly IPluginSettings _settings;
    private readonly ILogger<JobRunner> _logger;
    private readonly TimeSpan _pollInterval;

    public JobRunner(
        FileFinder finder,
        SubtitleSources sources,
        ModelPicker model,
        IRetimerApi retimer,
        IVideoLibrary library,
        PluginStore store,
        IPluginSettings settings,
        ILogger<JobRunner> logger,
        TimeSpan? pollInterval = null)
    {
        _finder = finder;
        _sources = sources;
        _model = model;
        _retimer = retimer;
        _library = library;
        _store = store;
        _settings = settings;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    private enum RetimeKind
    {
        Retimed,
        NoReference,
        OtherContent
    }

    /// <summary>
    /// Whether Retimer API is switched on and has an address and a key.
    /// </summary>
    public static bool RetimerReady(PluginConfiguration configuration) =>
        configuration.RetimerEnabled && !string.IsNullOrWhiteSpace(configuration.RetimerUrl) && !string.IsNullOrWhiteSpace(configuration.RetimerApiKey);

    /// <summary>
    /// Runs the job to its end and sets its final state. Only a cancelled job throws.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="changed">Called whenever the job changed, so the page can show it.</param>
    /// <param name="cancellationToken">Cancels the job.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(Job job, Action changed, CancellationToken cancellationToken)
    {
        var progress = new Progress(job, changed);
        var scratch = Path.Combine(Path.GetTempPath(), "subtitle-retimer-" + job.Id);
        try
        {
            await RunCoreAsync(job, progress, scratch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JobFailedException ex)
        {
            Fail(job, ex.Message);
        }
        catch (Exception ex) when (ex is SourceException or RetimerException or ModelException)
        {
            Fail(job, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            _logger.LogError(ex, "The job for {Show} {Item} failed", job.ShowName, job.ItemLabel);
            Fail(job, "It failed on the server: " + (ex.InnerException?.Message ?? ex.Message));
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "The job's temporary folder {Path} could not be removed", scratch);
            }
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(Path.GetFileName(name.Replace('\\', '/')).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length > 150)
        {
            cleaned = cleaned[..(150 - Path.GetExtension(cleaned).Length)] + Path.GetExtension(cleaned);
        }

        return cleaned.Length == 0 ? "subtitle.srt" : cleaned;
    }

    private static string Describe(ReferenceUpload track)
    {
        var language = Languages.Find(track.Language)?.Name;
        var title = string.IsNullOrWhiteSpace(track.Title) ? null : "“" + track.Title.Trim() + "”";
        return (language, title) switch
        {
            (not null, not null) => "the " + language + " track " + title,
            (not null, null) => "the " + language + " track",
            (null, not null) => "the track " + title,
            _ => "a subtitle track"
        };
    }

    private void Fail(Job job, string reason)
    {
        job.State = JobState.Failed;
        job.Detail = reason;
        _store.UpdateItem(job.ItemId, job.Language, item => item.LastError = reason);
    }

    private async Task RunCoreAsync(Job job, Progress progress, string scratch, CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var video = _library.GetVideo(Guid.Empty, job.ItemId) ?? throw new JobFailedException("The video is no longer in the library.");
        var language = job.Language;
        var languageName = Languages.NameOf(language);
        var retimerReady = RetimerReady(configuration);
        var retime = retimerReady && job.Kind != JobKind.GetPlain && (job.Kind != JobKind.Chosen || job.Retime);
        var record = _store.Item(job.ItemId, language);

        if (job.Kind == JobKind.Retime)
        {
            await RetimeKeptAsync(job, video, record, progress, scratch, cancellationToken).ConfigureAwait(false);
            return;
        }

        var steps = new List<string>();
        if (job.Kind != JobKind.Chosen)
        {
            steps.Add(StepSearch);
            steps.Add(StepChoose);
        }

        steps.Add(StepDownload);
        steps.Add(StepLanguage);
        if (retime)
        {
            steps.Add(StepTracks);
            steps.Add(StepRetime);
        }

        steps.Add(StepSave);
        progress.Plan(steps);

        // Which files to try, in order.
        var attempts = new List<Candidate>();
        string? pickedBy;
        if (job.Kind == JobKind.Chosen)
        {
            attempts.Add(new Candidate { File = await ResolveChosenAsync(job, video, cancellationToken).ConfigureAwait(false) });
            pickedBy = "you";
        }
        else
        {
            if (_sources.ReadyFor(language).Count == 0)
            {
                throw new JobFailedException("No subtitle source is set up for " + languageName + ".");
            }

            progress.Step(StepSearch);
            var found = await _finder.FindAsync(video, language, progress.Note, cancellationToken).ConfigureAwait(false);
            job.CostUsd += found.CostUsd;

            // A job that was cancelled while the sources or the model answered ends as cancelled, not as "no file fits".
            cancellationToken.ThrowIfCancellationRequested();
            progress.Step(StepChoose);

            if (found.NeedsChoice)
            {
                job.State = JobState.NoFit;
                job.Detail = "No model answered" + (found.ModelProblem is null ? "." : ": " + found.ModelProblem) + " Choose a file yourself.";
                _store.UpdateItem(job.ItemId, language, item => item.NoFit = true);
                return;
            }

            var skip = (record?.Tried ?? []).Select(t => t.Source + "\n" + t.FileId).ToHashSet(StringComparer.Ordinal);
            if (job.Kind == JobKind.Another && record is not null && record.SavedPath.Length > 0)
            {
                skip.Add(record.Source + "\n" + record.FileId);
            }

            attempts.AddRange(found.Candidates.Where(c => c.Auto && !skip.Contains(c.File.Source + "\n" + c.File.Id)).Take(MaxAttempts));
            pickedBy = found.Model ?? "the rules";

            if (attempts.Count == 0)
            {
                var problems = found.Sources.Where(s => s.Problem is not null).Select(s => s.Problem!).ToList();
                var listed = found.Sources.Sum(s => s.Files);
                job.State = JobState.NoFit;
                job.Detail = problems.Count > 0 && listed == 0
                    ? string.Join(" ", problems)
                    : (listed == 0
                        ? "The sources have no " + languageName + " file for this " + (video.IsMovie ? "film." : "episode.")
                        : (skip.Count > 0 ? "No other file fits. " : "No file fits. ") + listed.ToString(CultureInfo.InvariantCulture) + (listed == 1 ? " file was" : " files were") + " listed, none of them for this " + (video.IsMovie ? "film." : "episode."));
                _store.UpdateItem(job.ItemId, language, item =>
                {
                    item.NoFit = true;
                    item.LastError = null;
                });
                return;
            }
        }

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = attempt.File;
            var source = _sources.Find(file.Source) ?? throw new JobFailedException("The source of the chosen file is not set up.");
            var chosenByUser = job.Kind == JobKind.Chosen;

            void Reject(string reason)
            {
                var tried = new TriedFile { Source = file.Source, FileId = file.Id, Name = file.Name, Reason = reason, At = DateTimeOffset.UtcNow };
                job.Tried.Add(tried);
                _store.UpdateItem(job.ItemId, language, item =>
                {
                    item.Tried.RemoveAll(t => t.Source == tried.Source && t.FileId == tried.FileId);
                    item.Tried.Add(tried);
                });
            }

            // Download, and take the episode's file out of a pack.
            progress.Step(StepDownload, file.Name);
            DownloadedFile download;
            try
            {
                download = await source.DownloadAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceException ex) when (ex.Error is SourceError.NotFound or SourceError.TooLarge)
            {
                Reject(ex.Message);
                continue;
            }

            if (ArchiveReader.IsZip(download.Name, download.Content))
            {
                var names = ArchiveReader.ListSubtitles(download.Content);
                var inner = RulesPicker.PickFromArchive(FileFinder.ToQuery(video, language), names, _finder.Options());
                var unpacked = inner is null ? null : ArchiveReader.Read(download.Content, inner);
                if (unpacked is null)
                {
                    Reject(names.Count == 0 ? "the pack holds no text subtitle" : "the pack holds no file for this " + (video.IsMovie ? "film" : "episode"));
                    continue;
                }

                download = unpacked;
            }

            var guess = FileNameParser.Parse(download.Name);
            if (guess.IsPicture)
            {
                if (!retimerReady || !configuration.ConvertPictureSubtitles)
                {
                    Reject("it is a picture subtitle, and converting those is switched off");
                    continue;
                }

                progress.Note("Retimer API is reading the picture subtitle");
                var (text, ocrCost) = await ReadPicturesAsync(download.Name, download.Content, language, cancellationToken).ConfigureAwait(false);
                job.CostUsd += ocrCost;
                if (text is null)
                {
                    Reject("Retimer API could not read the picture subtitle");
                    continue;
                }

                download = new DownloadedFile(Path.GetFileNameWithoutExtension(SafeFileName(file.Name).Replace(".sup", string.Empty, StringComparison.OrdinalIgnoreCase)) + ".srt", text);
            }
            else if (!SubtitleText.IsTextSubtitle(download.Name))
            {
                Reject("it is not a subtitle the plugin can use");
                continue;
            }

            // The language: the letters first, then the model.
            progress.Step(StepLanguage);
            var lines = SubtitleText.Lines(download.Name, SubtitleText.Decode(download.Content));
            string? languageWarning = null;
            if (lines.Count == 0)
            {
                Reject("it holds no subtitle lines");
                continue;
            }

            var script = ScriptCheck.Check(lines, language);
            if (!script.Fits)
            {
                languageWarning = "the text is " + script.Seen + ", not " + languageName;
            }
            else if (_model.Model is { } checker)
            {
                progress.Note(checker.Name + " is reading the file");
                try
                {
                    var verdict = await _model.CheckLanguageAsync(SubtitleText.Sample(lines), language, cancellationToken).ConfigureAwait(false);
                    job.CostUsd += verdict.CostUsd;
                    if (!verdict.Fits)
                    {
                        languageWarning = "the text is " + verdict.Seen + ", not " + languageName + " (" + checker.Name + ")";
                    }
                }
                catch (ModelException ex)
                {
                    // The letters fit, and that check stands when the model gives no answer.
                    _logger.LogWarning("The model could not check the language: {Problem}", ex.Message);
                }
            }

            if (languageWarning is not null && !chosenByUser)
            {
                Reject(languageWarning);
                continue;
            }

            // Retime, or keep as downloaded.
            var content = download.Content;
            var name = SafeFileName(download.Name);
            var retimed = false;
            string? resultNote = null;

            if (retime && !SubtitleText.IsRetimable(name))
            {
                resultNote = "Not retimed: Retimer API takes SRT and ASS files, and this one is " + Path.GetExtension(name).TrimStart('.').ToUpperInvariant() + ".";
            }
            else if (retime)
            {
                var outcome = await RetimeAsync(job, video, name, content, language, progress, scratch, cancellationToken).ConfigureAwait(false);
                if (outcome.Kind == RetimeKind.OtherContent)
                {
                    Reject("it belongs to other content");
                    if (chosenByUser)
                    {
                        job.State = JobState.NoFit;
                        job.Detail = "Retimer API found that the chosen file belongs to other content. " + outcome.Detail;
                        _store.UpdateItem(job.ItemId, language, item => item.NoFit = item.SavedPath.Length == 0);
                        return;
                    }

                    continue;
                }

                if (outcome.Kind == RetimeKind.Retimed)
                {
                    retimed = true;
                    content = outcome.Result!;
                    name = Path.ChangeExtension(name, outcome.Format);
                }

                resultNote = outcome.Detail;
            }
            else if (job.Kind == JobKind.GetPlain || (chosenByUser && !job.Retime))
            {
                resultNote = null;
            }
            else if (!configuration.RetimerEnabled)
            {
                resultNote = null;
            }

            if (languageWarning is not null)
            {
                resultNote = (resultNote is null ? string.Empty : resultNote + " ") + "Mind: " + languageWarning + ".";
            }

            progress.Step(StepSave);
            await SaveAsync(job, video, file, download, name, content, retimed, resultNote, chosenByUser ? "you" : (attempt.SuggestedBy ?? pickedBy), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Every file was rejected.
        job.State = JobState.NoFit;
        job.Detail = job.Tried.Count.ToString(CultureInfo.InvariantCulture)
            + (job.Tried.Count == 1 ? " file tried: " : " files tried: ")
            + string.Join("; ", job.Tried.Select(t => t.Reason).Distinct(StringComparer.Ordinal))
            + ".";
        _store.UpdateItem(job.ItemId, language, item =>
        {
            item.NoFit = item.SavedPath.Length == 0;
            item.LastError = null;
        });
    }

    private async Task<SourceFile> ResolveChosenAsync(Job job, VideoInfo video, CancellationToken cancellationToken)
    {
        var source = _sources.Find(job.ChosenSource);
        if (source is null || !source.IsReady || string.IsNullOrEmpty(job.ChosenFileId))
        {
            throw new JobFailedException("The source of the chosen file is not set up.");
        }

        // The file is looked up again at its source, so nothing the browser sent is fetched as it is.
        var files = await _finder.ListFilesAsync(source, FileFinder.ToQuery(video, job.Language), source.HasEntries ? job.ChosenEntryId : null, cancellationToken).ConfigureAwait(false);
        return files.FirstOrDefault(f => f.Id == job.ChosenFileId)
            ?? throw new JobFailedException(source.Name + " no longer lists the chosen file.");
    }

    private async Task SaveAsync(
        Job job,
        VideoInfo video,
        SourceFile file,
        DownloadedFile download,
        string name,
        byte[] content,
        bool retimed,
        string? note,
        string? pickedBy,
        CancellationToken cancellationToken)
    {
        var language = job.Language;
        var previous = _store.Item(job.ItemId, language);

        // The downloaded file is kept, so a subtitle saved as it came can be retimed later without the source.
        string? kept = null;
        try
        {
            var folder = Path.Combine(_store.DownloadsDirectory, PluginStore.ItemKey(job.ItemId, language).Replace(':', '-'));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
            kept = SafeFileName(download.Name);
            await File.WriteAllBytesAsync(Path.Combine(folder, kept), download.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The download for {Show} {Item} could not be kept", job.ShowName, job.ItemLabel);
            kept = null;
        }

        // One subtitle from the plugin per video and language: the earlier one makes room.
        if (previous is not null && previous.SavedPath.Length > 0)
        {
            await _library.RemoveSubtitleAsync(job.ItemId, previous.SavedPath, cancellationToken).ConfigureAwait(false);
        }

        var format = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var saved = await _library.SaveSubtitleAsync(job.ItemId, language, format, content, cancellationToken).ConfigureAwait(false);
        if (!saved.Listed)
        {
            note = (note is null ? string.Empty : note + " ") + "Jellyfin has not listed the new subtitle yet. It appears after the next scan of the library.";
        }

        _store.UpdateItem(job.ItemId, language, item =>
        {
            item.SavedPath = saved.Path ?? string.Empty;
            item.Source = file.Source;
            item.FileName = file.Name;
            item.FileId = file.Id;
            item.EntryId = file.EntryId;
            item.Retimed = retimed;
            item.Note = note;
            item.PickedBy = pickedBy;
            item.SavedAt = DateTimeOffset.UtcNow;
            item.SavedBy = job.StartedBy;
            item.KeptDownload = kept;
            item.NoFit = false;
            item.LastError = null;
        });

        job.State = retimed ? JobState.Saved : JobState.SavedRaw;
        job.Detail = note;
        _logger.LogInformation(
            "Saved a {Language} subtitle for {Show} {Item} from {Source} ({File}), retimed: {Retimed}",
            language,
            video.ShowName,
            job.ItemLabel,
            file.Source,
            file.Name,
            retimed);
    }

    private async Task RetimeKeptAsync(Job job, VideoInfo video, ItemRecord? record, Progress progress, string scratch, CancellationToken cancellationToken)
    {
        if (!RetimerReady(_settings.Current))
        {
            throw new JobFailedException("Retimer API is switched off or not set up.");
        }

        if (record is null || record.SavedPath.Length == 0 || string.IsNullOrEmpty(record.KeptDownload))
        {
            throw new JobFailedException("The plugin has no downloaded file for this subtitle any more. Get the subtitle again.");
        }

        var path = Path.Combine(_store.DownloadsDirectory, PluginStore.ItemKey(job.ItemId, job.Language).Replace(':', '-'), record.KeptDownload);
        if (!File.Exists(path))
        {
            throw new JobFailedException("The plugin has no downloaded file for this subtitle any more. Get the subtitle again.");
        }

        var name = record.KeptDownload;
        if (!SubtitleText.IsRetimable(name))
        {
            throw new JobFailedException("Retimer API takes SRT and ASS files, and this one is " + Path.GetExtension(name).TrimStart('.').ToUpperInvariant() + ".");
        }

        progress.Plan([StepTracks, StepRetime, StepSave]);
        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var outcome = await RetimeAsync(job, video, name, content, job.Language, progress, scratch, cancellationToken).ConfigureAwait(false);
        if (outcome.Kind != RetimeKind.Retimed)
        {
            throw new JobFailedException(outcome.Kind == RetimeKind.OtherContent
                ? ("Retimer API found that this subtitle belongs to other content. " + outcome.Detail).TrimEnd()
                : outcome.Detail);
        }

        progress.Step(StepSave);
        var file = new SourceFile { Source = record.Source, Id = record.FileId, EntryId = record.EntryId, Name = record.FileName };
        await SaveAsync(job, video, file, new DownloadedFile(name, content), Path.ChangeExtension(name, outcome.Format), outcome.Result!, true, outcome.Detail, record.PickedBy, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(byte[]? Text, double Cost)> ReadPicturesAsync(string fileName, byte[] content, string? language, CancellationToken cancellationToken)
    {
        var features = await _retimer.GetFeaturesAsync(cancellationToken).ConfigureAwait(false);
        if (!features.Ocr || content.Length > features.ImageSubtitleBytes)
        {
            return (null, 0);
        }

        var submitted = await _retimer.SubmitOcrAsync(fileName, content, Languages.Normalize(language), cancellationToken).ConfigureAwait(false);
        var finished = await WaitAsync(submitted, null, cancellationToken).ConfigureAwait(false);
        try
        {
            if (finished.Outcome != "transcribed")
            {
                _logger.LogWarning("Retimer API could not read a picture subtitle: {Code} {Message}", finished.ErrorCode, finished.ErrorMessage);
                return (null, finished.CostUsd);
            }

            return (await _retimer.GetResultAsync(finished.Id, cancellationToken).ConfigureAwait(false), finished.CostUsd);
        }
        finally
        {
            await _retimer.DeleteJobAsync(finished.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<RetimerJob> WaitAsync(RetimerJob submitted, Progress? progress, CancellationToken cancellationToken)
    {
        var job = submitted;
        var started = DateTimeOffset.UtcNow;
        var failures = 0;
        while (!job.IsFinished)
        {
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(30))
            {
                throw new JobFailedException("Retimer API did not finish within 30 minutes.");
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            try
            {
                job = await _retimer.GetJobAsync(submitted.Id, cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (RetimerException ex) when (ex.Error == RetimerError.Unavailable && ++failures < 5)
            {
                // A dropped connection while waiting is not the end of the job.
                continue;
            }

            if (progress is not null && !string.IsNullOrEmpty(job.Stage))
            {
                progress.Note("Retimer API: " + job.Stage);
            }
        }

        return job;
    }

    private async Task<RetimeOutcome> RetimeAsync(
        Job job,
        VideoInfo video,
        string targetName,
        byte[] target,
        string language,
        Progress progress,
        string scratch,
        CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var features = await _retimer.GetFeaturesAsync(cancellationToken).ConfigureAwait(false);

        // What the subtitle is compared with: the video's own text tracks.
        progress.Step(StepTracks);
        var mine = _store.Read(data => data.Items
            .Where(pair => pair.Key.StartsWith(job.ItemId.ToString("N"), StringComparison.Ordinal) && pair.Value.SavedPath.Length > 0)
            .Select(pair => pair.Value.SavedPath)
            .ToList());
        var tracks = (await _library.ReadTextTracksAsync(job.ItemId, 6, mine, scratch, cancellationToken).ConfigureAwait(false)).ToList();

        if (tracks.Count == 0 && configuration.ConvertPictureSubtitles && features.Ocr && video.IsLocalFile)
        {
            var picture = await _library.ReadPictureTrackAsync(job.ItemId, scratch, cancellationToken).ConfigureAwait(false);
            if (picture is not null)
            {
                progress.Note("Retimer API is reading the video's picture subtitles");
                var (text, ocrCost) = await ReadPicturesAsync(picture.FileName, picture.Content, picture.Language, cancellationToken).ConfigureAwait(false);
                job.CostUsd += ocrCost;
                if (text is not null)
                {
                    tracks.Add(picture with { FileName = "track.srt", Content = text });
                }
            }
        }

        if (tracks.Count > features.MaxReferenceSubtitles)
        {
            // An older Retimer API takes one track: the default one that is not forced, else the first.
            tracks = features.MaxReferenceSubtitles <= 1
                ? [tracks.FirstOrDefault(t => t.IsDefault && !t.IsForced) ?? tracks.FirstOrDefault(t => !t.IsForced) ?? tracks[0]]
                : tracks.Take(features.MaxReferenceSubtitles).ToList();
        }

        var mode = configuration.SendAudio;
        string? audioPath = null;
        string? audioProblem = null;

        async Task<bool> PrepareAudioAsync()
        {
            if (audioPath is not null)
            {
                return true;
            }

            if (!video.IsLocalFile)
            {
                audioProblem = "The video is not a file on this server, so its audio cannot be cut out.";
                return false;
            }

            if (video.RunTimeTicks is { } ticks && TimeSpan.FromTicks(ticks).TotalMinutes > features.AudioMinutes)
            {
                audioProblem = "The video is longer than Retimer API takes audio for.";
                return false;
            }

            progress.Note("Cutting out the audio");
            var path = Path.Combine(scratch, "audio.ogg");
            if (!await _library.ExtractAudioAsync(job.ItemId, language, path, cancellationToken).ConfigureAwait(false))
            {
                audioProblem = "The audio could not be cut out of the video.";
                return false;
            }

            if (new FileInfo(path).Length > features.AudioBytes)
            {
                audioProblem = "The audio is larger than Retimer API accepts.";
                return false;
            }

            audioPath = path;
            return true;
        }

        var withAudio = mode == PluginConfiguration.AudioAlways || (mode == PluginConfiguration.AudioFallback && tracks.Count == 0);
        if (withAudio && !await PrepareAudioAsync().ConfigureAwait(false))
        {
            withAudio = false;
        }

        if (tracks.Count == 0 && !withAudio)
        {
            // A video with picture subtitles only: say that they could be used.
            var pictures = !configuration.ConvertPictureSubtitles && video.Subtitles.Any(track => !track.IsText && !track.IsExternal)
                ? " Its picture subtitles can be used once “Convert picture subtitles with Retimer API” is switched on in the settings."
                : string.Empty;
            return new RetimeOutcome(RetimeKind.NoReference, null, string.Empty, mode == PluginConfiguration.AudioNever
                ? "Not retimed: the video has no text subtitle track, and sending the audio is switched off." + pictures
                : "Not retimed: the video has no text subtitle track. " + audioProblem + pictures);
        }

        progress.Step(StepRetime);
        var first = await SubmitAsync(job, targetName, target, tracks, withAudio ? audioPath : null, language, features, progress, cancellationToken).ConfigureAwait(false);
        var outcome = Judge(first, tracks, withAudio);
        if (outcome.Kind != RetimeKind.Retimed && outcome.TracksDoNotFit && !withAudio && mode == PluginConfiguration.AudioFallback)
        {
            // No track of the video fits. The audio settles whether the tracks or the subtitle are the odd ones.
            if (await PrepareAudioAsync().ConfigureAwait(false))
            {
                progress.Step(StepRetime, "No subtitle track fits, trying the audio");
                var second = await SubmitAsync(job, targetName, target, [], audioPath, language, features, progress, cancellationToken).ConfigureAwait(false);
                outcome = Judge(second, [], true);
                first = second;
            }
            else
            {
                return new RetimeOutcome(RetimeKind.NoReference, null, string.Empty, "Not retimed: no subtitle track of the video fits. " + audioProblem);
            }
        }

        if (outcome.Kind != RetimeKind.Retimed)
        {
            await _retimer.DeleteJobAsync(first.Id, CancellationToken.None).ConfigureAwait(false);
            return new RetimeOutcome(outcome.Kind, null, string.Empty, outcome.Detail);
        }

        try
        {
            var result = await _retimer.GetResultAsync(first.Id, cancellationToken).ConfigureAwait(false);
            var format = configuration.OutputFormat is "srt" or "ass" ? "." + configuration.OutputFormat : Path.GetExtension(targetName).ToLowerInvariant();
            return new RetimeOutcome(RetimeKind.Retimed, result, format, outcome.Detail);
        }
        finally
        {
            await _retimer.DeleteJobAsync(first.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<RetimerJob> SubmitAsync(
        Job job,
        string targetName,
        byte[] target,
        List<ReferenceUpload> tracks,
        string? audioPath,
        string language,
        RetimerFeatures features,
        Progress progress,
        CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var options = new JsonObject
        {
            ["remove_hearing_impaired"] = configuration.RemoveHearingImpaired,
            ["remove_karaoke"] = configuration.RemoveSongs,
            ["strip_formatting"] = configuration.RemoveStyling,
            ["remove_symbols"] = configuration.RemoveSymbols
        };
        if (features.RemoveSignsAndCredits && configuration.RemoveSignsAndCredits)
        {
            options["remove_signs_and_credits"] = true;
        }

        if (configuration.OutputFormat is "srt" or "ass")
        {
            options["output_format"] = configuration.OutputFormat;
        }

        if (audioPath is not null)
        {
            options["audio_language"] = language;
        }

        if (features.ReferenceTracks && tracks.Count > 0)
        {
            var labels = new JsonArray();
            foreach (var track in tracks)
            {
                labels.Add(new JsonObject
                {
                    ["language"] = string.IsNullOrWhiteSpace(track.Language) ? "und" : track.Language,
                    ["title"] = track.Title ?? string.Empty,
                    ["default"] = track.IsDefault,
                    ["forced"] = track.IsForced
                });
            }

            options["reference_tracks"] = labels;
        }

        var submitted = await _retimer.SubmitAsync(
            new RetimerSubmission { TargetName = targetName, Target = target, References = tracks, AudioPath = audioPath, Options = options },
            cancellationToken).ConfigureAwait(false);
        var finished = await WaitAsync(submitted, progress, cancellationToken).ConfigureAwait(false);
        job.CostUsd += finished.CostUsd;
        if (finished.HasReferenceReport)
        {
            // The server's log keeps what Retimer API made of each track, since the job itself is removed there.
            _logger.LogInformation(
                "Retimer API on {Show} {Item}: outcome {Outcome}, chose track {Chosen} of {Count}. {Verdicts}",
                job.ShowName,
                job.ItemLabel,
                finished.Outcome,
                finished.ChosenReference is { } chosen ? (chosen + 1).ToString(CultureInfo.InvariantCulture) : "none",
                tracks.Count,
                string.Join("; ", finished.Candidates.Select(c => string.Create(CultureInfo.InvariantCulture, $"track {c.Index + 1} ({c.Language ?? "?"}, {c.Title ?? "no title"}): {c.Verdict}"))));
        }
        if (finished.Outcome == "failed" || finished.Outcome is null)
        {
            await _retimer.DeleteJobAsync(finished.Id, CancellationToken.None).ConfigureAwait(false);
            throw new JobFailedException("Retimer API could not finish the job" + (string.IsNullOrWhiteSpace(finished.ErrorMessage) ? "." : ": " + finished.ErrorMessage));
        }

        return finished;
    }

    private static (RetimeKind Kind, string Detail, bool TracksDoNotFit) Judge(RetimerJob finished, List<ReferenceUpload> tracks, bool withAudio)
    {
        var noTrackChosen = tracks.Count > 0 && finished.HasReferenceReport && finished.ChosenReference is null;

        // Retimer API says the tracks themselves are of no use when every one has too little dialogue or cannot be read.
        var tracksAreTheProblem = noTrackChosen
            && finished.Candidates.Count > 0
            && finished.Candidates.All(c => c.Verdict is "too_little_dialogue" or "unreadable");

        string Against()
        {
            if (tracks.Count > 0 && finished.ChosenReference is { } index && index >= 0 && index < tracks.Count)
            {
                return "Retimed against " + Describe(tracks[index]) + " of the video" + (withAudio ? ", checked against the audio." : ".");
            }

            if (withAudio)
            {
                return tracks.Count > 0 ? "Retimed against the audio, since no subtitle track of the video fits." : "Retimed against the audio.";
            }

            return tracks.Count == 1 ? "Retimed against " + Describe(tracks[0]) + " of the video." : "Retimed against a subtitle track of the video.";
        }

        switch (finished.Outcome)
        {
            case "synced":
                return (RetimeKind.Retimed, Against() + Moved(finished), false);
            case "already_in_sync":
                return (RetimeKind.Retimed, "The timing was already right. " + Against().Replace("Retimed against", "Checked against", StringComparison.Ordinal), false);
            case "partial" when noTrackChosen && !withAudio:
                return (RetimeKind.NoReference, "Not retimed: the video's subtitle tracks hold too little dialogue to compare with, and the audio was not sent.", true);
            case "partial":
                return (RetimeKind.Retimed, Against() + Moved(finished) + " Some parts could not be placed with confidence.", false);
            case "rejected_no_match" when tracksAreTheProblem && !withAudio:
                return (RetimeKind.NoReference, "Not retimed: the video's subtitle tracks hold too little dialogue to compare with, and the audio was not sent.", true);
            case "rejected_no_match":
                return (RetimeKind.OtherContent, finished.ReferenceReason ?? string.Empty, noTrackChosen || tracks.Count > 0);
            default:
                throw new JobFailedException("Retimer API ended the job with an outcome the plugin does not know: " + finished.Outcome + ".");
        }
    }

    /// <summary>
    /// One sentence on how far the subtitle was moved, from what Retimer API says about its fit.
    /// It starts with a space, and is empty when Retimer API does not say.
    /// </summary>
    internal static string Moved(RetimerJob finished)
    {
        var shifts = finished.ShiftsMs;
        if (shifts.Count == 0)
        {
            return string.Empty;
        }

        static string Seconds(int ms) => (Math.Abs(ms) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        var rate = finished.Fit is "rate" or "rate_blocks";
        if (shifts.Distinct().Count() > 1)
        {
            var largest = shifts.MaxBy(Math.Abs);
            return string.Create(
                CultureInfo.InvariantCulture,
                $" {(rate ? "Adjusted for another frame rate and moved" : "Moved")} in {shifts.Count} parts, by up to {Seconds(largest)}.");
        }

        // After a change of rate the one shift says little by itself.
        if (rate)
        {
            return " Adjusted for another frame rate.";
        }

        var shift = shifts[0];
        return Math.Abs(shift) < 50
            ? " Moved by less than 0.1 s."
            : " Moved " + Seconds(shift) + (shift > 0 ? " later." : " earlier.");
    }

    private sealed record RetimeOutcome(RetimeKind Kind, byte[]? Result, string Format, string Detail);

    /// <summary>
    /// Writes the job's steps where the page reads them.
    /// </summary>
    private sealed class Progress
    {
        private readonly Job _job;
        private readonly Action _changed;
        private List<string> _steps = [];

        public Progress(Job job, Action changed)
        {
            _job = job;
            _changed = changed;
        }

        public void Plan(List<string> steps)
        {
            _steps = steps;
            _job.Steps = steps.Count;
            _changed();
        }

        public void Step(string text, string? note = null)
        {
            var index = _steps.IndexOf(text);
            _job.Step = index < 0 ? _job.Step : index + 1;
            _job.StepText = text;
            _job.StepNote = note;
            _changed();
        }

        public void Note(string? note)
        {
            _job.StepNote = note;
            _changed();
        }
    }
}
