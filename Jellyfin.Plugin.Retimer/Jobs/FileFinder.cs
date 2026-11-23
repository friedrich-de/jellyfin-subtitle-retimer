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
/// <param name="EntryId">The entry that was used, for a source with entries: the first when there are several.</param>
/// <param name="EntryName">That entry's name.</param>
/// <param name="Files">How many files the source listed.</param>
/// <param name="MatchedBy">Who matched the show to the entry: a model's name, "the rules" or "you".</param>
/// <param name="Problem">Why the source gave nothing, when it failed.</param>
/// <param name="EntryNames">The names of all entries that were used, in airing order, when the source splits the season over several.</param>
internal sealed record SourceReport(string Source, string Name, string? EntryId, string? EntryName, int Files, string? MatchedBy, string? Problem, IReadOnlyList<string>? EntryNames = null);

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

    /// <summary>Gets the name of the model that ranked the files, or <c>null</c> when it gave no answer.</summary>
    public string? Model { get; init; }

    /// <summary>Gets why the model gave no answer: none is set up, or it was asked and failed.</summary>
    public string? ModelProblem { get; init; }

    /// <summary>Gets a value indicating whether the search for a job stopped because the model gave no answer.</summary>
    public bool NeedsChoice { get; init; }
}

/// <summary>
/// Searches the sources for a video and has the model rank what they have. The rules read facts from
/// the names and never pick in the model's place. What the model decided for a show is kept, so a
/// season costs one question.
/// </summary>
internal sealed class FileFinder
{
    /// <summary>How many kinds of rejected file are kept for a show.</summary>
    internal const int MaxRejectedKinds = 40;

    /// <summary>What a search says when the settings hold no key or no model.</summary>
    internal const string NoModel = "No AI model is set up. The plugin needs one to pick files: add an OpenRouter key and a model in the settings.";

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
        SeasonName = video.SeasonName,
        Episode = video.Episode,
        AbsoluteEpisode = video.AbsoluteEpisode,
        EpisodeTitle = video.IsMovie ? null : video.Name,
        Aired = video.Aired,
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
    /// Finds and ranks the files for a video. Which entry is the show's and which file is an episode's
    /// is the model's to say. When it gives no answer, a search for a job stops and says so, and a search
    /// for someone who chooses by hand lists the files in the order of their names, with none suggested.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="language">The wanted subtitle language.</param>
    /// <param name="note">Told what is going on, for the page.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <param name="byHand">Whether a person will choose from the list, so that it is worth having without the model's answer.</param>
    /// <returns>The files, best first.</returns>
    public async Task<FindResult> FindAsync(VideoInfo video, string language, Action<string?> note, CancellationToken cancellationToken, bool byHand = false)
    {
        var query = ToQuery(video, language);
        var options = Options();
        var model = _model.Model;
        var modelProblem = model is null ? NoModel : null;
        var cost = 0.0;

        var reports = new List<SourceReport>();
        var candidates = new List<Candidate>();
        var all = new List<SourceFile>();

        IReadOnlyList<SeasonEpisode>? seasonEpisodes = null;

        FindResult Stopped() => new() { Sources = reports, CostUsd = cost, ModelProblem = modelProblem, NeedsChoice = true };

        if (modelProblem is not null && !byHand)
        {
            return Stopped();
        }

        foreach (var source in _sources.ReadyFor(language))
        {
            try
            {
                IReadOnlyList<SourceFile> files;
                string? entryId = null;
                string? entryName = null;
                string? matchedBy = null;
                IReadOnlyList<EntryRef> parts = [];

                if (source.HasEntries)
                {
                    var key = MatchKey(source.Id, video, language);
                    var match = _store.Show(video.ShowId)?.Matches.GetValueOrDefault(key);
                    var age = match is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - match.At;
                    seasonEpisodes ??= video.IsMovie ? [] : _library.GetSeasonEpisodes(video.Id);
                    var upTo = seasonEpisodes.Count == 0 ? 0 : seasonEpisodes.Max(e => e.Number);

                    // A match "by the rules" is from a version that picked without a model, and a model's answer to an
                    // older question may have missed a season the source splits into parts. Both are asked again.
                    var stale = match is null
                        || (match.By != "user" && age > _matchLifetime)
                        || (match.EntryId.Length == 0 && age > _noMatchLifetime)
                        || match.By == "rules"
                        || (match.By == "model" && match.Question < EntryMatch.CurrentQuestion)
                        || (match.By == "model" && upTo > match.UpTo);

                    if (stale && modelProblem is null)
                    {
                        var entries = await source.SearchEntriesAsync(query, null, cancellationToken).ConfigureAwait(false);
                        note(model!.Name + " is looking for the show on " + source.Name);
                        try
                        {
                            var choice = await _model.ChooseEntryAsync(query, seasonEpisodes, source, entries, cancellationToken).ConfigureAwait(false);
                            cost += choice.CostUsd;
                            var first = choice.Entries.Count > 0 ? choice.Entries[0] : null;
                            match = new EntryMatch
                            {
                                EntryId = first?.Id ?? string.Empty,
                                EntryName = first is null ? string.Empty : NameOf(first),
                                EntryNotes = ModelPicker.CleanNotes(first?.Notes),
                                MoreEntries = choice.Entries.Skip(1).Select(entry => new EntryPart { Id = entry.Id, Name = NameOf(entry), Notes = ModelPicker.CleanNotes(entry.Notes) }).ToList(),
                                Question = EntryMatch.CurrentQuestion,
                                UpTo = upTo,
                                By = "model",
                                Model = model.Name,
                                Reason = choice.Reason,
                                At = DateTimeOffset.UtcNow
                            };
                            var saved = match;
                            _store.UpdateShow(video.ShowId, show => show.Matches[key] = saved);
                            stale = false;
                        }
                        catch (ModelException ex)
                        {
                            modelProblem = ex.Message;
                            _logger.LogWarning("The model could not choose an entry on {Source}: {Problem}", source.Name, ex.Message);
                        }

                        note(null);
                    }

                    if (stale)
                    {
                        if (!byHand)
                        {
                            return Stopped();
                        }

                        if (match is null)
                        {
                            reports.Add(new SourceReport(source.Id, source.Name, null, null, 0, null, "No entry is chosen for this " + (video.IsMovie ? "film" : "show and season") + " on " + source.Name + ", because " + (model is null ? "no AI model is set up" : "the model gave no answer") + ". Search for the entry yourself."));
                            continue;
                        }
                    }

                    matchedBy = match!.By switch { "model" => match.Model ?? "the model", "user" => "you", _ => "the rules" };
                    if (match.EntryId.Length == 0)
                    {
                        reports.Add(new SourceReport(source.Id, source.Name, null, null, 0, matchedBy, source.Name + " has no entry for this " + (video.IsMovie ? "film" : "show and season") + "."));
                        continue;
                    }

                    parts = PartsOf(match);
                    entryId = match.EntryId;
                    entryName = match.EntryName;
                    files = await ListEntryFilesAsync(source, query, parts, cancellationToken).ConfigureAwait(false);
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
                reports.Add(new SourceReport(source.Id, source.Name, entryId, entryName, files.Count, matchedBy, null, parts.Count > 1 ? parts.Select(p => p.Name).ToList() : null));

                // Only the files of an entry need a reader of names. A source that is asked for one episode answers for that episode.
                if (source.HasEntries && files.Count > 0)
                {
                    if (modelProblem is null)
                    {
                        try
                        {
                            var (mapped, mapCost) = await MapAsync(source, video, query, parts, files, options, model!, rejected, note, cancellationToken).ConfigureAwait(false);
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
                        }
                    }

                    if (!byHand)
                    {
                        return Stopped();
                    }

                    // For someone who chooses by hand the files come in the order of their names. None is tried unasked.
                    candidates.AddRange(ranked.Select(c => c with { Auto = false }));
                    continue;
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
            Model = modelProblem is null ? model!.Name : null,
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
            EntryName = NameOf(entry),
            EntryNotes = ModelPicker.CleanNotes(entry.Notes),
            By = "user",
            Reason = "Chosen by " + userName,
            At = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Lists the files of a season's entries, entry by entry in airing order. A file listed by two entries is kept once, with the first.
    /// </summary>
    public async Task<IReadOnlyList<SourceFile>> ListEntryFilesAsync(ISubtitleSource source, VideoQuery query, IReadOnlyList<EntryRef> parts, CancellationToken cancellationToken)
    {
        if (parts.Count == 1)
        {
            return await ListFilesAsync(source, query, parts[0].Id, cancellationToken).ConfigureAwait(false);
        }

        var files = new List<SourceFile>();
        foreach (var part in parts)
        {
            files.AddRange(await ListFilesAsync(source, query, part.Id, cancellationToken).ConfigureAwait(false));
        }

        return files.DistinctBy(file => file.Id, StringComparer.Ordinal).ToList();
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

    /// <summary>The entries of a match by id and name, in airing order.</summary>
    internal static IReadOnlyList<EntryRef> PartsOf(EntryMatch match) =>
        [new(match.EntryId, match.EntryName, match.EntryNotes), .. (match.MoreEntries ?? []).Where(part => part.Id.Length > 0 && part.Id != match.EntryId).Select(part => new EntryRef(part.Id, part.Name, part.Notes))];

    /// <summary>
    /// The files the model reads, at most <see cref="ModelPicker.MaxFilesForModel"/>. When a season's entries list more,
    /// each entry keeps an even share, so a large entry cannot crowd out the files of the others.
    /// </summary>
    internal static IReadOnlyList<SourceFile> ForModel(IReadOnlyList<SourceFile> files, IReadOnlyList<EntryRef> parts)
    {
        if (files.Count <= ModelPicker.MaxFilesForModel || parts.Count < 2)
        {
            return files;
        }

        var groups = parts.Select(part => files.Where(file => file.EntryId == part.Id).ToList()).ToList();
        var shares = new int[groups.Count];
        var left = ModelPicker.MaxFilesForModel;
        var open = Enumerable.Range(0, groups.Count).ToList();
        while (left > 0 && open.Count > 0)
        {
            var share = Math.Max(1, left / open.Count);
            foreach (var i in open.ToList())
            {
                var take = Math.Min(Math.Min(share, groups[i].Count - shares[i]), left);
                shares[i] += take;
                left -= take;
                if (shares[i] == groups[i].Count)
                {
                    open.Remove(i);
                }
            }
        }

        return groups.SelectMany((group, i) => group.Take(shares[i])).ToList();
    }

    private static string NameOf(SourceEntry entry) => entry.JapaneseName ?? entry.Name;

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
        IReadOnlyList<EntryRef> parts,
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

        // One entry keeps the key it always had; a season split into parts is keyed by all of its entries.
        var entries = string.Join('+', parts.Select(part => part.Id));
        var key = string.Create(CultureInfo.InvariantCulture, $"{source.Id}|{entries}|{(video.IsMovie ? "film" : "s" + (video.Season ?? 0))}|{query.Language}");
        files = ForModel(files, parts);
        var fingerprint = Fingerprint(files, episodes, model.Id);
        var mapping = _store.Show(video.ShowId)?.Mappings.GetValueOrDefault(key);
        var cost = 0.0;

        if (mapping is null || mapping.ListHash != fingerprint)
        {
            note(model.Name + " is reading the file names");
            var map = await _model.MapFilesAsync(query, episodes, files, cancellationToken, parts).ConfigureAwait(false);
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
