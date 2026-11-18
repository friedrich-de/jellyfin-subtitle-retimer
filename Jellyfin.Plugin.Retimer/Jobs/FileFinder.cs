using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Jobs;

/// <summary>
/// What one source had for a video.
/// </summary>
/// <param name="Source">The source's id.</param>
/// <param name="Name">The source's name.</param>
/// <param name="EntryId">The entry that was used, for a source with entries.</param>
/// <param name="EntryName">That entry's name.</param>
/// <param name="Files">How many files the source listed.</param>
/// <param name="MatchedBy">Who matched the show to the entry: a model's name, "the rules" or "you".</param>
/// <param name="Problem">Why the source gave nothing, when it failed.</param>
internal sealed record SourceReport(string Source, string Name, string? EntryId, string? EntryName, int Files, string? MatchedBy, string? Problem);

/// <summary>
/// The files found for a video, best first, and how they were found.
/// </summary>
internal sealed record FindResult
{
    public IReadOnlyList<Candidate> Candidates { get; init; } = [];

    /// <summary>Gets the files the sources listed that are not among the candidates: other episodes by their names, and kinds the plugin cannot open.</summary>
    public IReadOnlyList<SourceFile> Others { get; init; } = [];

    public IReadOnlyList<SourceReport> Sources { get; init; } = [];

    public double CostUsd { get; init; }

    /// <summary>Gets the name of the model that ranked the files, or <c>null</c> when the rules did.</summary>
    public string? Model { get; init; }

    /// <summary>Gets why the model gave no answer, when it was asked and failed.</summary>
    public string? ModelProblem { get; init; }

    /// <summary>Gets a value indicating whether the search stopped because no model answered and the settings say to ask then.</summary>
    public bool NeedsChoice { get; init; }
}

/// <summary>
/// Searches the sources for a video and ranks what they have, with the model when one is set up and
/// with the rules otherwise. What the model decided for a show is kept, so a season costs one question.
/// </summary>
internal sealed class FileFinder
{
    /// <summary>How many kinds of rejected file are kept for a show.</summary>
    internal const int MaxRejectedKinds = 40;

    private static readonly TimeSpan _listLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _matchLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan _noMatchLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan _rejectedLifetime = TimeSpan.FromDays(30);

    private readonly SubtitleSources _sources;
    private readonly ModelPicker _model;
    private readonly PluginStore _store;
    private readonly IPluginSettings _settings;
    private readonly IVideoLibrary _library;
    private readonly ILogger<FileFinder> _logger;
    private readonly Lock _listLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<SourceFile> Files)> _lists = [];

    public FileFinder(SubtitleSources sources, ModelPicker model, PluginStore store, IPluginSettings settings, IVideoLibrary library, ILogger<FileFinder> logger)
    {
        _sources = sources;
        _model = model;
        _store = store;
        _settings = settings;
        _library = library;
        _logger = logger;
    }

    public static VideoQuery ToQuery(VideoInfo video, string language) => new()
    {
        Title = video.ShowName,
        OriginalTitle = video.ShowOriginalName,
        Year = video.Year,
        IsMovie = video.IsMovie,
        Season = video.Season,
        Episode = video.Episode,
        AbsoluteEpisode = video.AbsoluteEpisode,
        EpisodeTitle = video.IsMovie ? null : video.Name,
        VideoFileName = video.FileName,
        AnilistId = video.AnilistId,
        TmdbId = video.TmdbId,
        ImdbId = video.ImdbId,
        EpisodeImdbId = video.EpisodeImdbId,
        Language = language
    };

    public static string MatchKey(string source, VideoInfo video, string language) =>
        string.Create(CultureInfo.InvariantCulture, $"{source}|{(video.IsMovie ? "film" : "s" + (video.Season ?? 0))}|{language}");

    public PickOptions Options()
    {
        var configuration = _settings.Current;
        return new PickOptions
        {
            PreferredSource = configuration.PreferredSource,
            PicturesAllowed = configuration.ConvertPictureSubtitles && configuration.RetimerEnabled
        };
    }

    /// <summary>
    /// Finds and ranks the files for a video.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="language">The wanted subtitle language.</param>
    /// <param name="note">Told what is going on, for the page.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The files, best first.</returns>
    public async Task<FindResult> FindAsync(VideoInfo video, string language, Action<string?> note, CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var query = ToQuery(video, language);
        var options = Options();
        var model = _model.Model;
        var useModel = model is not null;
        string? modelProblem = null;
        var cost = 0.0;

        var reports = new List<SourceReport>();
        var candidates = new List<Candidate>();
        var all = new List<SourceFile>();

        foreach (var source in _sources.ReadyFor(language))
        {
            try
            {
                IReadOnlyList<SourceFile> files;
                string? entryId = null;
                string? entryName = null;
                string? matchedBy = null;

                if (source.HasEntries)
                {
                    var key = MatchKey(source.Id, video, language);
                    var match = _store.Show(video.ShowId)?.Matches.GetValueOrDefault(key);
                    var age = match is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - match.At;
                    var stale = match is null
                        || (match.By != "user" && age > _matchLifetime)
                        || (match.EntryId.Length == 0 && age > _noMatchLifetime)
                        || (match.By == "rules" && useModel && modelProblem is null);

                    if (stale)
                    {
                        var entries = await source.SearchEntriesAsync(query, null, cancellationToken).ConfigureAwait(false);
                        SourceEntry? chosen = null;
                        string by = "rules";
                        string? reason = null;

                        if (useModel && modelProblem is null)
                        {
                            note(model!.Name + " is looking for the show on " + source.Name);
                            try
                            {
                                var choice = await _model.ChooseEntryAsync(query, source, entries, cancellationToken).ConfigureAwait(false);
                                cost += choice.CostUsd;
                                chosen = choice.Entry;
                                reason = choice.Reason;
                                by = "model";
                            }
                            catch (ModelException ex)
                            {
                                modelProblem = ex.Message;
                                _logger.LogWarning("The model could not choose an entry on {Source}: {Problem}", source.Name, ex.Message);
                            }
                        }

                        if (by == "rules")
                        {
                            if (useModel && configuration.WithoutModel == "ask")
                            {
                                return new FindResult { Sources = reports, CostUsd = cost, ModelProblem = modelProblem, NeedsChoice = true };
                            }

                            chosen = RulesPicker.PickEntry(query, entries);
                        }

                        match = new EntryMatch
                        {
                            EntryId = chosen?.Id ?? string.Empty,
                            EntryName = chosen is null ? string.Empty : (chosen.JapaneseName ?? chosen.Name),
                            By = by,
                            Model = by == "model" ? model!.Name : null,
                            Reason = reason,
                            At = DateTimeOffset.UtcNow
                        };
                        var saved = match;
                        _store.UpdateShow(video.ShowId, show => show.Matches[key] = saved);
                        note(null);
                    }

                    matchedBy = match!.By switch { "model" => match.Model ?? "the model", "user" => "you", _ => "the rules" };
                    if (match.EntryId.Length == 0)
                    {
                        reports.Add(new SourceReport(source.Id, source.Name, null, null, 0, matchedBy, source.Name + " has no entry for this " + (video.IsMovie ? "film" : "show and season") + "."));
                        continue;
                    }

                    entryId = match.EntryId;
                    entryName = match.EntryName;
                    files = await ListFilesAsync(source, query, entryId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    files = await ListFilesAsync(source, query, null, cancellationToken).ConfigureAwait(false);
                }

                var rejected = RejectedKinds(video, source.Id, language);
                var ranked = RulesPicker.Rank(query, files, options)
                    .Select(candidate => rejected.TryGetValue(RulesPicker.NamePattern(candidate.File.Name), out var why)
                        ? candidate with { Score = candidate.Score - 45, RejectedBefore = why }
                        : candidate)
                    .OrderByDescending(candidate => candidate.Score)
                    .ToList();
                all.AddRange(files);
                reports.Add(new SourceReport(source.Id, source.Name, entryId, entryName, files.Count, matchedBy, null));

                // Only the files of an entry need a reader of names. A source that is asked for one episode answers for that episode.
                if (source.HasEntries && useModel && modelProblem is null && files.Count > 0)
                {
                    try
                    {
                        var (mapped, mapCost) = await MapAsync(source, video, query, entryId!, files, options, model!, rejected, note, cancellationToken).ConfigureAwait(false);
                        cost += mapCost;
                        var listed = mapped.Select(c => c.File.Id).ToHashSet(StringComparer.Ordinal);

                        // What the model left out stays in the list for the picker, but is not tried unasked.
                        candidates.AddRange(mapped);
                        candidates.AddRange(ranked.Where(c => !listed.Contains(c.File.Id)).Select(c => c with { Auto = false, Score = Math.Min(c.Score, 60) }));
                        continue;
                    }
                    catch (ModelException ex)
                    {
                        modelProblem = ex.Message;
                        _logger.LogWarning("The model could not rank the files of {Source}: {Problem}", source.Name, ex.Message);
                        if (configuration.WithoutModel == "ask")
                        {
                            return new FindResult { Sources = reports, CostUsd = cost, ModelProblem = modelProblem, NeedsChoice = true };
                        }
                    }
                }

                candidates.AddRange(ranked);
            }
            catch (SourceException ex)
            {
                _logger.LogWarning("{Source} could not be searched: {Problem}", source.Name, ex.Message);
                reports.Add(new SourceReport(source.Id, source.Name, null, null, 0, null, ex.Message));
            }
        }

        note(null);
        var shown = candidates.Select(c => c.File.Source + "\n" + c.File.Id).ToHashSet(StringComparer.Ordinal);
        return new FindResult
        {
            Candidates = candidates.OrderByDescending(c => c.Score).ToList(),
            Others = all.Where(file => !shown.Contains(file.Source + "\n" + file.Id)).ToList(),
            Sources = reports,
            CostUsd = cost,
            Model = useModel && modelProblem is null ? model!.Name : null,
            ModelProblem = modelProblem
        };
    }

    /// <summary>
    /// Searches the sources that keep entries for a title the user typed.
    /// </summary>
    public async Task<IReadOnlyList<SourceEntry>> SearchEntriesAsync(VideoInfo video, string language, string title, CancellationToken cancellationToken)
    {
        var found = new List<SourceEntry>();
        foreach (var source in _sources.ReadyFor(language).Where(s => s.HasEntries))
        {
            found.AddRange(await source.SearchEntriesAsync(ToQuery(video, language), title, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    /// <summary>
    /// Keeps the entry the user chose for a show's season, and forgets what was mapped and tried before.
    /// </summary>
    public void Pin(VideoInfo video, string language, SourceEntry entry, string userName)
    {
        var key = MatchKey(entry.Source, video, language);
        _store.UpdateShow(video.ShowId, show => show.Matches[key] = new EntryMatch
        {
            EntryId = entry.Id,
            EntryName = entry.JapaneseName ?? entry.Name,
            By = "user",
            Reason = "Chosen by " + userName,
            At = DateTimeOffset.UtcNow
        });
    }

    public async Task<IReadOnlyList<SourceFile>> ListFilesAsync(ISubtitleSource source, VideoQuery query, string? entryId, CancellationToken cancellationToken)
    {
        var key = source.HasEntries
            ? source.Id + "|" + entryId
            : string.Create(CultureInfo.InvariantCulture, $"{source.Id}|{query.Title}|{query.Year}|{query.Season}|{query.Episode}|{query.Language}|{query.ImdbId}|{query.TmdbId}");
        lock (_listLock)
        {
            if (_lists.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < _listLifetime)
            {
                return cached.Files;
            }
        }

        var files = await source.ListFilesAsync(query, entryId, cancellationToken).ConfigureAwait(false);
        lock (_listLock)
        {
            if (_lists.Count > 200)
            {
                _lists.Clear();
            }

            _lists[key] = (DateTimeOffset.UtcNow, files);
        }

        return files;
    }

    /// <summary>
    /// Remembers that a file was rejected for a reason its release's files for the other episodes share,
    /// so those come last from now on and are not suggested.
    /// </summary>
    public void RememberRejected(VideoInfo video, string language, SourceFile file, string reason)
    {
        var pattern = RulesPicker.NamePattern(file.Name);
        _store.UpdateShow(video.ShowId, show =>
        {
            // The list is replaced, never changed in place: a snapshot may still read the old one.
            var kept = show.Rejected.Where(kind => !(kind.Source == file.Source && kind.Language == language && kind.Pattern == pattern)).ToList();
            kept.Add(new RejectedKind { Source = file.Source, Language = language, Pattern = pattern, Reason = reason, At = DateTimeOffset.UtcNow });
            show.Rejected = kept.Count > MaxRejectedKinds ? kept.GetRange(kept.Count - MaxRejectedKinds, MaxRejectedKinds) : kept;
        });
    }

    /// <summary>
    /// Forgets what was held against a file's release, because a file of it was saved after all.
    /// </summary>
    public void ForgetRejected(VideoInfo video, string language, SourceFile file)
    {
        var pattern = RulesPicker.NamePattern(file.Name);
        _store.UpdateShow(video.ShowId, show =>
        {
            if (show.Rejected.Any(kind => kind.Source == file.Source && kind.Language == language && kind.Pattern == pattern))
            {
                show.Rejected = show.Rejected.Where(kind => !(kind.Source == file.Source && kind.Language == language && kind.Pattern == pattern)).ToList();
            }
        });
    }

    private static string Fingerprint(IReadOnlyList<SourceFile> files, IReadOnlyList<SeasonEpisode> episodes, string modelId)
    {
        var text = new StringBuilder(modelId).Append('\n');
        foreach (var file in files)
        {
            text.Append(file.Id).Append('\n');
        }

        foreach (var episode in episodes)
        {
            text.Append(CultureInfo.InvariantCulture, $"{episode.Number}/{episode.AbsoluteNumber}/{episode.VideoFileName}\n");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 12);
    }

    /// <summary>The kinds of file that were rejected for another episode of the show, by name pattern, with the reason.</summary>
    private Dictionary<string, string> RejectedKinds(VideoInfo video, string source, string language)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in _store.Show(video.ShowId)?.Rejected ?? [])
        {
            if (kind.Source == source && kind.Language == language && DateTimeOffset.UtcNow - kind.At < _rejectedLifetime)
            {
                kinds[kind.Pattern] = kind.Reason;
            }
        }

        return kinds;
    }

    private async Task<(List<Candidate> Mapped, double Cost)> MapAsync(
        ISubtitleSource source,
        VideoInfo video,
        VideoQuery query,
        string entryId,
        IReadOnlyList<SourceFile> files,
        PickOptions options,
        ModelEntry model,
        Dictionary<string, string> rejected,
        Action<string?> note,
        CancellationToken cancellationToken)
    {
        var episodes = _library.GetSeasonEpisodes(video.Id);
        var episodeNumber = video.IsMovie ? 1 : video.Episode;
        if (episodeNumber is null || episodes.Count == 0)
        {
            return ([], 0);
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{source.Id}|{entryId}|{(video.IsMovie ? "film" : "s" + (video.Season ?? 0))}|{query.Language}");
        var fingerprint = Fingerprint(files, episodes, model.Id);
        var mapping = _store.Show(video.ShowId)?.Mappings.GetValueOrDefault(key);
        var cost = 0.0;

        if (mapping is null || mapping.ListHash != fingerprint)
        {
            note(model.Name + " is reading the file names");
            var map = await _model.MapFilesAsync(query, episodes, files, cancellationToken).ConfigureAwait(false);
            cost = map.CostUsd;
            mapping = new FileMapping
            {
                ListHash = fingerprint,
                Model = model.Name,
                At = DateTimeOffset.UtcNow,
                Episodes = map.Episodes.ToDictionary(
                    pair => pair.Key,
                    pair => new MappedEpisode { Files = pair.Value.Files.Select(index => files[index].Id).ToList(), Reason = pair.Value.Reason })
            };
            var saved = mapping;
            _store.UpdateShow(video.ShowId, show => show.Mappings[key] = saved);
            note(null);
        }

        var mapped = new List<Candidate>();
        if (mapping.Episodes.TryGetValue(episodeNumber.Value, out var listed))
        {
            var byId = files.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var position = 0;
            foreach (var id in listed.Files)
            {
                if (!byId.TryGetValue(id, out var file) || !RulesPicker.IsUsable(file, options))
                {
                    continue;
                }

                // The model reads names, and what a name plainly says stands over its order: a plain file goes
                // before one with furigana or for the hearing impaired, those before one with a second language,
                // and a kind of file that was already rejected for another episode comes last.
                var notes = new List<string>();
                var step = 0;
                if (RulesPicker.HasFurigana(file))
                {
                    step = 1;
                    notes.Add(RulesPicker.FuriganaNote);
                }

                if (RulesPicker.IsHearingImpaired(file))
                {
                    step = 1;
                }

                if (RulesPicker.HasSecondLanguage(file, query.Language))
                {
                    step = 2;
                    notes.Add(RulesPicker.SecondLanguageNote);
                }

                var before = rejected.GetValueOrDefault(RulesPicker.NamePattern(file.Name));
                if (before is not null)
                {
                    step = 3;
                }

                if (position == 0 && step == 0 && !string.IsNullOrEmpty(listed.Reason))
                {
                    notes.Insert(0, listed.Reason);
                }

                mapped.Add(new Candidate
                {
                    File = file,
                    Score = 120 - (position * 8) - (step * 32),
                    Reason = string.Join(" · ", notes),
                    SuggestedBy = before is null ? mapping.Model : null,
                    Auto = true,
                    RejectedBefore = before
                });
                position++;
            }
        }

        return (mapped.OrderByDescending(candidate => candidate.Score).ToList(), cost);
    }
}
