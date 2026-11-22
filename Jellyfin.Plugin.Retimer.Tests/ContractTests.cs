using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Jobs;
using Jellyfin.Plugin.Retimer.Library;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// The plugin's answers are read by its own page, so the names on both sides have to agree.
/// </summary>
public partial class ContractTests
{
    private static string Script()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(Plugin.UserScriptResource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The capitalised fields read from a variable, such as job.StepText. A lower-case
    // name after the dot is JavaScript's own (file.Name.length) and is left out.
    private static string[] FieldsReadFrom(string script, string variable) =>
        Regex.Matches(script, @"(?<![\w.])" + Regex.Escape(variable) + @"\.([A-Z][A-Za-z]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

    private static HashSet<string> Properties(params Type[] types) =>
        types.SelectMany(t => t.GetProperties()).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Answers_use_Jellyfins_naming()
    {
        // These are the options Jellyfin's server writes its answers with.
        var json = JsonSerializer.Serialize(
            new JobView { Id = "a", ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555"), State = JobState.Saved, StepText = "Saving", CostUsd = 0.02 },
            JsonDefaults.PascalCaseOptions);

        Assert.Contains("\"State\":\"saved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"StepText\":\"Saving\"", json, StringComparison.Ordinal);
        Assert.Contains("\"CostUsd\":0.02", json, StringComparison.Ordinal);
        Assert.Contains("\"ItemId\":\"11111111222233334444555555555555\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stepText", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_reads_only_fields_the_answers_have()
    {
        var script = Script();
        var cases = new (string Variable, HashSet<string> Known)[]
        {
            ("status", Properties(typeof(StatusView))),
            ("usage", Properties(typeof(UsageToday))),
            ("job", Properties(typeof(JobView))),
            ("episode", Properties(typeof(EpisodeView))),
            ("season", Properties(typeof(SeasonView))),
            ("data", Properties(typeof(ShowDetailView), typeof(FilesView))),
            ("file", Properties(typeof(FileView))),
            ("entry", Properties(typeof(EntryView))),
            ("library", Properties(typeof(LibraryInfo))),
            ("s", Properties(typeof(SettingsView), typeof(ShowView), typeof(SourceReportView), typeof(SeasonView), typeof(SourceView))),
            ("l", Properties(typeof(LibraryInfo), typeof(LanguageView), typeof(LibraryChoice))),
            ("m", Properties(typeof(ModelEntry))),
            ("t", Properties(typeof(TriedView))),
            ("e", Properties(typeof(EpisodeView), typeof(EntryView))),
            ("f", Properties(typeof(FileView))),
            ("x", Properties(typeof(SourceView), typeof(ModelEntry))),
            ("w", Properties(typeof(WatchView))),
            ("run", Properties(typeof(WatchRunView))),
            ("ws", Properties(typeof(WatchedShowView))),
            ("wv", Properties(typeof(WatchedVideoView)))
        };

        foreach (var (variable, known) in cases)
        {
            var fields = FieldsReadFrom(script, variable);
            Assert.All(fields, field => Assert.True(known.Contains(field), $"The page reads {variable}.{field}, which no answer has."));
        }

        // The names the checks above stand on are really in use.
        Assert.NotEmpty(FieldsReadFrom(script, "status"));
        Assert.NotEmpty(FieldsReadFrom(script, "job"));
        Assert.NotEmpty(FieldsReadFrom(script, "episode"));
        Assert.NotEmpty(FieldsReadFrom(script, "file"));
        Assert.NotEmpty(FieldsReadFrom(script, "w"));
        Assert.NotEmpty(FieldsReadFrom(script, "ws"));
        Assert.NotEmpty(FieldsReadFrom(script, "wv"));
    }

    [Fact]
    public void The_page_reads_only_answer_fields_that_some_answer_has()
    {
        var known = Properties(typeof(StartView), typeof(JobsView), typeof(ConnectionTestResponse));

        Assert.All(FieldsReadFrom(Script(), "answer"), field => Assert.True(known.Contains(field), $"The page reads answer.{field}, which no answer has."));
    }

    [Fact]
    public void The_settings_screen_sends_only_settings_that_exist()
    {
        var script = Script();
        var start = script.IndexOf("function collect()", StringComparison.Ordinal);
        var end = script.IndexOf("return update;", start, StringComparison.Ordinal);
        var sent = Regex.Matches(script[start..end], @"^\s+([A-Z][A-Za-z]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(script[start..end], @"'([A-Z][A-Za-z]+)'").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        var update = Properties(typeof(SettingsUpdate));
        Assert.True(sent.Count > 20);
        Assert.All(sent, name => Assert.Contains(name, update));

        // Every setting can be changed from the page, and every key can be typed there.
        var settings = typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name);
        Assert.All(settings, name => Assert.Contains(name, sent));
        Assert.All(settings, name => Assert.Contains(name, update));
    }

    [Fact]
    public void The_requests_the_page_sends_use_names_the_endpoints_read()
    {
        var script = Script();

        foreach (var (pattern, type) in new[]
        {
            (@"start\(\{([^}]+)\}", typeof(StartJobsRequest)),
            (@"/Language', \{ method: 'PUT', body: \{([^}]+)\}", typeof(SetLanguageRequest)),
            (@"/Entry', \{ method: 'PUT', body: \{([^}]+)\}", typeof(PinEntryRequest)),
            (@"/Watch', \{ method: 'PUT', body: \{([^}]+)\}", typeof(WatchRequest)),
            (@"/Retimer/Test/' \+ target, \{ method: 'POST', body: \{([^}]+)\}", typeof(ModelTestRequest))
        })
        {
            var matches = Regex.Matches(script, pattern);
            Assert.NotEmpty(matches);
            var names = matches.SelectMany(m => Regex.Matches(m.Groups[1].Value, @"([A-Z][A-Za-z]+):").Select(n => n.Groups[1].Value)).Distinct().ToList();
            Assert.NotEmpty(names);
            Assert.All(names, name => Assert.Contains(name, Properties(type)));
        }
    }

    [Fact]
    public void The_page_calls_only_endpoints_that_exist()
    {
        var called = ApiPath().Matches(Script()).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();
        var routes = new[] { typeof(RetimerController), typeof(RetimerSettingsController) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
            .Select(a => "/Retimer/" + (a.Template ?? string.Empty))
            .ToList();

        Assert.Equal(
            [
                "/Retimer/Items/", "/Retimer/Jobs", "/Retimer/Jobs/", "/Retimer/Jobs?since=", "/Retimer/Libraries", "/Retimer/Libraries/",
                "/Retimer/Settings", "/Retimer/Shows/", "/Retimer/Status", "/Retimer/Test/", "/Retimer/Watch", "/Retimer/Watch/Check"
            ],
            called);

        // Each address the page builds starts like a route of a controller.
        foreach (var address in called)
        {
            var stem = address.Split('?')[0];
            Assert.Contains(routes, route => route == stem || (stem.EndsWith('/') && route.StartsWith(stem + "{", StringComparison.Ordinal)));
        }

        // And the tails the page adds to an id are routes too.
        foreach (var tail in new[] { "/Shows", "/Language", "/Retry", "/Files", "/Entries", "/Entry", "/Subtitle", "/Watch" })
        {
            Assert.Contains("'" + tail, Script(), StringComparison.Ordinal);
            Assert.Contains(routes, route => route.EndsWith("}" + tail, StringComparison.Ordinal));
        }
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

    [GeneratedRegex(@"'(/Retimer/[A-Za-z/?=]*)'")]
    private static partial Regex ApiPath();
}
