using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Sources;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// One episode of the season the model maps files to.
/// </summary>
internal sealed record SeasonEpisode(int Number, int? AbsoluteNumber, string? Title, string? VideoFileName, DateTime? Aired = null);

/// <summary>
/// An entry of a season as the plugin keeps it: its id, its name and the source's notes on it.
/// </summary>
internal sealed record EntryRef(string Id, string Name, string? Notes);

/// <summary>
/// The entries the model chose at a source for a season or a film, in airing order. Empty when the source has none.
/// A season can need several: a source may split what the library keeps as one season into parts.
/// </summary>
internal sealed record EntryChoice(IReadOnlyList<SourceEntry> Entries, string Reason, double CostUsd);

/// <summary>
/// The files the model lists for each episode number, best first, as positions in the list it was given.
/// </summary>
internal sealed record FileMap(IReadOnlyDictionary<int, (IReadOnlyList<int> Files, string? Reason)> Episodes, double CostUsd);

/// <summary>
/// What the model says about a subtitle's language.
/// </summary>
internal sealed record LanguageVerdict(bool Fits, string Seen, string Reason, double CostUsd);

/// <summary>
/// The choices a model makes: the entry for a show, the files for each episode, and the language check.
/// Each one is a single question with a tool the model has to answer through.
/// </summary>
internal sealed partial class ModelPicker
{
    internal const int MaxFilesForModel = 400;

    /// <summary>The most entries one season is matched to.</summary>
    internal const int MaxEntries = 6;

    private static readonly System.Text.Json.JsonSerializerOptions _json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IModelClient _client;

    public ModelPicker(IModelClient client)
    {
        _client = client;
    }

    /// <summary>Gets the model in use, or <c>null</c>.</summary>
    public Configuration.ModelEntry? Model => _client.Current;

    /// <summary>
    /// Lets the model pick a source's entries for a show's season, or for a film. It may search the source with other titles.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="episodes">The season's episodes in the library, which tell the model how long the season is and when it aired.</param>
    /// <param name="source">The source.</param>
    /// <param name="entries">The entries the source's own search found.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>The chosen entries, in airing order.</returns>
    public async Task<EntryChoice> ChooseEntryAsync(VideoQuery video, IReadOnlyList<SeasonEpisode> episodes, ISubtitleSource source, IReadOnlyList<SourceEntry> entries, CancellationToken cancellationToken)
    {
        var model = _client.Current ?? throw new ModelException(ModelError.NotConfigured, "No model is set up.");
        var known = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        var tools = new JsonArray(
            Tool(
                "search_entries",
                "Search " + source.Name + " for entries by a title. Use it for another spelling: romaji, Japanese, English, the title with the season, or the name of the season's arc.",
                new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                "query"),
            Tool(
                "choose_entries",
                "Give the final answer: the entries that hold this season (or film), in airing order. An empty list when " + source.Name + " has none.",
                new JsonObject
                {
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "One or two short sentences: which of the site's entries hold this season, and why." },
                    ["entries"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Usually one entry. Several when the site splits this season into parts.",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["id"] = new JsonObject { ["type"] = "string" },
                                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The entry's name as listed." }
                            },
                            ["required"] = new JsonArray("id", "name")
                        }
                    }
                },
                "reason",
                "entries"));

        var messages = new JsonArray(
            Message(
                "system",
                "You match a season of a show, or a film, from a media library to its entries on the subtitle site " + source.Name + ". "
                + "An entry holds the subtitle files of one show, season or part. "
                + "The library numbers seasons the way TMDB or TheTVDB do, and the site often divides a show differently: "
                + "1. Each season of an anime usually has its own entry, named like \"Title 2nd Season\", \"Title Season 2\", \"Title 2\" or after its arc. "
                + "2. One season of the library can be split on the site into several entries, one for each part or cour, "
                + "like \"Title 2nd Season\" and \"Title 2nd Season Part 2\", or \"Title: Arc\" and \"Title: Arc - Subtitle\". "
                + "Then choose every entry that is a part of that season. Leave none out because you are unsure which episodes it holds: "
                + "the next step reads the file names of all chosen entries and sorts them to the episodes. "
                + "3. One entry can also hold several seasons of the library, such as a long show kept as a single entry. Then that entry is the answer for each of them. "
                + "Tell which entries hold the season by its name, its number of episodes and its air dates, and by the entries' notes, which often say which episodes they hold "
                + "(\"No. overall\" counts from the show's first episode, \"No. in season\" within the season). "
                + "An entry for another season of the library, a spin-off, a recap or a film of the same franchise is wrong. "
                + "If the listed entries do not hold the right ones, search with other titles, at most three times. "
                + "Then answer with choose_entries, in airing order. Answer with an empty list when no entry is for this season."),
            Message("user", "The video:\n" + About(video, episodes).ToJsonString(_json) + "\n\nEntries found so far:\n" + EntryList(entries)));

        var cost = 0.0;
        var checkedOnce = false;
        for (var turn = 0; turn < 5; turn++)
        {
            var answer = await _client.ChatAsync(model.Id, messages, tools, turn == 4 ? "choose_entries" : null, 600, cancellationToken).ConfigureAwait(false);
            cost += answer.CostUsd;

            var choice = answer.Calls.FirstOrDefault(call => call.Name == "choose_entries");
            if (choice is not null)
            {
                var chosen = Chosen(choice.Arguments, known)
                    .DistinctBy(entry => entry.Id)
                    .Take(MaxEntries)
                    .ToList();

                // When the source's notes say which episodes the chosen entries hold and the library's season has
                // more, the model is shown the gap once: a season split into parts is easy to take for one part.
                var gap = checkedOnce || turn == 4 ? null : Gap(video, episodes, chosen);
                if (gap is null)
                {
                    return new EntryChoice(chosen, Clip(choice.Arguments["reason"]?.ToString()) ?? string.Empty, cost);
                }

                checkedOnce = true;
                messages.Add(answer.Message.DeepClone());
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = choice.Id, ["content"] = gap });
                continue;
            }

            var search = answer.Calls.FirstOrDefault(call => call.Name == "search_entries");
            if (search is null)
            {
                throw new ModelException(ModelError.BadAnswer, "The model did not choose an entry.");
            }

            var query = search.Arguments["query"]?.ToString()?.Trim();
            IReadOnlyList<SourceEntry> found = string.IsNullOrEmpty(query) ? [] : await source.SearchEntriesAsync(video, query, cancellationToken).ConfigureAwait(false);
            foreach (var entry in found)
            {
                known[entry.Id] = entry;
            }

            messages.Add(answer.Message.DeepClone());
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = search.Id,
                ["content"] = found.Count == 0 ? "No entries." : EntryList(found)
            });
        }

        throw new ModelException(ModelError.BadAnswer, "The model did not choose an entry.");
    }

    /// <summary>
    /// Lets the model list, for each episode of a season, the files that are for it, best first.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="episodes">The season's episodes in the library.</param>
    /// <param name="files">The files of the season's entries, entry by entry in the order of <paramref name="parts"/>.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <param name="parts">The entries the files come from, in the order they are listed.</param>
    /// <returns>The files for each episode.</returns>
    public async Task<FileMap> MapFilesAsync(VideoQuery video, IReadOnlyList<SeasonEpisode> episodes, IReadOnlyList<SourceFile> files, CancellationToken cancellationToken, IReadOnlyList<EntryRef>? parts = null)
    {
        var model = _client.Current ?? throw new ModelException(ModelError.NotConfigured, "No model is set up.");
        var language = Languages.NameOf(video.Language);

        var tools = new JsonArray(Tool(
            "map_files",
            "Give the files for each episode.",
            new JsonObject
            {
                ["episodes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["episode"] = new JsonObject { ["type"] = "integer", ["description"] = "The episode's number within the season, as listed." },
                            ["files"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "File numbers, best first, at most 4." },
                            ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Why the first file is the best one, in a few words." }
                        },
                        ["required"] = new JsonArray("episode", "files")
                    }
                }
            },
            "episodes"));

        // Files of a season split over several entries come in groups, each under a line that names its entry.
        var split = parts is { Count: > 1 };
        string? group = null;
        var list = new StringBuilder();
        for (var i = 0; i < files.Count && i < MaxFilesForModel; i++)
        {
            var file = files[i];
            if (split && (i == 0 || !string.Equals(file.EntryId, group, StringComparison.Ordinal)))
            {
                group = file.EntryId;
                var part = parts!.FirstOrDefault(p => p.Id == group);
                list.Append("Files of the entry \"").Append(part?.Name ?? "?").Append('"');
                if (part?.Notes is { Length: > 0 } partNotes)
                {
                    list.Append(" (the site's notes on it: ").Append(partNotes).Append(')');
                }

                list.Append(":\n");
            }

            var name = file.Name.Length > 150 ? file.Name[..150] + "…" : file.Name;
            list.Append(CultureInfo.InvariantCulture, $"{i + 1} | {name}");
            if (file.Size is { } size)
            {
                list.Append(CultureInfo.InvariantCulture, $" | {Math.Max(1, size / 1024)} KB");
            }

            list.Append('\n');
        }

        var wanted = new StringBuilder();
        foreach (var episode in episodes)
        {
            wanted.Append(CultureInfo.InvariantCulture, $"episode {episode.Number}");
            if (episode.AbsoluteNumber is { } absolute && absolute != episode.Number)
            {
                wanted.Append(CultureInfo.InvariantCulture, $" (number {absolute} counted from the first season)");
            }

            if (!string.IsNullOrWhiteSpace(episode.Title))
            {
                wanted.Append(" | ").Append(episode.Title);
            }

            if (!string.IsNullOrWhiteSpace(episode.VideoFileName))
            {
                wanted.Append(" | video file: ").Append(episode.VideoFileName);
            }

            wanted.Append('\n');
        }

        var system =
            "You match subtitle files to the episodes of one season of a show (or to a film). You see only file names and sizes. "
            + "The wanted subtitle language is " + language + ". Rules: "
            + "1. A file belongs to an episode when its name carries that episode's number. Names write it as \"- 05\", \"E05\", \"S01E05\", \"第5話\" or similar. "
            + "Later seasons are often numbered on from the first season, so a file \"14\" can be episode 1 of season 2: the list says which number that is. "
            + "2. An archive (.zip) whose name gives a range of episodes, or no episode at all, may hold every episode: list it last for the episodes it covers. "
            + "Archives other than .zip cannot be opened, so leave them out, except a packed picture subtitle (.sup.7z, .sup.zip). "
            + "3. Prefer text subtitles (.srt, .ass) over picture subtitles (.sup). "
            + "4. Prefer files in " + language + " only. A file marked as two languages (ja-en, CHS&JPN, bilingual) or as another language comes last or not at all. "
            + "Files with furigana or for the hearing impaired ([cc], SDH) are fine but come after a plain one. "
            + "5. When a file is named after the same release as the video file (same group or same source such as BD or Netflix), put it first: its timing is the closest. "
            + "6. Never list a file for an episode its name does not fit. An episode without a fitting file is left out. "
            + (split
                ? "7. The site splits this season into parts, one entry each, and the files come in groups by entry. "
                    + "A file's number can count on through the whole season (\"- 14\", \"S17E14\"), count from the first season, or start again at 1 within its part (\"Part 2 - 01\", \"S02E01\"). "
                    + "Use the entry a file is in, the site's notes on it (\"No. in season: 27-40\" means its first file is episode 27 of the season), the numbers and titles of the other files of that entry, and the episode titles to tell which episode of the season it is. "
                : string.Empty)
            + "Answer with map_files: for each episode up to 4 file numbers, best first.";

        var user = "Show: " + video.Title
            + (string.IsNullOrWhiteSpace(video.OriginalTitle) ? string.Empty : " (" + video.OriginalTitle + ")")
            + (video.Year is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {video.Year}"))
            + (video.IsMovie ? "\nIt is a film. Use episode 1 for it." : string.Create(CultureInfo.InvariantCulture, $"\nSeason {video.Season ?? 1}"))
            + "\n\nEpisodes:\n" + wanted
            + "\nFiles:\n" + list;

        var messages = new JsonArray(Message("system", system), Message("user", user));
        var answer = await _client.ChatAsync(model.Id, messages, tools, "map_files", 6000, cancellationToken).ConfigureAwait(false);
        var call = answer.Calls.FirstOrDefault(c => c.Name == "map_files")
            ?? throw new ModelException(ModelError.BadAnswer, "The model did not answer with a list of files.");

        var known = episodes.Select(e => e.Number).ToHashSet();
        var map = new Dictionary<int, (IReadOnlyList<int> Files, string? Reason)>();
        var cost = answer.CostUsd;
        Read(call, files.Count, known, map);

        // A model sometimes answers for the first episode or the first few only. When a quarter or more of the
        // episodes have no files, it is asked once more for those, and what it adds is kept.
        var missing = episodes.Select(e => e.Number).Where(n => !map.ContainsKey(n)).ToList();
        if (episodes.Count >= 4 && missing.Count * 4 >= episodes.Count)
        {
            messages.Add(answer.Message.DeepClone());
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["content"] = "Your answer has no files for episodes " + Ranges(missing) + ". Give the files for each of those episodes that a file fits, with map_files. Leave out every episode that no file fits."
            });
            var more = await _client.ChatAsync(model.Id, messages, tools, "map_files", 6000, cancellationToken).ConfigureAwait(false);
            cost += more.CostUsd;
            if (more.Calls.FirstOrDefault(c => c.Name == "map_files") is { } added)
            {
                Read(added, files.Count, missing.ToHashSet(), map);
            }
        }

        return new FileMap(map, cost);
    }

    /// <summary>Reads a map_files answer into the map, for the given episode numbers only.</summary>
    private static void Read(ToolCall call, int files, HashSet<int> known, Dictionary<int, (IReadOnlyList<int> Files, string? Reason)> map)
    {
        foreach (var item in call.Arguments["episodes"] as JsonArray ?? [])
        {
            if (item?["episode"] is not JsonValue number || !number.TryGetValue<int>(out var episode) || !known.Contains(episode))
            {
                continue;
            }

            var indexes = new List<int>();
            foreach (var file in item["files"] as JsonArray ?? [])
            {
                // The model counts from 1. A number outside the list is dropped.
                if (file is JsonValue value && value.TryGetValue<int>(out var index) && index >= 1 && index <= Math.Min(files, MaxFilesForModel) && !indexes.Contains(index - 1))
                {
                    indexes.Add(index - 1);
                }
            }

            if (indexes.Count > 0)
            {
                map[episode] = (indexes.Take(4).ToList(), Clip(item["reason"]?.ToString()));
            }
        }
    }

    /// <summary>
    /// Lets the model read a sample of a subtitle and say which language it is in.
    /// </summary>
    public async Task<LanguageVerdict> CheckLanguageAsync(string sample, string wantedLanguage, CancellationToken cancellationToken)
    {
        var model = _client.Current ?? throw new ModelException(ModelError.NotConfigured, "No model is set up.");
        var wanted = Languages.NameOf(wantedLanguage);

        var tools = new JsonArray(Tool(
            "language_verdict",
            "Say which language the subtitle is in.",
            new JsonObject
            {
                ["language"] = new JsonObject { ["type"] = "string", ["description"] = "ISO 639-1 code of the language most lines are in, such as ja, en, zh." },
                ["second_language"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "ISO 639-1 code of a second language that many lines are in, such as the translation in a file with two languages. null when there is none." },
                ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "One short sentence." }
            },
            "language",
            "second_language",
            "reason"));

        var messages = new JsonArray(
            Message(
                "system",
                "You check the language of a subtitle file from a sample of its lines. The wanted language is " + wanted + ". "
                + "Single foreign words, names, song lines and sound descriptions do not make a second language. "
                + "A second language means that many lines are written in it, as in a file that shows the original and a translation together. "
                + "Answer with language_verdict."),
            Message("user", "Sample of the subtitle's lines:\n\n" + sample));

        var answer = await _client.ChatAsync(model.Id, messages, tools, "language_verdict", 300, cancellationToken).ConfigureAwait(false);
        var call = answer.Calls.FirstOrDefault(c => c.Name == "language_verdict")
            ?? throw new ModelException(ModelError.BadAnswer, "The model did not answer the language check.");

        var seen = Languages.Normalize(call.Arguments["language"]?.ToString()) ?? call.Arguments["language"]?.ToString() ?? "unknown";
        var second = call.Arguments["second_language"]?.ToString();
        second = string.IsNullOrWhiteSpace(second) || second == "null" ? null : Languages.Normalize(second) ?? second;
        var wantedCode = Languages.Normalize(wantedLanguage) ?? wantedLanguage;

        var fits = string.Equals(seen, wantedCode, StringComparison.OrdinalIgnoreCase) && (second is null || string.Equals(second, wantedCode, StringComparison.OrdinalIgnoreCase));
        var description = Languages.NameOf(seen) + (second is null || second == seen ? string.Empty : " with " + Languages.NameOf(second));
        return new LanguageVerdict(fits, description, Clip(call.Arguments["reason"]?.ToString()) ?? string.Empty, answer.CostUsd);
    }

    /// <summary>
    /// Asks a model one small question through a tool, to see that it answers and can call tools.
    /// </summary>
    public async Task<double> TestAsync(string modelId, CancellationToken cancellationToken)
    {
        var tools = new JsonArray(Tool(
            "answer",
            "Give the answer.",
            new JsonObject { ["episode"] = new JsonObject { ["type"] = "integer" } },
            "episode"));
        var messages = new JsonArray(Message("user", "Which episode is the file \"Show - 07 [BD].ja.srt\"? Answer with the tool."));
        var answer = await _client.ChatAsync(modelId, messages, tools, "answer", 100, cancellationToken).ConfigureAwait(false);
        if (answer.Calls.All(call => call.Name != "answer"))
        {
            throw new ModelException(ModelError.BadAnswer, "The model answered, but it did not call the tool. It cannot be used here.");
        }

        return answer.CostUsd;
    }

    /// <summary>What the model is told about the video and, for an episode, about its season in the library.</summary>
    internal static JsonObject About(VideoQuery video, IReadOnlyList<SeasonEpisode> episodes)
    {
        var about = new JsonObject
        {
            ["title"] = video.Title,
            ["original_title"] = video.OriginalTitle,
            ["year"] = video.Year,
            ["kind"] = video.IsMovie ? "film" : "series"
        };

        if (video.IsMovie)
        {
            about["aired"] = Day(video.Aired);
        }
        else
        {
            about["season"] = video.Season;
            if (!string.IsNullOrWhiteSpace(video.SeasonName))
            {
                about["season_name"] = video.SeasonName;
            }

            var numbers = episodes.Select(e => e.Number).ToList();
            if (numbers.Count > 0)
            {
                about["season_episodes_in_library"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{numbers.Count} ({(numbers.Min() == numbers.Max() ? numbers.Min().ToString(CultureInfo.InvariantCulture) : numbers.Min() + " to " + numbers.Max())})");
            }

            // Counted on from the earlier seasons the library holds, which matches a source's "No. overall" when it holds them all.
            var absolute = episodes.Where(e => e.AbsoluteNumber is not null && e.AbsoluteNumber != e.Number).Select(e => e.AbsoluteNumber!.Value).ToList();
            if (absolute.Count > 0 && video.Season is > 1)
            {
                about["season_episodes_counted_from_the_first_season"] = string.Create(CultureInfo.InvariantCulture, $"{absolute.Min()} to {absolute.Max()}");
            }

            var aired = episodes.Select(e => e.Aired).OfType<DateTime>().Order().ToList();
            if (aired.Count > 0)
            {
                about["season_aired"] = aired.Count == 1 || aired[0].Year * 12 + aired[0].Month == aired[^1].Year * 12 + aired[^1].Month
                    ? Month(aired[0])
                    : Month(aired[0]) + " to " + Month(aired[^1]);
            }

            about["episode"] = video.Episode;
            if (!string.IsNullOrWhiteSpace(video.EpisodeTitle))
            {
                about["episode_title"] = video.EpisodeTitle;
            }

            if (video.Aired is not null)
            {
                about["episode_aired"] = Day(video.Aired);
            }
        }

        about["anilist_id"] = video.AnilistId;
        about["tmdb_id"] = video.TmdbId;
        return about;

        static string? Day(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static string Month(DateTime date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What the model is told when the chosen entries, by the source's notes ("No. in season: 1-13"), do not hold
    /// every episode of the season that the library has. <c>null</c> when they do, or when the notes do not say.
    /// </summary>
    internal static string? Gap(VideoQuery video, IReadOnlyList<SeasonEpisode> episodes, IReadOnlyList<SourceEntry> chosen)
    {
        if (video.IsMovie || chosen.Count == 0 || episodes.Count == 0)
        {
            return null;
        }

        var held = new HashSet<int>();
        foreach (var entry in chosen)
        {
            var ranges = InSeason().Matches(entry.Notes ?? string.Empty);
            if (ranges.Count == 0)
            {
                return null;
            }

            foreach (System.Text.RegularExpressions.Match range in ranges)
            {
                var from = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                var to = range.Groups[2].Success ? int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture) : from;
                for (var n = from; n <= to && n - from < 2000; n++)
                {
                    held.Add(n);
                }
            }
        }

        var missing = episodes.Select(e => e.Number).Where(n => n > 0 && !held.Contains(n)).Distinct().Order().ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        return "By the site's notes, the chosen entries hold episodes " + Ranges(held.Order().ToList()) + " of a season, "
            + string.Create(CultureInfo.InvariantCulture, $"but season {video.Season} in the library also has episodes {Ranges(missing)}. ")
            + "If the site splits this season into parts, other entries hold those: look at the entries again, search if needed, and answer with choose_entries with all entries of the season. "
            + "If no entry holds them, answer with the same choice again.";
    }

    /// <summary>Numbers as short ranges, such as "1-13, 27".</summary>
    private static string Ranges(List<int> numbers)
    {
        var parts = new List<string>();
        for (var i = 0; i < numbers.Count;)
        {
            var j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1)
            {
                j++;
            }

            parts.Add(i == j ? numbers[i].ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{numbers[i]}-{numbers[j]}"));
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The entries in a choose_entries answer. Ids that differ in one digit are easy to mix up, so an entry the
    /// answer names exactly is taken by its name, and by its id only when the name fits no entry.
    /// Plain ids (<c>entry_ids</c>, or an older single <c>entry_id</c>) are read too.
    /// </summary>
    private static IEnumerable<SourceEntry> Chosen(JsonNode arguments, Dictionary<string, SourceEntry> known)
    {
        var items = arguments["entries"] as JsonArray
            ?? arguments["entry_ids"] as JsonArray
            ?? (arguments["entry_ids"] ?? arguments["entry_id"]) switch
            {
                JsonValue one => [one.DeepClone()],
                _ => []
            };

        foreach (var item in items)
        {
            var id = (item is JsonObject named ? named["id"] : item)?.ToString().Trim();
            var name = (item as JsonObject)?["name"]?.ToString().Trim();
            var byName = string.IsNullOrEmpty(name)
                ? []
                : known.Values.Where(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.EnglishName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.JapaneseName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1)
            {
                yield return byName[0];
            }
            else if (!string.IsNullOrEmpty(id) && known.TryGetValue(id, out var entry))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// A source's notes on an entry as one short line: links give way to their text and line breaks to " · ".
    /// Jimaku's notes often say which episodes an entry holds ("No. overall: 380-392", "No. in season: 14-26").
    /// </summary>
    internal static string? CleanNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var text = MarkdownLink().Replace(notes, "$1");
        text = BareLink().Replace(text, string.Empty);
        var lines = text.Split('\n').Select(line => Spaces().Replace(line, " ").Trim(' ', '-', '*', '\r', '\t')).Where(line => line.Length > 0);
        text = string.Join(" · ", lines);
        return text.Length == 0 ? null : (text.Length > 240 ? text[..240] + "…" : text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex MarkdownLink();

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://\S+")]
    private static partial System.Text.RegularExpressions.Regex BareLink();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Spaces();

    [System.Text.RegularExpressions.GeneratedRegex(@"No\.?\s*in\s*season\s*:\s*(\d{1,4})(?:\s*[-–~]\s*(\d{1,4}))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex InSeason();

    private static string? Clip(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : (text.Length > 200 ? text[..200] + "…" : text);
    }

    private static string EntryList(IReadOnlyList<SourceEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "(none)";
        }

        var list = new JsonArray();
        foreach (var entry in entries.Take(40))
        {
            list.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["name"] = entry.Name,
                ["english_name"] = entry.EnglishName,
                ["japanese_name"] = entry.JapaneseName,
                ["anilist_id"] = entry.AnilistId,
                ["tmdb_id"] = entry.TmdbId,
                ["film"] = entry.IsMovie,
                ["notes"] = CleanNotes(entry.Notes)
            });
        }

        return list.ToJsonString(_json);
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray())
            }
        }
    };
}
