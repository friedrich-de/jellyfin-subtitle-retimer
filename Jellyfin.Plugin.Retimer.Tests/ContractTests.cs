using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Jimaku;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// The plugin's answers are read by its own pages, so the names on both sides have to agree.
/// </summary>
public partial class ContractTests
{
    private static string Page(string resource)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Answers_use_Jellyfins_naming_and_not_Jimakus()
    {
        var entry = new JimakuEntry
        {
            Id = 4321,
            Name = "Sousou no Frieren",
            EnglishName = "Frieren",
            JapaneseName = "葬送のフリーレン",
            AnilistId = 154587,
            TmdbId = "tv:209867",
            LastModified = new DateTimeOffset(2024, 3, 22, 15, 4, 5, TimeSpan.Zero),
            Flags = new JimakuEntryFlags { Anime = true }
        };

        // These are the options Jellyfin's server writes its answers with.
        var json = JsonSerializer.Serialize(entry, JsonDefaults.PascalCaseOptions);

        Assert.Contains("\"Id\":4321", json, StringComparison.Ordinal);
        Assert.Contains("\"JapaneseName\":", json, StringComparison.Ordinal);
        Assert.Contains("\"EnglishName\":\"Frieren\"", json, StringComparison.Ordinal);
        Assert.Contains("\"AnilistId\":154587", json, StringComparison.Ordinal);
        Assert.Contains("\"TmdbId\":\"tv:209867\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Flags\":{\"Anime\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("anilist_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("english_name", json, StringComparison.Ordinal);

        var file = JsonSerializer.Serialize(new JimakuFile { Name = "03.srt", Url = "https://jimaku.cc/x", Size = 5 }, JsonDefaults.PascalCaseOptions);
        Assert.Contains("\"Name\":\"03.srt\"", file, StringComparison.Ordinal);
        Assert.Contains("\"Url\":\"https://jimaku.cc/x\"", file, StringComparison.Ordinal);
        Assert.Contains("\"Size\":5", file, StringComparison.Ordinal);
        Assert.Contains("\"LastModified\":", file, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("entry", typeof(JimakuEntry))]
    [InlineData("entry.Flags", typeof(JimakuEntryFlags))]
    [InlineData("file", typeof(JimakuFile))]
    [InlineData("status", typeof(StatusResponse))]
    public void The_user_page_reads_only_fields_the_answers_have(string variable, Type answer)
    {
        var script = Page(Plugin.UserPageResource);
        var fields = FieldsReadFrom(script, variable);

        Assert.NotEmpty(fields);
        var properties = answer.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(fields, field => Assert.Contains(field, properties));
    }

    [Fact]
    public void The_settings_page_reads_only_fields_the_check_answers_have()
    {
        var fields = FieldsReadFrom(Page(Plugin.ConfigPageResource), "result");

        Assert.Equal(["Message", "Ok"], fields.Order());
        var properties = typeof(ConnectionTestResponse).GetProperties().Select(p => p.Name).ToList();
        Assert.All(fields, field => Assert.Contains(field, properties));
    }

    [Fact]
    public void The_user_page_calls_only_endpoints_that_exist()
    {
        var script = Page(Plugin.UserPageResource);
        var called = ApiPath().Matches(script).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();

        // Entries with a query, Entries/<id>/Files, and Status.
        Assert.Equal(["/Retimer/Jimaku/Entries/", "/Retimer/Jimaku/Entries?", "/Retimer/Status"], called);
        Assert.Contains("+ '/Files'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("ftp://example.org/api")]
    public void Without_a_usable_override_Jimaku_is_called_at_its_own_address(string? value)
    {
        Assert.Equal("https://jimaku.cc/api/", JimakuClient.ResolveBaseAddress(value).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/jimaku/api", "http://127.0.0.1:5000/jimaku/api/")]
    [InlineData(" https://example.org/api/ ", "https://example.org/api/")]
    public void A_test_can_point_the_client_at_a_stand_in(string value, string expected)
    {
        Assert.Equal(expected, JimakuClient.ResolveBaseAddress(value).AbsoluteUri);
    }

    // The capitalised fields read from a variable, such as entry.JapaneseName. A lower-case
    // name after the dot is JavaScript's own (file.Url.length) and is left out.
    private static string[] FieldsReadFrom(string script, string variable) =>
        Regex.Matches(script, @"(?<![\w.])" + Regex.Escape(variable) + @"\.([A-Z][A-Za-z]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

    [GeneratedRegex(@"'(/Retimer/[A-Za-z/?]+)'")]
    private static partial Regex ApiPath();
}
