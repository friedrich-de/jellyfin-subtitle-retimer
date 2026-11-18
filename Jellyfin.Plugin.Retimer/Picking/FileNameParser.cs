using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// What a file name says about the file.
/// </summary>
/// <param name="Season">The season in the name, such as 2 in "S02E05".</param>
/// <param name="Episode">The episode in the name, or the first one of a range.</param>
/// <param name="EpisodeEnd">The last episode of a range such as "01-12".</param>
/// <param name="IsArchive">Whether the file packs other files.</param>
/// <param name="IsPicture">Whether the subtitle is pictures (PGS or VobSub), not text.</param>
/// <param name="Extension">The extension that says what the file is, such as ".srt" or ".zip".</param>
/// <param name="Group">The release group at the start of the name, such as "UTW" in "[UTW] Show - 01".</param>
internal sealed record FileGuess(int? Season, int? Episode, int? EpisodeEnd, bool IsArchive, bool IsPicture, string Extension, string? Group)
{
    public bool HasRange => EpisodeEnd is not null && Episode is not null && EpisodeEnd > Episode;
}

/// <summary>
/// Reads season and episode numbers out of file names, the way release names write them.
/// </summary>
internal static partial class FileNameParser
{
    private static readonly string[] _archiveExtensions = [".zip", ".7z", ".rar", ".gz", ".tar"];
    private static readonly string[] _pictureExtensions = [".sup", ".sub", ".idx"];

    // Numbers that are a picture size, a codec or a sound layout, never an episode.
    private static readonly HashSet<int> _notEpisodes = [264, 265, 480, 540, 576, 720, 1080, 2160];

    public static FileGuess Parse(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var isArchive = _archiveExtensions.Contains(extension);
        var stem = Path.GetFileNameWithoutExtension(name);

        // "Show 01.sup.7z" is a packed picture subtitle.
        var inner = Path.GetExtension(stem).ToLowerInvariant();
        var isPicture = _pictureExtensions.Contains(extension) || (isArchive && _pictureExtensions.Contains(inner));
        if (isArchive && (inner.Length is > 1 and <= 5) && inner.Skip(1).All(char.IsLetter))
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }

        // A language tag at the end is not part of the title: "Show - 05.ja", "Show - 05.ja-en".
        stem = LanguageSuffix().Replace(stem, string.Empty);

        var group = LeadingGroup().Match(stem) is { Success: true } g ? g.Groups[1].Value.Trim() : null;

        int? season = null;
        int? episode = null;
        int? end = null;

        var seasonEpisode = SeasonEpisode().Match(stem);
        if (seasonEpisode.Success)
        {
            season = Number(seasonEpisode.Groups[1]);
            episode = Number(seasonEpisode.Groups[2]);
            end = seasonEpisode.Groups[3].Success ? Number(seasonEpisode.Groups[3]) : null;
            return new FileGuess(season, episode, end, isArchive, isPicture, extension, group);
        }

        var crossed = SeasonCrossEpisode().Match(stem);
        if (crossed.Success)
        {
            return new FileGuess(Number(crossed.Groups[1]), Number(crossed.Groups[2]), null, isArchive, isPicture, extension, group);
        }

        var seasonWord = SeasonWord().Match(stem);
        if (seasonWord.Success)
        {
            season = Number(seasonWord.Groups[1].Success ? seasonWord.Groups[1] : seasonWord.Groups[2]);
        }

        // A range names a pack of episodes: "(01-12)", "[1~25]", "01-12 (BD)".
        var range = Range().Match(stem);
        if (range.Success)
        {
            var first = Number(range.Groups[1]);
            var last = Number(range.Groups[2]);
            if (first is not null && last is not null && last > first && !_notEpisodes.Contains(last.Value) && (isArchive || range.Value.StartsWith('[') || range.Value.StartsWith('(')))
            {
                return new FileGuess(season, first, last, isArchive, isPicture, extension, group);
            }
        }

        foreach (var pattern in new[] { EpisodeWord(), JapaneseEpisode(), Dashed(), DashPair(), Hash() })
        {
            var match = pattern.Match(stem);
            if (match.Success && Number(match.Groups[1]) is { } found && !_notEpisodes.Contains(found))
            {
                return new FileGuess(season, found, null, isArchive, isPicture, extension, group);
            }
        }

        // Last resort: a short number that stands alone, outside brackets. The last one wins,
        // since a number in the title comes before the episode ("Mob Psycho 100 03").
        var bare = Brackets().Replace(stem, " ");
        foreach (var token in bare.Split([' ', '.', '_', '-', '　'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (BareNumber().IsMatch(token)
                && int.TryParse(VersionSuffix().Replace(token, string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number is > 0 and < 1900
                && !_notEpisodes.Contains(number))
            {
                episode = number;
                break;
            }
        }

        return new FileGuess(season, episode, null, isArchive, isPicture, extension, group);
    }

    private static int? Number(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    [GeneratedRegex(@"(?i)(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,4})(?:\s*-\s*e?(\d{1,4}))?(?![0-9])")]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"(?<![0-9a-zA-Z])(\d{1,2})x(\d{2,3})(?![0-9])")]
    private static partial Regex SeasonCrossEpisode();

    [GeneratedRegex(@"(?i)(?<![a-z])season[ ._-]?(\d{1,2})(?![0-9])|(?<![0-9])(\d)(?:st|nd|rd|th)[ ._-]season")]
    private static partial Regex SeasonWord();

    [GeneratedRegex(@"[\[(]?(?<![0-9a-zA-Z.])(\d{1,3})\s*[-~～]\s*(\d{1,3})(?![0-9a-zA-Z])")]
    private static partial Regex Range();

    [GeneratedRegex(@"(?i)(?<![a-z])(?:episode|ep|e)[ ._-]?(\d{1,4})(?:v\d)?(?![0-9a-z])")]
    private static partial Regex EpisodeWord();

    [GeneratedRegex(@"第\s*(\d{1,4})\s*[話话集回]")]
    private static partial Regex JapaneseEpisode();

    [GeneratedRegex(@"[\s_]-[\s_]*(\d{1,4})(?:v\d)?(?![0-9a-zA-Z])")]
    private static partial Regex Dashed();

    [GeneratedRegex(@"-(\d{1,4})(?:v\d)?-")]
    private static partial Regex DashPair();

    [GeneratedRegex(@"#(\d{1,4})(?![0-9])")]
    private static partial Regex Hash();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|（[^）]*）|【[^】]*】")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^\d{1,4}(?:v\d)?$")]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"v\d$")]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"^\s*\[([^\]]{1,40})\]")]
    private static partial Regex LeadingGroup();

    [GeneratedRegex(@"(?i)(?:[._](?:[a-z]{2,3}(?:-[a-z]{2,4})?)(?:\[[a-z]{2,6}\])?){1,2}$")]
    private static partial Regex LanguageSuffix();
}
