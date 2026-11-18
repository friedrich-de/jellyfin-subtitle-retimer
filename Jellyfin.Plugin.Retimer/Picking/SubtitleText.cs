using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// Reads the spoken lines out of a subtitle file, enough to look at its language. It does not check times.
/// </summary>
internal static partial class SubtitleText
{
    /// <summary>The text formats the plugin saves.</summary>
    public static readonly IReadOnlyList<string> TextExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    /// <summary>The formats Retimer API takes as the subtitle to retime.</summary>
    public static readonly IReadOnlyList<string> RetimableExtensions = [".srt", ".ass", ".ssa"];

    public static bool IsTextSubtitle(string fileName) =>
        TextExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static bool IsRetimable(string fileName) =>
        RetimableExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// Turns the bytes of a subtitle file into text. Files without a byte order mark are read as UTF-8,
    /// and as Shift-JIS when that fails, which older Japanese files use.
    /// </summary>
    public static string Decode(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(932).GetString(content);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1.GetString(content);
            }
        }
    }

    /// <summary>
    /// The lines of text a viewer would read, without numbers, times, styles and tags.
    /// </summary>
    public static IReadOnlyList<string> Lines(string fileName, string text)
    {
        var lines = new List<string>();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var isAss = extension is ".ass" or ".ssa" || text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '﻿');
            if (line.Length == 0)
            {
                continue;
            }

            if (isAss)
            {
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Dialogue: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text
                var parts = line.Split(',', 10);
                if (parts.Length < 10)
                {
                    continue;
                }

                line = AssTag().Replace(parts[9], string.Empty).Replace("\\N", " ", StringComparison.OrdinalIgnoreCase).Replace("\\h", " ", StringComparison.Ordinal);

                // Drawing commands are pictures, not text.
                if (parts[9].Contains("\\p1", StringComparison.Ordinal) || parts[9].Contains("\\p2", StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else
            {
                if (line.Contains("-->", StringComparison.Ordinal)
                    || int.TryParse(line, out _)
                    || line.StartsWith("WEBVTT", StringComparison.Ordinal)
                    || line.StartsWith("NOTE", StringComparison.Ordinal))
                {
                    continue;
                }

                line = AssTag().Replace(HtmlTag().Replace(line, string.Empty), string.Empty);
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// A sample for a model: lines from the first, middle and last part of the file, since
    /// the first minutes are often a song or credits.
    /// </summary>
    public static string Sample(IReadOnlyList<string> lines, int maxLines = 45, int maxCharacters = 2400)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> picked = lines;
        if (lines.Count > maxLines)
        {
            var third = maxLines / 3;
            var middle = (lines.Count / 2) - (third / 2);
            picked = lines.Take(third).Concat(lines.Skip(middle).Take(third)).Concat(lines.Skip(lines.Count - third));
        }

        var builder = new StringBuilder();
        foreach (var line in picked)
        {
            if (builder.Length + line.Length + 1 > maxCharacters)
            {
                break;
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssTag();

    [GeneratedRegex(@"<[^>]{1,40}>")]
    private static partial Regex HtmlTag();
}
