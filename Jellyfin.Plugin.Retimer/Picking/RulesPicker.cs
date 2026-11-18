using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Retimer.Sources;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// A file that may belong to a video, with how well it fits and why.
/// </summary>
internal sealed record Candidate
{
    public required SourceFile File { get; init; }

    /// <summary>Gets how well the file fits by the rules. Higher is better.</summary>
    public int Score { get; init; }

    /// <summary>Gets one short sentence on why the file is in the list.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets who put the file first: the model's name, or <c>null</c> for the rules.</summary>
    public string? SuggestedBy { get; init; }

    /// <summary>Gets a value indicating whether the plugin may try the file by itself, without the user choosing it.</summary>
    public bool Auto { get; init; }
}

/// <summary>
/// What the rules need to know beyond the video itself.
/// </summary>
internal sealed record PickOptions
{
    public string PreferredSource { get; init; } = "jimaku";

    /// <summary>Gets a value indicating whether picture subtitles can be read into text.</summary>
    public bool PicturesAllowed { get; init; }
}

/// <summary>
/// Picks files without a model: by the numbers in their names and by what the source says about them.
/// </summary>
internal static class RulesPicker
{
    /// <summary>
    /// From this score on the rules trust a file enough to try it unasked: its name carries the
    /// episode, the source lists it for the video, or it is a pack that names the episode's range.
    /// </summary>
    internal const int AutoScore = 20;

    private static readonly string[] _secondLanguageMarks =
    [
        "ja-en", "en-ja", "jp-en", "jpn-eng", "eng-jpn", "jpn+eng", "ja+en", "chs", "cht", "sc&jp", "tc&jp", "chs&jpn", "cht&jpn",
        "bilingual", "dual sub", "dualsub", "双语", "简日", "繁日", "中日"
    ];

    private static readonly string[] _hearingImpairedMarks = ["[cc]", "(cc)", ".cc.", ".sdh", "[sdh]", "(sdh)", " sdh"];

    /// <summary>
    /// Ranks the files that may belong to the video, best first. Files that clearly belong to
    /// another episode, or that the plugin cannot open, are left out.
    /// </summary>
    public static IReadOnlyList<Candidate> Rank(VideoQuery video, IEnumerable<SourceFile> files, PickOptions options)
    {
        var videoGroup = video.VideoFileName is null ? null : FileNameParser.Parse(video.VideoFileName).Group;
        var ranked = new List<Candidate>();

        foreach (var file in files)
        {
            var guess = FileNameParser.Parse(file.Name);
            var reasons = new List<string>();
            var score = EpisodeScore(video, file, guess, reasons);
            if (score < 0)
            {
                continue;
            }

            if (guess.IsPicture)
            {
                // Only PGS can be read, bare or packed. VobSub cannot.
                if (!options.PicturesAllowed || !file.Name.Contains(".sup", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                score -= 30;
                reasons.Add("picture subtitle");
            }
            else if (guess.IsArchive)
            {
                // Only zip archives can be opened here.
                if (guess.Extension != ".zip")
                {
                    continue;
                }

                score -= 10;
            }
            else if (guess.Extension is ".srt" or ".ass" or ".ssa")
            {
                score += 10;
            }
            else if (guess.Extension == ".vtt")
            {
                score += 6;
            }
            else
            {
                continue;
            }

            var lower = file.Name.ToLowerInvariant();
            if (video.Language == "ja" && _secondLanguageMarks.Any(mark => lower.Contains(mark, StringComparison.Ordinal)))
            {
                score -= 30;
                reasons.Add("has a second language");
            }

            if (lower.Contains("furigana", StringComparison.Ordinal))
            {
                score -= 10;
                reasons.Add("with furigana");
            }

            if (file.MachineTranslated)
            {
                score -= 40;
                reasons.Add("machine translated");
            }

            // Enough to come after a plain file whose name carries the episode in the other form.
            // The page says so beside the file, so it is not repeated among the reasons.
            if (IsHearingImpaired(file))
            {
                score -= 12;
            }

            if (file.Trusted)
            {
                score += 4;
            }

            if (file.Downloads is > 0)
            {
                score += Math.Min(8, (int)Math.Round(Math.Log10(file.Downloads.Value + 1) * 2));
            }

            var releaseGroup = guess.Group ?? (file.Release is null ? null : FileNameParser.Parse(file.Release).Group);
            if (videoGroup is not null && string.Equals(videoGroup, releaseGroup, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
                reasons.Add("same release as the video");
            }
            else if (SameRelease(video.VideoFileName, file.Release ?? file.Name))
            {
                score += 8;
                reasons.Add("same kind of release as the video");
            }

            if (string.Equals(file.Source, options.PreferredSource, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (file.Size is > 0 and < 600)
            {
                score -= 40;
                reasons.Add("almost empty");
            }

            ranked.Add(new Candidate { File = file, Score = score, Reason = string.Join(" · ", reasons), Auto = score >= AutoScore });
        }

        return ranked
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.File.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether a file carries text for the hearing impaired, by what the source says or by its name.
    /// </summary>
    public static bool IsHearingImpaired(SourceFile file)
    {
        if (file.HearingImpaired)
        {
            return true;
        }

        var lower = file.Name.ToLowerInvariant();
        return _hearingImpairedMarks.Any(mark => lower.Contains(mark, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the plugin can do anything with a file of this kind: a text subtitle, a zip pack,
    /// or a PGS picture subtitle when those may be converted.
    /// </summary>
    public static bool IsUsable(SourceFile file, PickOptions options)
    {
        var guess = FileNameParser.Parse(file.Name);
        if (guess.IsPicture)
        {
            return options.PicturesAllowed && file.Name.Contains(".sup", StringComparison.OrdinalIgnoreCase);
        }

        return guess.IsArchive ? guess.Extension == ".zip" : SubtitleText.IsTextSubtitle(file.Name);
    }

    /// <summary>
    /// Picks the file for an episode out of an archive's file names.
    /// </summary>
    public static string? PickFromArchive(VideoQuery video, IReadOnlyList<string> names, PickOptions options)
    {
        var files = names.Select(name => new SourceFile { Source = "archive", Id = name, Name = name });
        var ranked = Rank(video, files, options with { PicturesAllowed = false });
        var best = ranked.FirstOrDefault(candidate => !FileNameParser.Parse(candidate.File.Name).IsArchive);

        // In a pack, a file without an episode number is not a safe pick unless it is the only subtitle.
        if (best is not null && best.Score < 50 && !video.IsMovie && names.Count(SubtitleText.IsTextSubtitle) > 1)
        {
            return null;
        }

        return best?.File.Name;
    }

    /// <summary>
    /// Picks an entry for a show by ids first, then by its names.
    /// </summary>
    public static SourceEntry? PickEntry(VideoQuery video, IReadOnlyList<SourceEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var wantedSeason = video.IsMovie ? null : video.Season;
        var titles = new[] { video.Title, video.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Squash(t!)).ToList();

        SourceEntry? best = null;
        var bestScore = 0;
        foreach (var entry in entries)
        {
            if (entry.IsMovie != video.IsMovie && entries.Any(other => other.IsMovie == video.IsMovie))
            {
                continue;
            }

            var names = new[] { entry.Name, entry.EnglishName, entry.JapaneseName }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            var entrySeason = names.Select(SeasonIn).FirstOrDefault(s => s is not null) ?? 1;
            var score = 0;

            // An AniList id names one season of an anime, so it only settles the first season.
            if (video.AnilistId is not null && entry.AnilistId == video.AnilistId && (wantedSeason is null or 1))
            {
                score += 100;
            }

            if (video.TmdbId is not null && entry.TmdbId is not null
                && entry.TmdbId.EndsWith(":" + video.TmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                score += 40;
            }

            var bare = names.Select(n => Squash(StripSeason(n))).ToList();
            if (titles.Any(bare.Contains))
            {
                score += 50;
            }
            else if (titles.Any(t => bare.Any(b => b.Length >= 4 && (b.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(b, StringComparison.Ordinal)))))
            {
                score += 15;
            }

            if (wantedSeason is not null)
            {
                score += entrySeason == wantedSeason ? 30 : -60;
            }

            if (score > bestScore)
            {
                best = entry;
                bestScore = score;
            }
        }

        return bestScore >= 50 ? best : null;
    }

    /// <summary>
    /// The season a title names, such as 2 for "Show 2nd Season" or "Show Season 2".
    /// </summary>
    internal static int? SeasonIn(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var (mark, season) in new (string, int)[]
        {
            ("2nd season", 2), ("second season", 2), ("season 2", 2), ("part 2", 2), ("part ii", 2), ("第2期", 2), ("2ndシーズン", 2), ("第二期", 2),
            ("3rd season", 3), ("third season", 3), ("season 3", 3), ("part 3", 3), ("part iii", 3), ("第3期", 3), ("3rdシーズン", 3), ("第三期", 3),
            ("4th season", 4), ("fourth season", 4), ("season 4", 4), ("第4期", 4),
            ("5th season", 5), ("season 5", 5), ("第5期", 5),
            ("6th season", 6), ("season 6", 6)
        })
        {
            if (lower.Contains(mark, StringComparison.Ordinal))
            {
                return season;
            }
        }

        return null;
    }

    private static string StripSeason(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var mark in new[] { "2nd season", "second season", "season 2", "3rd season", "third season", "season 3", "4th season", "season 4", "season 1", "1st season", "2ndシーズン", "3rdシーズン", "第2期", "第3期", "第二期", "第三期" })
        {
            lower = lower.Replace(mark, string.Empty, StringComparison.Ordinal);
        }

        return lower;
    }

    private static string Squash(string title) =>
        new(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int EpisodeScore(VideoQuery video, SourceFile file, FileGuess guess, List<string> reasons)
    {
        if (file.ForThisVideo)
        {
            reasons.Add(video.IsMovie ? "listed for this film" : "listed for this episode");
            return 95;
        }

        if (video.IsMovie)
        {
            return guess.IsArchive ? 30 : 60;
        }

        var episode = video.Episode;
        if (episode is null)
        {
            return 5;
        }

        if (guess.Season is not null && guess.Episode is not null && !guess.HasRange)
        {
            if (guess.Season == (video.Season ?? 1) && guess.Episode == episode)
            {
                reasons.Add($"season {guess.Season}, episode {guess.Episode} in the name");
                return 100;
            }

            return -1;
        }

        if (guess.HasRange)
        {
            var inRange = (episode >= guess.Episode && episode <= guess.EpisodeEnd)
                || (video.AbsoluteEpisode is { } absolute && absolute >= guess.Episode && absolute <= guess.EpisodeEnd);
            if (!inRange)
            {
                return -1;
            }

            reasons.Add($"pack of episodes {guess.Episode} to {guess.EpisodeEnd}");
            return 40;
        }

        if (guess.Episode is not null)
        {
            if (guess.Episode == episode && (guess.Season is null || guess.Season == (video.Season ?? 1)))
            {
                reasons.Add($"episode {episode} in the name");
                return 90;
            }

            if (video.Season is > 1 && video.AbsoluteEpisode == guess.Episode)
            {
                reasons.Add($"episode {guess.Episode}, counted from the first season");
                return 85;
            }

            return -1;
        }

        // No number in the name: a pack may hold the episode, a single file is a guess.
        if (guess.IsArchive)
        {
            reasons.Add("a pack that may hold the episode");
            return 20;
        }

        reasons.Add("no episode number in the name");
        return 5;
    }

    private static bool SameRelease(string? videoFileName, string subtitleName)
    {
        if (string.IsNullOrEmpty(videoFileName))
        {
            return false;
        }

        var video = Path.GetFileNameWithoutExtension(videoFileName).ToLowerInvariant();
        var subtitle = subtitleName.ToLowerInvariant();
        foreach (var kind in new[] { "bluray", "blu-ray", "bdrip", "[bd", "bd]", "web-dl", "webrip", "web", "dvd", "hdtv", "netflix", "amzn", "crunchyroll" })
        {
            if (video.Contains(kind, StringComparison.Ordinal) && subtitle.Contains(kind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
