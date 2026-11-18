using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// How a language is written, as far as a look at the letters can tell.
/// </summary>
internal enum Script
{
    Latin,
    Japanese,
    Chinese,
    Korean,
    Cyrillic,
    Arabic,
    Hebrew,
    Thai,
    Greek,
    Devanagari
}

/// <summary>
/// One subtitle language the plugin can ask the sources for.
/// </summary>
internal sealed record LanguageInfo(string Code, string Name, Script Script, string[] OtherCodes);

/// <summary>
/// The languages the page offers, and the many ways files and video tracks name them.
/// </summary>
internal static class Languages
{
    public const string Default = "ja";

    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("ja", "Japanese", Script.Japanese, ["jpn", "jap", "jp"]),
        new("en", "English", Script.Latin, ["eng"]),
        new("zh", "Chinese", Script.Chinese, ["chi", "zho", "chs", "cht", "cmn", "yue", "zh-cn", "zh-tw", "zh-hans", "zh-hant", "zh-hk"]),
        new("ko", "Korean", Script.Korean, ["kor"]),
        new("es", "Spanish", Script.Latin, ["spa", "es-419", "es-es", "es-mx"]),
        new("fr", "French", Script.Latin, ["fre", "fra", "fr-fr", "fr-ca"]),
        new("de", "German", Script.Latin, ["ger", "deu"]),
        new("it", "Italian", Script.Latin, ["ita"]),
        new("pt", "Portuguese", Script.Latin, ["por", "pob", "pt-br", "pt-pt"]),
        new("ru", "Russian", Script.Cyrillic, ["rus"]),
        new("ar", "Arabic", Script.Arabic, ["ara"]),
        new("nl", "Dutch", Script.Latin, ["dut", "nld"]),
        new("pl", "Polish", Script.Latin, ["pol"]),
        new("tr", "Turkish", Script.Latin, ["tur"]),
        new("sv", "Swedish", Script.Latin, ["swe"]),
        new("da", "Danish", Script.Latin, ["dan"]),
        new("nb", "Norwegian", Script.Latin, ["nor", "nob", "no"]),
        new("fi", "Finnish", Script.Latin, ["fin"]),
        new("cs", "Czech", Script.Latin, ["cze", "ces"]),
        new("hu", "Hungarian", Script.Latin, ["hun"]),
        new("ro", "Romanian", Script.Latin, ["rum", "ron"]),
        new("el", "Greek", Script.Greek, ["gre", "ell"]),
        new("he", "Hebrew", Script.Hebrew, ["heb", "iw"]),
        new("th", "Thai", Script.Thai, ["tha"]),
        new("vi", "Vietnamese", Script.Latin, ["vie"]),
        new("id", "Indonesian", Script.Latin, ["ind"]),
        new("uk", "Ukrainian", Script.Cyrillic, ["ukr"]),
        new("hi", "Hindi", Script.Devanagari, ["hin"])
    ];

    private static readonly Dictionary<string, LanguageInfo> _byAnyCode = BuildIndex();

    /// <summary>
    /// Finds a language by any of its codes or by its English name.
    /// </summary>
    public static LanguageInfo? Find(string? value)
    {
        var key = value?.Trim().ToLowerInvariant().Replace('_', '-');
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (_byAnyCode.TryGetValue(key, out var found))
        {
            return found;
        }

        // "pt-BR" or "en-US": the part before the dash decides.
        var dash = key.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && _byAnyCode.TryGetValue(key[..dash], out found) ? found : null;
    }

    /// <summary>
    /// The two-letter code of a known language, or <c>null</c>.
    /// </summary>
    public static string? Normalize(string? value) => Find(value)?.Code;

    /// <summary>
    /// The English name of a language, or the code itself when the plugin does not know it.
    /// </summary>
    public static string NameOf(string? code) => Find(code)?.Name ?? (string.IsNullOrWhiteSpace(code) ? "Unknown" : code.Trim());

    /// <summary>
    /// Whether a video track's language tag means the wanted language.
    /// </summary>
    public static bool Matches(string? trackLanguage, string wantedCode)
    {
        var found = Find(trackLanguage);
        return found is not null && string.Equals(found.Code, Normalize(wantedCode), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every code a video track may carry for a language, the two-letter one first.
    /// </summary>
    public static IReadOnlyList<string> CodesOf(string code)
    {
        var info = Find(code);
        return info is null ? [code] : [info.Code, .. info.OtherCodes];
    }

    private static Dictionary<string, LanguageInfo> BuildIndex()
    {
        var index = new Dictionary<string, LanguageInfo>(StringComparer.Ordinal);
        foreach (var language in All)
        {
            index[language.Code] = language;
            index[language.Name.ToLowerInvariant()] = language;
            foreach (var other in language.OtherCodes)
            {
                index[other] = language;
            }
        }

        return index;
    }
}
