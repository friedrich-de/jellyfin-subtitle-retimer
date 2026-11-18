using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// What a look at a subtitle's letters says about its language.
/// </summary>
/// <param name="Fits">Whether the letters fit the wanted language.</param>
/// <param name="Seen">What the text looks like instead, such as "Chinese", when it does not fit.</param>
/// <param name="Certain">Whether the letters settle the language. Latin letters alone do not tell English from French.</param>
internal sealed record ScriptVerdict(bool Fits, string? Seen, bool Certain);

/// <summary>
/// Checks a subtitle's language by its letters alone, without a model.
/// </summary>
internal static class ScriptCheck
{
    public static ScriptVerdict Check(IReadOnlyList<string> lines, string wantedCode)
    {
        var wanted = Languages.Find(wantedCode);
        var counts = Count(lines);
        var total = counts.Values.Sum();
        if (wanted is null || total < 40)
        {
            // Too little text to judge: do not reject a file over it.
            return new ScriptVerdict(true, null, false);
        }

        double Share(params Script[] scripts) => scripts.Sum(s => counts.GetValueOrDefault(s)) / (double)total;

        var kana = Share(Script.Japanese);
        var han = Share(Script.Chinese);
        var main = counts.OrderByDescending(pair => pair.Value).First().Key;

        switch (wanted.Script)
        {
            case Script.Japanese:
                if (kana >= 0.15 && kana + han >= 0.6)
                {
                    return new ScriptVerdict(true, null, true);
                }

                if (kana >= 0.15)
                {
                    // Japanese next to a second language, such as a file with Japanese and English lines.
                    return new ScriptVerdict(false, "Japanese mixed with " + Describe(MainBeside(counts, Script.Japanese, Script.Chinese)), true);
                }

                return new ScriptVerdict(false, Describe(han > 0.5 ? Script.Chinese : main), true);

            case Script.Chinese:
                if (han >= 0.6 && kana < 0.05)
                {
                    return new ScriptVerdict(true, null, true);
                }

                return new ScriptVerdict(false, kana >= 0.05 ? "Japanese" : Describe(main), true);

            case Script.Latin:
                return Share(Script.Latin) >= 0.7
                    ? new ScriptVerdict(true, null, false)
                    : new ScriptVerdict(false, Describe(kana >= 0.1 ? Script.Japanese : main), true);

            default:
                return Share(wanted.Script) >= 0.6
                    ? new ScriptVerdict(true, null, true)
                    : new ScriptVerdict(false, Describe(kana >= 0.1 ? Script.Japanese : main), true);
        }
    }

    private static Script MainBeside(Dictionary<Script, int> counts, params Script[] leaveOut) =>
        counts.Where(pair => !leaveOut.Contains(pair.Key)).OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault(Script.Latin);

    private static string Describe(Script script) => script switch
    {
        Script.Latin => "a language in Latin letters",
        Script.Cyrillic => "a language in Cyrillic letters",
        Script.Devanagari => "Hindi",
        _ => script.ToString()
    };

    private static Dictionary<Script, int> Count(IReadOnlyList<string> lines)
    {
        var counts = new Dictionary<Script, int>();
        foreach (var line in lines)
        {
            foreach (var c in line)
            {
                Script? script = c switch
                {
                    >= '぀' and <= 'ヿ' => Script.Japanese,
                    >= 'ｦ' and <= 'ﾟ' => Script.Japanese,
                    >= '一' and <= '鿿' => Script.Chinese,
                    >= '㐀' and <= '䶿' => Script.Chinese,
                    >= '가' and <= '힯' => Script.Korean,
                    >= 'ᄀ' and <= 'ᇿ' => Script.Korean,
                    >= 'Ѐ' and <= 'ӿ' => Script.Cyrillic,
                    >= '؀' and <= 'ۿ' => Script.Arabic,
                    >= '֐' and <= '׿' => Script.Hebrew,
                    >= '฀' and <= '๿' => Script.Thai,
                    >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
                    >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
                    _ => char.IsLetter(c) && c < 'ɐ' ? Script.Latin : null
                };

                if (script is { } found)
                {
                    counts[found] = counts.GetValueOrDefault(found) + 1;
                }
            }
        }

        return counts;
    }
}
