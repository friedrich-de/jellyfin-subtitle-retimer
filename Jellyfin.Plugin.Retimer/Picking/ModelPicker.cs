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
internal sealed record SeasonEpisode(int Number, int? AbsoluteNumber, string? Title, string? VideoFileName);

/// <summary>
/// The entry the model chose at a source, or none.
/// </summary>
internal sealed record EntryChoice(SourceEntry? Entry, string Reason, double CostUsd);

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
internal sealed class ModelPicker
{
    internal const int MaxFilesForModel = 400;

    private readonly IModelClient _client;

    public ModelPicker(IModelClient client)
    {
        _client = client;
    }

    /// <summary>Gets the model in use, or <c>null</c>.</summary>
    public Configuration.ModelEntry? Model => _client.Current;

    /// <summary>
    /// Lets the model pick a source's entry for a show and season. It may search the source with other titles.
    /// </summary>
    public async Task<EntryChoice> ChooseEntryAsync(VideoQuery video, ISubtitleSource source, IReadOnlyList<SourceEntry> entries, CancellationToken cancellationToken)
    {
        var model = _client.Current ?? throw new ModelException(ModelError.NotConfigured, "No model is set up.");
        var known = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        var tools = new JsonArray(
            Tool(
                "search_entries",
                "Search " + source.Name + " for entries by a title. Use it for another spelling: romaji, Japanese, English, or the title with the season.",
                new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                "query"),
            Tool(
                "choose_entry",
                "Give the final answer: the id of the entry for this show and season, or null when " + source.Name + " has no entry for it.",
                new JsonObject
                {
                    ["entry_id"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "One short sentence." }
                },
                "entry_id",
                "reason"));

        var about = new JsonObject
        {
            ["title"] = video.Title,
            ["original_title"] = video.OriginalTitle,
            ["year"] = video.Year,
            ["kind"] = video.IsMovie ? "film" : "series",
            ["season"] = video.IsMovie ? null : video.Season,
            ["anilist_id"] = video.AnilistId,
            ["tmdb_id"] = video.TmdbId
        };

        var messages = new JsonArray(
            Message(
                "system",
                "You match a show or film from a media library to its entry on the subtitle site " + source.Name + ". "
                + "An entry holds the subtitle files of one show. On this site each season of an anime usually has its own entry, "
                + "named like \"Title 2nd Season\" or \"Title Season 2\", and its ids belong to that season. "
                + "Pick the entry for the asked season. An entry for another season, a spin-off, a recap or a film of the same franchise is wrong. "
                + "If the listed entries do not hold the right one, search with other titles, at most three times. "
                + "Then answer with choose_entry. Answer null when no entry is for this show and season."),
            Message("user", "The video:\n" + about.ToJsonString() + "\n\nEntries found so far:\n" + EntryList(entries)));

        var cost = 0.0;
        for (var turn = 0; turn < 4; turn++)
        {
            var answer = await _client.ChatAsync(model.Id, messages, tools, turn == 3 ? "choose_entry" : null, 600, cancellationToken).ConfigureAwait(false);
            cost += answer.CostUsd;

            var choice = answer.Calls.FirstOrDefault(call => call.Name == "choose_entry");
            if (choice is not null)
            {
                var id = choice.Arguments["entry_id"]?.ToString();
                var reason = choice.Arguments["reason"]?.ToString() ?? string.Empty;
                return new EntryChoice(id is not null && known.TryGetValue(id, out var entry) ? entry : null, Clip(reason) ?? string.Empty, cost);
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
    public async Task<FileMap> MapFilesAsync(VideoQuery video, IReadOnlyList<SeasonEpisode> episodes, IReadOnlyList<SourceFile> files, CancellationToken cancellationToken)
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

        var list = new StringBuilder();
        for (var i = 0; i < files.Count && i < MaxFilesForModel; i++)
        {
            var file = files[i];
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
                if (file is JsonValue value && value.TryGetValue<int>(out var index) && index >= 1 && index <= Math.Min(files.Count, MaxFilesForModel) && !indexes.Contains(index - 1))
                {
                    indexes.Add(index - 1);
                }
            }

            if (indexes.Count > 0)
            {
                map[episode] = (indexes.Take(4).ToList(), Clip(item["reason"]?.ToString()));
            }
        }

        return new FileMap(map, answer.CostUsd);
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
                ["film"] = entry.IsMovie
            });
        }

        return list.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
