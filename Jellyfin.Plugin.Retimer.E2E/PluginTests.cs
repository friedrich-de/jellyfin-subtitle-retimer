using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// Drives the packaged plugin inside a real Jellyfin server over HTTP, as three kinds of caller:
/// nobody, a normal user and an administrator.
/// </summary>
public class PluginTests(JellyfinServer server) : IClassFixture<JellyfinServer>
{
    private const string JimakuKey = "e2e-not-a-real-jimaku-key";

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private void ClearJimakuRequests()
    {
        lock (server.JimakuRequests)
        {
            server.JimakuRequests.Clear();
        }
    }

    private List<(string PathAndQuery, string? Authorization, string? UserAgent)> JimakuRequests()
    {
        lock (server.JimakuRequests)
        {
            return [.. server.JimakuRequests];
        }
    }

    [Fact]
    public async Task The_plugin_installed_from_a_repository_is_active()
    {
        JellyfinServer.RequireEnabled();

        var plugins = (await server.GetJsonAsync("Plugins", server.AdminToken))!.AsArray();
        var plugin = Assert.Single(plugins, p => Guid.Parse(p!["Id"]!.GetValue<string>()) == Guid.Parse(server.PluginGuid))!;

        Assert.Equal(server.PluginName, plugin["Name"]!.GetValue<string>());
        Assert.Equal(server.PluginVersion, plugin["Version"]!.GetValue<string>());
        Assert.Equal("Active", plugin["Status"]!.GetValue<string>());

        // Jellyfin unpacked the zip into its own folder and wrote the manifest's details next to it.
        var folder = Path.Combine(server.DataDirectory, "plugins", $"{server.PluginName}_{server.PluginVersion}");
        Assert.True(File.Exists(Path.Combine(folder, "Jellyfin.Plugin.Retimer.dll")), "The plugin's DLL is missing in " + folder);
        var meta = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "meta.json"), TestContext.Current.CancellationToken))!;
        Assert.Equal("Subtitles", meta["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_server_is_the_version_the_plugin_was_built_for()
    {
        JellyfinServer.RequireEnabled();

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(JellyfinServer.PackageDirectory!, "manifest.json"), TestContext.Current.CancellationToken))!;
        var targetAbi = Version.Parse(manifest[0]!["versions"]![0]!["targetAbi"]!.GetValue<string>());
        var running = Version.Parse(server.ServerVersion);

        Assert.Equal((targetAbi.Major, targetAbi.Minor), (running.Major, running.Minor));
    }

    [Fact]
    public async Task The_settings_page_is_served_and_listed_in_the_dashboard_menu()
    {
        JellyfinServer.RequireEnabled();

        using var page = await server.SendAsync(HttpMethod.Get, "web/ConfigurationPage?name=SubtitleRetimer", server.AdminToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await BodyAsync(page);
        Assert.Contains("id=\"SubtitleRetimerConfigForm\"", html, StringComparison.Ordinal);
        Assert.Contains(server.PluginGuid, html, StringComparison.OrdinalIgnoreCase);

        var pages = (await server.GetJsonAsync("web/ConfigurationPages?enableInMainMenu=true", server.AdminToken))!.AsArray();
        var listed = Assert.Single(pages, p => p!["Name"]!.GetValue<string>() == "SubtitleRetimer")!;
        Assert.Equal("Subtitle Retimer", listed["DisplayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_login_only_the_empty_page_answers()
    {
        JellyfinServer.RequireEnabled();

        using var page = await server.SendAsync(HttpMethod.Get, "Retimer");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<title>Subtitle Retimer</title>", await BodyAsync(page), StringComparison.Ordinal);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "Retimer/Status"),
            (HttpMethod.Get, "Retimer/Jimaku/Entries?query=frieren"),
            (HttpMethod.Get, "Retimer/Jimaku/Entries/1/Files"),
            (HttpMethod.Post, "Retimer/Test/Jimaku"),
            (HttpMethod.Post, "Retimer/Test/Retimer")
        })
        {
            using var response = await server.SendAsync(method, path);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)response.StatusCode} without a login");
        }
    }

    [Fact]
    public async Task A_normal_user_gets_the_status_but_no_settings_and_no_checks()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(JimakuKey, server.RetimerUrl, JellyfinServer.GoodRetimerKey);

        using var status = await server.SendAsync(HttpMethod.Get, "Retimer/Status", server.ViewerToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var text = await BodyAsync(status);
        var json = JsonNode.Parse(text)!;
        Assert.Equal(server.PluginVersion, json["Version"]!.GetValue<string>());
        Assert.True(json["JimakuConfigured"]!.GetValue<bool>());
        Assert.True(json["RetimerConfigured"]!.GetValue<bool>());
        Assert.False(json["IsAdministrator"]!.GetValue<bool>());

        // The plugin's own answer carries neither key nor the retimer's address.
        Assert.DoesNotContain(JimakuKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(JellyfinServer.GoodRetimerKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(server.HelperAddress.Authority, text, StringComparison.Ordinal);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, "Retimer/Test/Jimaku"),
            (HttpMethod.Post, "Retimer/Test/Retimer"),
            (HttpMethod.Get, $"Plugins/{server.PluginGuid}/Configuration"),
            (HttpMethod.Get, "web/ConfigurationPages")
        })
        {
            using var response = await server.SendAsync(method, path, server.ViewerToken);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {(int)response.StatusCode} to a normal user");
        }
    }

    [Fact]
    public async Task An_administrator_saves_settings_and_reads_them_back()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync("key & <one>", "https://retimer.example.org", "rt_two");

        var saved = (await server.GetJsonAsync($"Plugins/{server.PluginGuid}/Configuration", server.AdminToken))!;
        Assert.Equal("key & <one>", saved["JimakuApiKey"]!.GetValue<string>());
        Assert.Equal("https://retimer.example.org", saved["RetimerUrl"]!.GetValue<string>());
        Assert.Equal("rt_two", saved["RetimerApiKey"]!.GetValue<string>());

        var file = Path.Combine(server.DataDirectory, "plugins", "configurations", "Jellyfin.Plugin.Retimer.xml");
        Assert.Contains("<RetimerApiKey>rt_two</RetimerApiKey>", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var status = (await server.GetJsonAsync("Retimer/Status", server.AdminToken))!;
        Assert.True(status["IsAdministrator"]!.GetValue<bool>());
    }

    [Fact]
    public async Task With_nothing_set_up_a_search_says_what_is_missing()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(string.Empty, string.Empty, string.Empty);

        var status = (await server.GetJsonAsync("Retimer/Status", server.ViewerToken))!;
        Assert.False(status["JimakuConfigured"]!.GetValue<bool>());
        Assert.False(status["RetimerConfigured"]!.GetValue<bool>());

        using var search = await server.SendAsync(HttpMethod.Get, "Retimer/Jimaku/Entries?query=frieren", server.ViewerToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, search.StatusCode);
        var problem = JsonNode.Parse(await BodyAsync(search))!;
        Assert.Equal("NotConfigured", problem["title"]!.GetValue<string>());
        Assert.Contains("No Jimaku API key", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);

        using var bad = await server.SendAsync(HttpMethod.Get, "Retimer/Jimaku/Entries?tmdbId=209867", server.ViewerToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var check = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/Retimer", server.AdminToken)))!;
        Assert.Equal("NotConfigured", check["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_normal_user_finds_a_series_on_Jimaku_and_lists_its_files()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(JellyfinServer.GoodJimakuKey, string.Empty, string.Empty);
        ClearJimakuRequests();

        var entries = (await server.GetJsonAsync("Retimer/Jimaku/Entries?anilistId=154587", server.ViewerToken))!.AsArray();

        // The answer uses Jellyfin's naming, which is what the plugin's page reads.
        var entry = Assert.Single(entries)!;
        Assert.Equal(4321, entry["Id"]!.GetValue<long>());
        Assert.Equal("Sousou no Frieren", entry["Name"]!.GetValue<string>());
        Assert.Equal("葬送のフリーレン", entry["JapaneseName"]!.GetValue<string>());
        Assert.Equal("Frieren: Beyond Journey’s End", entry["EnglishName"]!.GetValue<string>());
        Assert.Equal(154587, entry["AnilistId"]!.GetValue<int>());
        Assert.Equal("tv:209867", entry["TmdbId"]!.GetValue<string>());
        Assert.True(entry["Flags"]!["Anime"]!.GetValue<bool>());
        Assert.False(entry["Flags"]!["Movie"]!.GetValue<bool>());
        Assert.StartsWith("2024-03-22T15:04:05", entry["LastModified"]!.GetValue<string>(), StringComparison.Ordinal);

        // What reached Jimaku: the bare key and a client name with the plugin's version.
        var request = Assert.Single(JimakuRequests());
        Assert.Equal("/jimaku/api/entries/search?anilist_id=154587", request.PathAndQuery);
        Assert.Equal(JellyfinServer.GoodJimakuKey, request.Authorization);
        Assert.Equal("jellyfin-plugin-retimer/" + server.PluginVersion, request.UserAgent);

        var all = (await server.GetJsonAsync("Retimer/Jimaku/Entries/4321/Files", server.ViewerToken))!.AsArray();
        Assert.Equal(2, all.Count);
        var file = Assert.Single((await server.GetJsonAsync("Retimer/Jimaku/Entries/4321/Files?episode=3", server.ViewerToken))!.AsArray())!;
        Assert.Equal("[Group] Sousou no Frieren - 03.ja.srt", file["Name"]!.GetValue<string>());
        Assert.Equal("https://jimaku.cc/entry/4321/download/x-03.srt", file["Url"]!.GetValue<string>());
        Assert.Equal(34567, file["Size"]!.GetValue<long>());
        Assert.Equal("/jimaku/api/entries/4321/files?episode=3", JimakuRequests().Last().PathAndQuery);

        // The key went to Jimaku and nowhere else.
        Assert.DoesNotContain(JellyfinServer.GoodJimakuKey, entries.ToJsonString() + all.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_tries_the_AniList_id_then_the_TMDB_id_then_the_title()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(JellyfinServer.GoodJimakuKey, string.Empty, string.Empty);
        ClearJimakuRequests();

        var entries = (await server.GetJsonAsync("Retimer/Jimaku/Entries?anilistId=1&tmdbId=movie:2&query=Frieren%20%26%20co", server.ViewerToken))!.AsArray();

        Assert.Equal(4321, Assert.Single(entries)!["Id"]!.GetValue<long>());
        Assert.Equal(
            [
                "/jimaku/api/entries/search?anilist_id=1",
                "/jimaku/api/entries/search?tmdb_id=movie%3A2",
                "/jimaku/api/entries/search?query=Frieren%20%26%20co"
            ],
            JimakuRequests().Select(r => r.PathAndQuery));

        ClearJimakuRequests();
        var nothing = (await server.GetJsonAsync("Retimer/Jimaku/Entries?query=no%20such%20show&anime=false", server.ViewerToken))!.AsArray();
        Assert.Empty(nothing);
        Assert.Equal("/jimaku/api/entries/search?query=no%20such%20show&anime=false", Assert.Single(JimakuRequests()).PathAndQuery);
    }

    [Fact]
    public async Task Jimakus_refusals_reach_the_caller_as_a_status_and_a_sentence()
    {
        JellyfinServer.RequireEnabled();

        // A key Jimaku rejects: the user cannot fix it, so the source is simply unavailable to them.
        await server.SaveSettingsAsync("e2e-wrong-key", string.Empty, string.Empty);
        using (var rejected = await server.SendAsync(HttpMethod.Get, "Retimer/Jimaku/Entries?anilistId=154587", server.ViewerToken))
        {
            Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode);
            var problem = JsonNode.Parse(await BodyAsync(rejected))!;
            Assert.Equal("Unauthorized", problem["title"]!.GetValue<string>());
            Assert.DoesNotContain("e2e-wrong-key", problem.ToJsonString(), StringComparison.Ordinal);
        }

        // The administrator's check names the cause.
        var check = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/Jimaku", server.AdminToken)))!;
        Assert.False(check["Ok"]!.GetValue<bool>());
        Assert.Equal("Unauthorized", check["Status"]!.GetValue<string>());

        await server.SaveSettingsAsync(JellyfinServer.GoodJimakuKey, string.Empty, string.Empty);
        check = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/Jimaku", server.AdminToken)))!;
        Assert.True(check["Ok"]!.GetValue<bool>(), check.ToJsonString());

        // A rate limit too long to wait out is passed on with the time to wait.
        using (var limited = await server.SendAsync(HttpMethod.Get, "Retimer/Jimaku/Entries?query=ratelimit", server.ViewerToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(30), limited.Headers.RetryAfter!.Delta);
        }

        using var missing = await server.SendAsync(HttpMethod.Get, "Retimer/Jimaku/Entries/999/Files", server.ViewerToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_retimer_check_tells_a_good_key_a_bad_key_and_a_dead_address_apart()
    {
        JellyfinServer.RequireEnabled();

        async Task<JsonNode> CheckAsync(string url, string key)
        {
            await server.SaveSettingsAsync(string.Empty, url, key);
            using var response = await server.SendAsync(HttpMethod.Post, "Retimer/Test/Retimer", server.AdminToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await BodyAsync(response))!;
        }

        // The plugin really calls out: the stand-in retimer on this machine sees the key arrive.
        var good = await CheckAsync(server.RetimerUrl, JellyfinServer.GoodRetimerKey);
        Assert.True(good["Ok"]!.GetValue<bool>(), good.ToJsonString());
        Assert.Equal("Ok", good["Status"]!.GetValue<string>());
        lock (server.RetimerKeysSeen)
        {
            Assert.Equal("Bearer " + JellyfinServer.GoodRetimerKey, server.RetimerKeysSeen.Last());
        }

        var wrong = await CheckAsync(server.RetimerUrl + "/", "rt_wrong");
        Assert.False(wrong["Ok"]!.GetValue<bool>());
        Assert.Equal("Unauthorized", wrong["Status"]!.GetValue<string>());

        var notARetimer = await CheckAsync(new Uri(server.HelperAddress, "nothing-here").AbsoluteUri, JellyfinServer.GoodRetimerKey);
        Assert.Equal("Unreachable", notARetimer["Status"]!.GetValue<string>());

        var dead = await CheckAsync("http://127.0.0.1:9", JellyfinServer.GoodRetimerKey);
        Assert.Equal("Unreachable", dead["Status"]!.GetValue<string>());

        var invalid = await CheckAsync("retimer.example.org", JellyfinServer.GoodRetimerKey);
        Assert.Equal("InvalidAddress", invalid["Status"]!.GetValue<string>());
    }
}
