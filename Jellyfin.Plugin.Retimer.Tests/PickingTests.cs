using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Sources;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// The rules that read file names and text. The names are real ones from Jimaku's entry for Fate/Zero.
/// </summary>
public class PickingTests
{
    private static readonly string[] _episodeFive =
    [
        "Fate Zero -05- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja-en.ass",
        "Fate Zero -05- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass",
        "Fate／Zero フェイト／ゼロ.S01E05.凶獣咆吼.WEBRip.Netflix.ja[cc].srt",
        "[Beatrice-Raws] Fate Zero Part I - 05 [BDRip 1920x1080 HEVC TrueHD]_rev.sup.7z",
        "[Kamigami] Fate Zero - 05 [BD 1920x1080 x264 AAC].Jap&furigana.ass",
        "[Kamigami] Fate Zero - 05 [BD 1920x1080 x264(10bit) FLAC PGS][CHS, JPN].ass",
        "[Tenrai-Sensei] Fate Zero E05 [BD][1080p][HEVC 10bit x265].srt",
        "[UTW] Fate Zero - 05 [BD][h264-720p AC3][F82B7255].ass"
    ];

    private static readonly string[] _otherEpisodes =
    [
        "Fate Zero -04- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass",
        "[UTW] Fate Zero - 11 [BD][h264-720p AC3][5D482629].ass",
        "[Tenrai-Sensei] Fate Zero E13 [BD][1080p][HEVC 10bit x265].srt",
        "Fate／Zero フェイト／ゼロ.S01E06.謀略の夜.WEBRip.Netflix.ja[cc].srt"
    ];

    private static VideoQuery Episode(int season, int episode, int? absolute = null, string? videoFile = null) => new()
    {
        Title = "Fate/Zero",
        Season = season,
        Episode = episode,
        AbsoluteEpisode = absolute ?? episode,
        VideoFileName = videoFile,
        Language = "ja"
    };

    private static List<SourceFile> Files(IEnumerable<string> names) =>
        names.Select(name => new SourceFile { Source = "jimaku", Id = name, EntryId = "384", Name = name }).ToList();

    [Theory]
    [InlineData("Fate Zero -05- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass", null, 5)]
    [InlineData("Fate／Zero フェイト／ゼロ.S01E05.凶獣咆吼.WEBRip.Netflix.ja[cc].srt", 1, 5)]
    [InlineData("[Kamigami] Fate Zero - 05 [BD 1920x1080 x264 AAC].Jap&furigana.ass", null, 5)]
    [InlineData("[Tenrai-Sensei] Fate Zero E05 [BD][1080p][HEVC 10bit x265].srt", null, 5)]
    [InlineData("[UTW] Fate Zero - 11 [BD][h264-720p AC3][5D482629].ass", null, 11)]
    [InlineData("Fate Zero -14- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass", null, 14)]
    [InlineData("Show.S02E01.1080p.WEB.ja.srt", 2, 1)]
    [InlineData("Show 2x07.srt", 2, 7)]
    [InlineData("番組 第5話.srt", null, 5)]
    public void The_episode_is_read_from_a_real_file_name(string name, int? season, int episode)
    {
        var guess = FileNameParser.Parse(name);

        Assert.Equal(season, guess.Season);
        Assert.Equal(episode, guess.Episode);
        Assert.False(guess.IsArchive);
        Assert.False(guess.IsPicture);
    }

    [Fact]
    public void A_picture_size_or_a_codec_is_never_taken_for_the_episode()
    {
        Assert.Null(FileNameParser.Parse("Fate Zero 1080p x264.srt").Episode);
        Assert.Null(FileNameParser.Parse("Your Name [BD 1920x1080 HEVC].ja.ass").Episode);
    }

    [Fact]
    public void A_packed_picture_subtitle_and_a_pack_of_episodes_are_told_apart()
    {
        var picture = FileNameParser.Parse("[Beatrice-Raws] Fate Zero Part I - 05 [BDRip 1920x1080 HEVC TrueHD]_rev.sup.7z");
        var pack = FileNameParser.Parse("[Group] Fate Zero 01-13 [BD].zip");

        Assert.True(picture.IsPicture);
        Assert.True(picture.IsArchive);
        Assert.Equal(5, picture.Episode);
        Assert.Equal("Beatrice-Raws", picture.Group);
        Assert.True(pack.IsArchive);
        Assert.False(pack.IsPicture);
        Assert.True(pack.HasRange);
        Assert.Equal((1, 13), (pack.Episode, pack.EpisodeEnd));
    }

    [Fact]
    public void Only_files_for_the_episode_are_ranked_and_a_plain_Japanese_text_file_comes_first()
    {
        var ranked = RulesPicker.Rank(Episode(1, 5), Files(_episodeFive.Concat(_otherEpisodes)), new PickOptions());

        Assert.All(ranked, candidate => Assert.Contains(candidate.File.Name, _episodeFive));
        Assert.DoesNotContain(ranked, candidate => candidate.File.Name.EndsWith(".sup.7z", StringComparison.Ordinal));
        Assert.Equal(7, ranked.Count);
        Assert.All(ranked, candidate => Assert.True(candidate.Auto));

        var names = ranked.Select(candidate => candidate.File.Name).ToList();
        var plain = new[] { names.IndexOf(_episodeFive[1]), names.IndexOf(_episodeFive[6]), names.IndexOf(_episodeFive[7]) };
        var twoLanguages = new[] { names.IndexOf(_episodeFive[0]), names.IndexOf(_episodeFive[5]) };
        Assert.True(plain.Max() < twoLanguages.Min(), "files that carry a second language come after the plain ones: " + string.Join(" | ", names));
        Assert.True(names.IndexOf(_episodeFive[4]) > plain.Min(), "a file with furigana comes after a plain one");

        // The Netflix file names season and episode, the strongest match, and still comes after the plain ones.
        var forHearingImpaired = names.IndexOf(_episodeFive[2]);
        Assert.True(forHearingImpaired > plain.Max(), "a file for the hearing impaired comes after the plain ones: " + string.Join(" | ", names));
        Assert.True(forHearingImpaired < twoLanguages.Min(), "and before the ones with a second language");
    }

    [Theory]
    [InlineData("Fate／Zero フェイト／ゼロ.S01E05.凶獣咆吼.WEBRip.Netflix.ja[cc].srt", true)]
    [InlineData("Show.S01E02.1080p.WEB.SDH.srt", true)]
    [InlineData("Show - 02 (SDH).ass", true)]
    [InlineData("[UTW] Fate Zero - 05 [BD][h264-720p AC3][F82B7255].ass", false)]
    [InlineData("Accel World - 02.srt", false)]
    public void A_file_for_the_hearing_impaired_is_known_by_its_name(string name, bool expected)
    {
        Assert.Equal(expected, RulesPicker.IsHearingImpaired(new SourceFile { Source = "jimaku", Id = "1", Name = name }));
        Assert.True(RulesPicker.IsHearingImpaired(new SourceFile { Source = "opensubtitles", Id = "1", Name = "plain.srt", HearingImpaired = true }));
    }

    [Fact]
    public void A_file_from_the_same_release_as_the_video_is_tried_first()
    {
        var ranked = RulesPicker.Rank(Episode(1, 5, videoFile: "[UTW] Fate Zero - 05 [BD][h264-720p AC3][F82B7255].mkv"), Files(_episodeFive), new PickOptions());

        Assert.Equal("[UTW] Fate Zero - 05 [BD][h264-720p AC3][F82B7255].ass", ranked[0].File.Name);
    }

    [Fact]
    public void A_packed_picture_subtitle_is_listed_only_when_pictures_can_be_converted()
    {
        var ranked = RulesPicker.Rank(Episode(1, 5), Files(_episodeFive), new PickOptions { PicturesAllowed = true });

        Assert.Equal(8, ranked.Count);
        Assert.EndsWith(".sup.7z", ranked[^1].File.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_season_that_is_numbered_on_from_the_first_is_found_by_the_absolute_number()
    {
        var files = Files(["Fate Zero -14- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass", "Fate Zero -01- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass", "Fate Zero.S02E01.ja.srt"]);

        var second = RulesPicker.Rank(Episode(2, 1, absolute: 14), files, new PickOptions()).Select(c => c.File.Name).ToList();
        var first = RulesPicker.Rank(Episode(1, 1, absolute: 1), files, new PickOptions()).Select(c => c.File.Name).ToList();

        Assert.Contains("Fate Zero -14- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass", second);
        Assert.Contains("Fate Zero.S02E01.ja.srt", second);
        Assert.Equal("Fate Zero.S02E01.ja.srt", second[0]);
        Assert.DoesNotContain("Fate Zero -14- 720p BD Dual Audio HEVC [Patwari][AnimeKayo].ja.ass", first);
        Assert.DoesNotContain("Fate Zero.S02E01.ja.srt", first);
    }

    [Fact]
    public void Packs_the_plugin_cannot_open_are_left_out_and_a_zip_for_the_range_stays()
    {
        var ranked = RulesPicker.Rank(Episode(1, 5), Files(["Fate Zero 01-13.rar", "Fate Zero 01-13.7z", "Fate Zero 01-13.zip", "Fate Zero 14-25.zip"]), new PickOptions());

        Assert.Equal("Fate Zero 01-13.zip", Assert.Single(ranked).File.Name);
        Assert.False(RulesPicker.IsUsable(new SourceFile { Source = "s", Id = "x", Name = "Fate Zero 01-13.rar" }, new PickOptions()));
        Assert.True(RulesPicker.IsUsable(new SourceFile { Source = "s", Id = "x", Name = "Fate Zero 01-13.zip" }, new PickOptions()));
    }

    [Fact]
    public void The_right_file_is_picked_out_of_a_pack_and_an_unnumbered_one_only_when_it_is_alone()
    {
        var video = Episode(1, 5);

        Assert.Equal("sub/Fate Zero - 05.ass", RulesPicker.PickFromArchive(video, ["sub/Fate Zero - 04.ass", "sub/Fate Zero - 05.ass", "readme.txt"], new PickOptions()));
        Assert.Null(RulesPicker.PickFromArchive(video, ["Fate Zero - 04.ass", "Fate Zero - 06.ass"], new PickOptions()));
        Assert.Null(RulesPicker.PickFromArchive(video, ["opening.ass", "ending.ass"], new PickOptions()));
        Assert.Equal("subtitle.srt", RulesPicker.PickFromArchive(video, ["subtitle.srt"], new PickOptions()));
    }

    [Fact]
    public void The_entry_is_matched_by_id_for_the_first_season_and_by_the_season_in_its_name_after_that()
    {
        var entries = new List<SourceEntry>
        {
            new() { Source = "jimaku", Id = "5795", Name = "Fate/Zero Cafe", AnilistId = 20791 },
            new() { Source = "jimaku", Id = "5724", Name = "Fate/Zero 2nd Season", AnilistId = 11741 },
            new() { Source = "jimaku", Id = "384", Name = "Fate/Zero", AnilistId = 10087 }
        };

        var first = RulesPicker.PickEntry(new VideoQuery { Title = "Fate/Zero", Season = 1, Episode = 1, AnilistId = 10087, Language = "ja" }, entries);
        var second = RulesPicker.PickEntry(new VideoQuery { Title = "Fate/Zero", Season = 2, Episode = 1, AnilistId = 10087, Language = "ja" }, entries);
        var byTitle = RulesPicker.PickEntry(new VideoQuery { Title = "Fate Zero", Season = 1, Episode = 1, Language = "ja" }, entries);
        var stranger = RulesPicker.PickEntry(new VideoQuery { Title = "Sousou no Frieren", Season = 1, Episode = 1, Language = "ja" }, entries);

        Assert.Equal("384", first?.Id);
        Assert.Equal("5724", second?.Id);
        Assert.Equal("384", byTitle?.Id);
        Assert.Null(stranger);
    }

    [Theory]
    [InlineData("Fate/Zero 2nd Season", 2)]
    [InlineData("Mushoku Tensei Season 2", 2)]
    [InlineData("Spy x Family Part 2", 2)]
    [InlineData("Fate/Zero", null)]
    public void The_season_is_read_from_an_entrys_name(string name, int? season)
    {
        Assert.Equal(season, RulesPicker.SeasonIn(name));
    }

    [Fact]
    public void Japanese_text_passes_the_letter_check_and_other_text_does_not()
    {
        var japanese = SubtitleText.Lines("a.srt", Support.Subtitles.Japanese);
        var english = SubtitleText.Lines("a.srt", Support.Subtitles.English);
        string[] chinese = ["你好，今天天气很好。我们一起去公园散步吧，好不好？", "那边有一只猫，看起来非常可爱，我们过去看看吧。"];
        string[] both = ["こんにちは、今日はいい天気ですね。", "Hello, the weather is lovely today, is it not?", "そうですね。", "It is. Shall we go for a walk together in the park?"];

        Assert.True(ScriptCheck.Check(japanese, "ja").Fits);
        Assert.False(ScriptCheck.Check(english, "ja").Fits);
        Assert.Equal("a language in Latin letters", ScriptCheck.Check(english, "ja").Seen);
        Assert.Equal("Chinese", ScriptCheck.Check(chinese, "ja").Seen);
        Assert.StartsWith("Japanese mixed with", ScriptCheck.Check(both, "ja").Seen, StringComparison.Ordinal);
        Assert.True(ScriptCheck.Check(english, "en").Fits);
        Assert.False(ScriptCheck.Check(japanese, "en").Fits);
        Assert.False(ScriptCheck.Check(japanese, "zh").Fits);
        Assert.True(ScriptCheck.Check(chinese, "zh").Fits);
    }

    [Fact]
    public void Too_little_text_is_not_judged()
    {
        var verdict = ScriptCheck.Check(["OK"], "ja");

        Assert.True(verdict.Fits);
        Assert.False(verdict.Certain);
    }

    [Fact]
    public void Subtitle_text_is_read_from_srt_and_ass_without_the_styling()
    {
        const string Ass = "[Script Info]\nTitle: x\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\\an8\\fs40}こんにちは\\N元気ですか\n"
            + "Comment: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,not shown\n";

        var ass = SubtitleText.Lines("a.ass", Ass);
        var srt = SubtitleText.Lines("a.srt", "1\n00:00:01,000 --> 00:00:02,000\n<i>こんにちは</i>\n\n2\n00:00:03,000 --> 00:00:04,000\nさようなら\n");

        Assert.Contains(ass, line => line.Contains("こんにちは", StringComparison.Ordinal));
        Assert.DoesNotContain(ass, line => line.Contains("\\an8", StringComparison.Ordinal) || line.Contains("not shown", StringComparison.Ordinal) || line.Contains("Title", StringComparison.Ordinal));
        Assert.Equal(2, srt.Count);
        Assert.DoesNotContain(srt, line => line.Contains("-->", StringComparison.Ordinal) || line.Contains("<i>", StringComparison.Ordinal));
    }

    [Fact]
    public void Old_Japanese_files_in_Shift_JIS_are_read()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(932).GetBytes("1\n00:00:01,000 --> 00:00:02,000\nこんにちは、今日はいい天気ですね。\n");

        Assert.Contains("こんにちは", SubtitleText.Decode(bytes), StringComparison.Ordinal);
        Assert.Contains("こんにちは", SubtitleText.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("こんにちは")]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sample_for_the_model_is_short()
    {
        var lines = Enumerable.Range(0, 500).Select(i => "これは字幕の行です。番号は" + i).ToList();

        var sample = SubtitleText.Sample(lines);

        Assert.True(sample.Length <= 2500);
        Assert.True(sample.Split('\n').Length <= 46);
    }

    [Theory]
    [InlineData("jpn", "ja", true)]
    [InlineData("ja", "ja", true)]
    [InlineData("Japanese", "ja", true)]
    [InlineData("eng", "ja", false)]
    [InlineData("eng", "en", true)]
    [InlineData("chi", "zh", true)]
    [InlineData("zho", "zh", true)]
    [InlineData(null, "ja", false)]
    [InlineData("und", "ja", false)]
    public void A_tracks_language_tag_is_matched_to_the_wanted_language(string? tag, string wanted, bool matches)
    {
        Assert.Equal(matches, Languages.Matches(tag, wanted));
    }

    [Fact]
    public void Languages_have_a_code_and_a_name_and_Japanese_is_the_default()
    {
        Assert.Equal("ja", Languages.Default);
        Assert.Equal("ja", Languages.All[0].Code);
        Assert.Equal("en", Languages.Normalize("eng"));
        Assert.Equal("English", Languages.NameOf("en"));
        Assert.Null(Languages.Normalize("klingon"));
        Assert.Equal(Languages.All.Count, Languages.All.Select(l => l.Code).Distinct().Count());
    }

    [Fact]
    public void Only_subtitle_files_are_read_out_of_a_zip_and_nothing_outside_it_is_named()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "subs/Show - 01.srt", "subs/Show - 02.ass", "readme.txt", "cover.jpg" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("content of " + name);
            }
        }

        var zip = buffer.ToArray();

        Assert.True(ArchiveReader.IsZip("pack.zip", zip));
        Assert.False(ArchiveReader.IsZip("pack.zip", Encoding.UTF8.GetBytes("not a zip")));
        Assert.Equal(["subs/Show - 01.srt", "subs/Show - 02.ass"], ArchiveReader.ListSubtitles(zip).Order());
        var file = ArchiveReader.Read(zip, "subs/Show - 02.ass");
        Assert.Equal("Show - 02.ass", file!.Name);
        Assert.Equal("content of subs/Show - 02.ass", Encoding.UTF8.GetString(file.Content));
        Assert.Null(ArchiveReader.Read(zip, "missing.srt"));
    }
}
