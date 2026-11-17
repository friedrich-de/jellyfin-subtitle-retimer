using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// Jimaku writes a TMDB id as <c>tv:123</c> or <c>movie:123</c>.
/// </summary>
public static partial class JimakuTmdbId
{
    /// <summary>
    /// The id of a TMDB series.
    /// </summary>
    /// <param name="tmdbId">The numeric TMDB id.</param>
    /// <returns>The id in Jimaku's form.</returns>
    public static string Tv(int tmdbId) => string.Create(CultureInfo.InvariantCulture, $"tv:{tmdbId}");

    /// <summary>
    /// The id of a TMDB movie.
    /// </summary>
    /// <param name="tmdbId">The numeric TMDB id.</param>
    /// <returns>The id in Jimaku's form.</returns>
    public static string Movie(int tmdbId) => string.Create(CultureInfo.InvariantCulture, $"movie:{tmdbId}");

    /// <summary>
    /// Whether a string is a TMDB id in Jimaku's form.
    /// </summary>
    /// <param name="value">The string to check.</param>
    /// <returns><c>true</c> for <c>tv:123</c> or <c>movie:123</c>.</returns>
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex(@"\A(tv|movie):[0-9]{1,10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
