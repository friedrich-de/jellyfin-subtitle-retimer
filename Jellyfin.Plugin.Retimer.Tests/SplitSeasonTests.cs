using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Sources;
using Jellyfin.Plugin.Retimer.Tests.Support;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// A season of the library that a source splits into several entries, such as Bleach's season 17
/// (Thousand-Year Blood War), which Jimaku keeps as four entries, one for each part.
/// </summary>
public sealed class SplitSeasonTests : IDisposable
{
    private readonly World _w = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    private static VideoQuery Bleach(int episode = 1, string? seasonName = null) => new()
    {
        Title = "Bleach",
        OriginalTitle = "BLEACH",
        Year = 2004,
        Season = 17,
        SeasonName = seasonName,
        Episode = episode,
        Language = "ja"
    };

    private static List<SeasonEpisode> Episodes(int from, int to, DateTime? firstAired = null) =>
        Enumerable.Range(from, to - from + 1)
            .Select(n => new SeasonEpisode(n, null, "Episode " + n, null, firstAired?.AddDays(7 * (n - from))))
            .ToList();

    /// <summary>Bleach as Jimaku lists it: the original show, and the four parts of season 17 with their notes.</summary>
    private FakeSource BleachEntries()
    {
        _w.Source.Entries.Add(new SourceEntry { Source = "jimaku", Id = "540", Name = "BLEACH", Notes = "Episode guide: https://en.wikipedia.org/wiki/List_of_Bleach_episodes" });
        _w.Source.Entries.Add(new SourceEntry { Source = "jimaku", Id = "2135", Name = "BLEACH: Sennen Kessen-hen", JapaneseName = "BLEACH 千年血戦篇", Notes = "No. overall: 367-379\r\n\r\nNo. in season: 1-13" });
        _w.Source.Entries.Add(new SourceEntry { Source = "jimaku", Id = "2966", Name = "BLEACH: Sennen Kessen-hen - Ketsubetsu-tan", JapaneseName = "BLEACH 千年血戦篇-訣別譚-", Notes = "No. overall: 380-392\r\n\r\nNo. in season: 14-26" });
        _w.Source.Entries.Add(new SourceEntry { Source = "jimaku", Id = "7651", Name = "BLEACH: Sennen Kessen-hen - Soukoku-tan", JapaneseName = "BLEACH 千年血戦篇-相剋譚-", Notes = "No. overall: 393-406\r\n\r\nNo. in season: 27-40" });
        return _w.Source;
    }

    private static JsonObject Answer(params (string Id, string Name)[] entries) => new()
    {
        ["reason"] = "the season's parts",
        ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["name"] = e.Name }).ToArray())
    };

    [Fact]
    public async Task A_season_the_source_splits_into_parts_gets_all_of_its_entries_in_order()
    {
        var source = BleachEntries();
        _w.Model.ChoosesEntries("2135", "2966", "7651");

        var choice = await _w.Picker.ChooseEntryAsync(Bleach(), Episodes(1, 40), source, source.Entries, Token);

        Assert.Equal(["2135", "2966", "7651"], choice.Entries.Select(e => e.Id));
        Assert.Equal("the title and the season fit", choice.Reason);
    }

    [Fact]
    public async Task An_entry_the_answer_names_is_taken_by_its_name_when_its_id_is_off_by_a_digit()
    {
        _w.Source.AddEntry("3456", "Shingeki no Kyojin 3");
        _w.Source.AddEntry("3457", "Shingeki no Kyojin 3 Part 2");
        _w.Source.AddEntry("3458", "Shingeki no Kyojin 2");
        _w.Model.Tools["choose_entries"] = _ => Answer(("3456", "Shingeki no Kyojin 3"), ("3458", "Shingeki no Kyojin 3 Part 2"));

        var choice = await _w.Picker.ChooseEntryAsync(Bleach() with { Title = "Attack on Titan", Season = 3 }, Episodes(1, 22), _w.Source, _w.Source.Entries, Token);

        Assert.Equal(["3456", "3457"], choice.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Unknown_and_repeated_entries_are_dropped_and_an_answer_with_a_single_entry_id_is_read()
    {
        _w.Source.AddEntry("384", "Fate/Zero");
        _w.Model.Tools["choose_entries"] = _ => new JsonObject { ["entry_ids"] = new JsonArray("999", "384", "384"), ["reason"] = "fits" };
        var query = Bleach() with { Title = "Fate/Zero", Season = 1 };

        var several = await _w.Picker.ChooseEntryAsync(query, Episodes(1, 13), _w.Source, _w.Source.Entries, Token);
        _w.Model.Tools["choose_entries"] = _ => new JsonObject { ["entry_id"] = "384", ["reason"] = "fits" };
        var single = await _w.Picker.ChooseEntryAsync(query, Episodes(1, 13), _w.Source, _w.Source.Entries, Token);
        _w.Model.ChoosesEntries();
        var none = await _w.Picker.ChooseEntryAsync(query, Episodes(1, 13), _w.Source, _w.Source.Entries, Token);

        Assert.Equal(["384"], several.Entries.Select(e => e.Id));
        Assert.Equal(["384"], single.Entries.Select(e => e.Id));
        Assert.Empty(none.Entries);
    }

    [Fact]
    public async Task The_model_hears_the_seasons_name_its_episodes_their_air_dates_and_the_entries_notes()
    {
        var source = BleachEntries();
        _w.Model.ChoosesEntries("2135");

        await _w.Picker.ChooseEntryAsync(Bleach(14, "Thousand-Year Blood War") with { Aired = new DateTime(2023, 7, 8) }, Episodes(1, 13, new DateTime(2022, 10, 11)), source, source.Entries, Token);

        var prompt = _w.Model.Calls[0].Prompt;
        Assert.Contains("\"season\":17", prompt, StringComparison.Ordinal);
        Assert.Contains("\"season_name\":\"Thousand-Year Blood War\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"season_episodes_in_library\":\"13 (1 to 13)\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"season_aired\":\"2022-10 to 2023-01\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"episode_aired\":\"2023-07-08\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"notes\":\"No. overall: 380-392 · No. in season: 14-26\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"notes\":\"Episode guide:\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_notes_show_that_the_chosen_entries_miss_episodes_the_model_is_asked_once_more()
    {
        var source = BleachEntries();
        var asked = 0;
        _w.Model.Tools["choose_entries"] = _ => ++asked == 1
            ? Answer(("2135", "BLEACH: Sennen Kessen-hen"))
            : Answer(("2135", "BLEACH: Sennen Kessen-hen"), ("2966", "BLEACH: Sennen Kessen-hen - Ketsubetsu-tan"));

        var choice = await _w.Picker.ChooseEntryAsync(Bleach(), Episodes(1, 26), source, source.Entries, Token);

        Assert.Equal(["2135", "2966"], choice.Entries.Select(e => e.Id));
        Assert.Equal(2, asked);
        Assert.Contains("hold episodes 1-13 of a season, but season 17 in the library also has episodes 14-26", _w.Model.Calls[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_is_shown_a_gap_only_once_and_not_when_the_notes_say_nothing()
    {
        var source = BleachEntries();
        _w.Model.ChoosesEntries("2135");

        var kept = await _w.Picker.ChooseEntryAsync(Bleach(), Episodes(1, 26), source, source.Entries, Token);
        var calls = _w.Model.Calls.Count;
        _w.Model.ChoosesEntries("540");
        var original = await _w.Picker.ChooseEntryAsync(Bleach() with { Season = 5 }, Episodes(92, 109), source, source.Entries, Token);

        Assert.Equal(["2135"], kept.Entries.Select(e => e.Id));
        Assert.Equal(2, calls);
        Assert.Equal(["540"], original.Entries.Select(e => e.Id));
        Assert.Equal(3, _w.Model.Calls.Count);
    }

    [Theory]
    [InlineData("No. overall: 367-379\r\n\r\nNo. in season: 1-13", "No. overall: 367-379 · No. in season: 1-13")]
    [InlineData("Recaps: [Memories\r\n](https://jimaku.cc/entry/2848)\r\n\r\nNo. overall: 114-138", "Recaps: Memories · No. overall: 114-138")]
    [InlineData("Episode guide: https://en.wikipedia.org/wiki/List", "Episode guide:")]
    [InlineData("  \r\n ", null)]
    [InlineData(null, null)]
    public void The_sources_notes_reach_the_model_as_one_line_without_links(string? notes, string? expected)
    {
        Assert.Equal(expected, ModelPicker.CleanNotes(notes));
    }

    [Theory]
    [InlineData("Season 17", 17, null)]
    [InlineData("Staffel 17", 17, null)]
    [InlineData("Specials", 0, null)]
    [InlineData("Thousand-Year Blood War", 17, "Thousand-Year Blood War")]
    [InlineData("  ", 3, null)]
    [InlineData(null, 3, null)]
    public void Only_a_season_name_of_its_own_is_passed_on(string? name, int number, string? expected)
    {
        Assert.Equal(expected, JellyfinVideoLibrary.OwnSeasonName(name, number));
    }

    [Fact]
    public async Task Jimaku_is_searched_for_the_title_with_the_seasons_own_name()
    {
        var jimaku = new FakeJimaku();
        var settings = new StubSettings();
        var source = new JimakuSource(jimaku, settings);

        await source.SearchEntriesAsync(Bleach(1, "Thousand-Year Blood War"), null, Token);
        await source.SearchEntriesAsync(Bleach(1), null, Token);

        Assert.Equal(1, jimaku.Searches.Count(q => q.Title == "Bleach Thousand-Year Blood War"));
        Assert.Equal(2, jimaku.Searches.Count(q => q.Title == "Bleach" && q.Anime != false));
    }

    [Fact]
    public async Task The_files_of_a_split_season_reach_the_model_in_groups_with_each_entrys_notes()
    {
        var files = new List<SourceFile>
        {
            new() { Source = "jimaku", Id = "a - 01.srt", Name = "a - 01.srt", EntryId = "2135" },
            new() { Source = "jimaku", Id = "b - 14.srt", Name = "b - 14.srt", EntryId = "2966" }
        };
        EntryRef[] parts = [new("2135", "BLEACH 千年血戦篇", "No. in season: 1-13"), new("2966", "BLEACH 千年血戦篇-訣別譚-", null)];

        var map = await _w.Picker.MapFilesAsync(Bleach(), Episodes(1, 14), files, Token, parts);

        var prompt = _w.Model.Calls[^1].Prompt;
        Assert.Contains("Files of the entry \"BLEACH 千年血戦篇\" (the site's notes on it: No. in season: 1-13):\n1 | a - 01.srt", prompt, StringComparison.Ordinal);
        Assert.Contains("Files of the entry \"BLEACH 千年血戦篇-訣別譚-\":\n2 | b - 14.srt", prompt, StringComparison.Ordinal);
        Assert.Contains("7. The site splits this season into parts", prompt, StringComparison.Ordinal);
        Assert.Equal([0], map.Episodes[1].Files);
        Assert.Equal([1], map.Episodes[14].Files);
    }

    [Fact]
    public async Task A_season_with_one_entry_is_listed_as_before()
    {
        var files = new List<SourceFile> { new() { Source = "jimaku", Id = "a - 01.srt", Name = "a - 01.srt", EntryId = "384" } };

        await _w.Picker.MapFilesAsync(Bleach(), Episodes(1, 1), files, Token, [new EntryRef("384", "Fate/Zero", "No. in season: 1-13")]);

        var prompt = _w.Model.Calls[^1].Prompt;
        Assert.Contains("Files:\n1 | a - 01.srt", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Files of the entry", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("7. The site splits", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_answer_leaves_out_most_episodes_the_model_is_asked_once_for_those()
    {
        var files = Enumerable.Range(1, 8).Select(n => new SourceFile { Source = "jimaku", Id = $"x - {n:00}.srt", Name = $"x - {n:00}.srt", EntryId = "384" }).ToList();
        var asked = 0;
        _w.Model.Tools["map_files"] = _ => ++asked == 1
            ? new JsonObject { ["episodes"] = new JsonArray(new JsonObject { ["episode"] = 1, ["files"] = new JsonArray(1) }) }
            : new JsonObject
            {
                ["episodes"] = new JsonArray(Enumerable.Range(1, 8).Select(n => (JsonNode)new JsonObject { ["episode"] = n, ["files"] = new JsonArray(n == 1 ? 8 : n) }).ToArray())
            };

        var map = await _w.Picker.MapFilesAsync(Bleach(), Episodes(1, 8), files, Token);

        Assert.Equal(2, asked);
        Assert.Equal(Enumerable.Range(1, 8), map.Episodes.Keys.Order());
        Assert.Equal([0], map.Episodes[1].Files);
        Assert.Equal([6], map.Episodes[7].Files);
        Assert.Contains("no files for episodes 2-8", _w.Model.Calls[^1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2 * _w.Model.CostPerCall, map.CostUsd, 6);
    }

    [Fact]
    public async Task A_few_episodes_without_a_file_do_not_cost_a_second_question()
    {
        var files = Enumerable.Range(1, 7).Select(n => new SourceFile { Source = "jimaku", Id = $"x - {n:00}.srt", Name = $"x - {n:00}.srt", EntryId = "384" }).ToList();

        var map = await _w.Picker.MapFilesAsync(Bleach(), Episodes(1, 8), files, Token);

        Assert.Equal(7, map.Episodes.Count);
        Assert.Equal(1, _w.Model.CallsOf("map_files"));
    }

    [Fact]
    public void Each_entry_keeps_an_even_share_of_the_files_the_model_reads()
    {
        var files = Enumerable.Range(0, 900).Select(i => new SourceFile { Source = "jimaku", Id = "big" + i, Name = "big" + i, EntryId = "540" })
            .Concat(Enumerable.Range(0, 30).Select(i => new SourceFile { Source = "jimaku", Id = "a" + i, Name = "a" + i, EntryId = "2135" }))
            .Concat(Enumerable.Range(0, 60).Select(i => new SourceFile { Source = "jimaku", Id = "b" + i, Name = "b" + i, EntryId = "2966" }))
            .ToList();
        EntryRef[] parts = [new("540", "BLEACH", null), new("2135", "A", null), new("2966", "B", null)];

        var read = FileFinder.ForModel(files, parts);

        Assert.Equal(ModelPicker.MaxFilesForModel, read.Count);
        Assert.Equal(30, read.Count(f => f.EntryId == "2135"));
        Assert.Equal(60, read.Count(f => f.EntryId == "2966"));
        Assert.Equal(["540", "2135", "2966"], read.Select(f => f.EntryId).Distinct());
        Assert.Same(files, FileFinder.ForModel(files, parts[..1]));
    }

    /// <summary>Bleach season 17 in the library with four episodes; two parts at the source hold two each.</summary>
    private List<VideoInfo> BleachSeason17()
    {
        _w.Settings.Current.OpenRouterApiKey = "or-key";
        _w.Settings.Current.JimakuApiKey = "jimaku-key";
        BleachEntries();
        var showId = Guid.Parse("b1eac400-0000-0000-0000-000000000017");
        var videos = Enumerable.Range(1, 4).Select(n => _w.Library.AddEpisode(showId, "Bleach", 17, n, "Episode " + n)).ToList();
        _w.Source.AddFile("[Nanako] Bleach - Sennen Kessen-hen - 01 (1080p).srt", "2135");
        _w.Source.AddFile("[Nanako] Bleach - Sennen Kessen-hen - 02 (1080p).srt", "2135");
        _w.Source.AddFile("[Nanako] Bleach - Sennen Kessen-hen - 03 (1080p).srt", "2966");
        _w.Source.AddFile("[Nanako] Bleach - Sennen Kessen-hen - 04 (1080p).srt", "2966");
        _w.Model.ChoosesEntries("2135", "2966");
        return videos;
    }

    [Fact]
    public async Task An_episode_is_found_in_the_second_entry_of_its_season()
    {
        var videos = BleachSeason17();

        var job = await _w.RunAsync(_w.JobFor(videos[2]), Token);

        Assert.Equal(JobState.SavedRaw, job.State);
        Assert.Equal("[Nanako] Bleach - Sennen Kessen-hen - 03 (1080p).srt", _w.Store.Item(videos[2].Id, "ja")!.FileName);
        Assert.Equal("2966", _w.Store.Item(videos[2].Id, "ja")!.EntryId);
        var show = _w.Store.Show(videos[2].ShowId)!;
        var match = show.Matches[FileFinder.MatchKey("jimaku", videos[2], "ja")];
        Assert.Equal(("2135", "BLEACH 千年血戦篇", "No. overall: 367-379 · No. in season: 1-13"), (match.EntryId, match.EntryName, match.EntryNotes));
        Assert.Equal(["2966"], match.MoreEntries.Select(p => p.Id));
        Assert.Equal((EntryMatch.CurrentQuestion, 4), (match.Question, match.UpTo));
        Assert.True(show.Mappings.ContainsKey("jimaku|2135+2966|s17|ja"));
        Assert.Equal(["2135", "2966"], _w.Source.Listings);
    }

    [Fact]
    public async Task The_page_names_every_entry_of_a_split_season_and_marks_them_in_use()
    {
        var videos = BleachSeason17();

        var (view, error) = await _w.Page.FilesAsync(_w.Admin, videos[0].Id, Token);
        var (entries, _) = await _w.Page.EntriesAsync(_w.Admin, videos[0].Id, "Bleach", Token);

        Assert.Null(error);
        var source = Assert.Single(view!.Sources);
        Assert.Equal(["BLEACH 千年血戦篇", "BLEACH 千年血戦篇-訣別譚-"], source.EntryNames);
        Assert.Equal(4, source.Files);
        Assert.Equal(["2135", "2966"], entries!.Where(e => e.Current).Select(e => e.Id));
    }

    [Fact]
    public async Task A_choice_the_model_made_for_an_older_question_is_asked_again_once()
    {
        var videos = _w.FateZero();
        var key = FileFinder.MatchKey("jimaku", videos[0], "ja");
        _w.Store.UpdateShow(videos[0].ShowId, show => show.Matches[key] = new EntryMatch { EntryId = string.Empty, By = "model", At = DateTimeOffset.UtcNow });

        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        await _w.RunAsync(_w.JobFor(videos[1]), Token);

        Assert.Single(_w.Source.Searches);
        Assert.Equal(2, _w.Library.Saved.Count);
        Assert.Equal(EntryMatch.CurrentQuestion, _w.Store.Show(videos[0].ShowId)!.Matches[key].Question);
    }

    [Fact]
    public async Task A_later_episode_in_the_library_has_the_entries_asked_again_but_not_an_entry_chosen_by_hand()
    {
        var videos = _w.FateZero();
        await _w.RunAsync(_w.JobFor(videos[0]), Token);
        await _w.RunAsync(_w.JobFor(videos[1]), Token);
        var fourth = _w.Library.AddEpisode(videos[0].ShowId, "Fate/Zero", 1, 4, "Episode 4");
        _w.Source.AddFile("[Group] Fate Zero - 04 [BD].ja.srt", "384");
        var key = FileFinder.MatchKey("jimaku", fourth, "ja");

        await _w.RunAsync(_w.JobFor(fourth), Token);
        var searches = _w.Source.Searches.Count;
        var upTo = _w.Store.Show(videos[0].ShowId)!.Matches[key].UpTo;
        _w.Finder.Pin(fourth, "ja", _w.Source.Entries[0], "admin");
        var fifth = _w.Library.AddEpisode(videos[0].ShowId, "Fate/Zero", 1, 5, "Episode 5");
        await _w.RunAsync(_w.JobFor(fifth), Token);

        Assert.Equal(2, searches);
        Assert.Equal(4, upTo);
        Assert.Equal(2, _w.Source.Searches.Count);
        Assert.Equal("user", _w.Store.Show(videos[0].ShowId)!.Matches[key].By);
    }
}
