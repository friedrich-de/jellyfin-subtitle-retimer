using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Jellyfin.Plugin.Retimer.Picking;

namespace Jellyfin.Plugin.Retimer.Sources;

/// <summary>
/// Opens zip archives of subtitles. Other archive kinds are not opened.
/// </summary>
internal static class ArchiveReader
{
    /// <summary>The largest subtitle taken out of an archive, the same limit Retimer API has for a subtitle.</summary>
    internal const long MaxEntryBytes = 64L * 1024 * 1024;

    private const int MaxEntries = 2000;

    public static bool IsZip(string name, byte[] content) =>
        content.Length > 4 && content[0] == 0x50 && content[1] == 0x4B && (content[2] == 0x03 || content[2] == 0x05)
        && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !SubtitleText.IsTextSubtitle(name));

    /// <summary>
    /// The text subtitles in the archive, by their path inside it.
    /// </summary>
    public static IReadOnlyList<string> ListSubtitles(byte[] content)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            return archive.Entries
                .Take(MaxEntries)
                .Where(entry => entry.Length > 0 && entry.Length <= MaxEntryBytes && SubtitleText.IsTextSubtitle(entry.Name))
                .Select(entry => entry.FullName)
                .ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads one file out of the archive, or <c>null</c> when it is missing, too large or damaged.
    /// </summary>
    public static DownloadedFile? Read(byte[] content, string fullName)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, fullName, StringComparison.Ordinal));
            if (entry is null || entry.Length > MaxEntryBytes)
            {
                return null;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                // The size in the archive's index can lie, so the real size is counted too.
                if (buffer.Length + read > MaxEntryBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return new DownloadedFile(entry.Name, buffer.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
