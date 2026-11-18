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
/// nobody, a normal user and an administrator. Jimaku, OpenRouter and Retimer API are stand-ins
/// of the fixture; the server, its library, ffmpeg and the plugin are real.
/// </summary>
public class PluginTests(JellyfinServer server) : IClassFixture<JellyfinServer>
{
    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private async Task<JsonNode> EpisodeAsync(int number, string? token = null)
    {
        var show = (await server.GetJsonAsync("Retimer/Shows/" + server.ShowId, token ?? server.AdminToken))!;
        return show["Seasons"]!.AsArray().SelectMany(season => season!["Episodes"]!.AsArray()).Single(episode => episode!["Number"]!.GetValue<int>() == number)!;
    }

    private async Task<List<JsonNode>> SubtitleStreamsAsync(int episode)
    {
        var item = (await server.GetJsonAsync($"Items/{server.Episodes[episode]}?userId={server.ViewerId}", server.ViewerToken))!;
        return item["MediaStreams"]!.AsArray().Where(stream => stream!["Type"]!.GetValue<string>() == "Subtitle").Select(stream => stream!).ToList();
    }

    private async Task<JsonNode> StartAsync(int episode, string kind = "get")
    {
        using var response = await server.SendAsync(HttpMethod.Post, "Retimer/Jobs", server.AdminToken, new JsonObject { ["ItemIds"] = new JsonArray(server.Episodes[episode]), ["Kind"] = kind });
        var text = await BodyAsync(response);
        Assert.True(response.IsSuccessStatusCode, $"POST Retimer/Jobs answered {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private string SavedPath(int episode, string extension = "srt") =>
        Path.Combine(server.SeasonDirectory, $"{JellyfinServer.ShowName} - S01E0{episode}.ja.{extension}");

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
    public async Task The_dashboard_lists_the_plugin_and_links_to_its_settings()
    {
        JellyfinServer.RequireEnabled();

        using var page = await server.SendAsync(HttpMethod.Get, "web/ConfigurationPage?name=SubtitleRetimer", server.AdminToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await BodyAsync(page);
        Assert.Contains("id=\"SubtitleRetimerSettingsLink\"", html, StringComparison.Ordinal);

        // The keys are typed on the plugin's own page, so this one holds no input field.
        Assert.DoesNotContain("<input", html, StringComparison.OrdinalIgnoreCase);

        var pages = (await server.GetJsonAsync("web/ConfigurationPages?enableInMainMenu=true", server.AdminToken))!.AsArray();
        var listed = Assert.Single(pages, p => p!["Name"]!.GetValue<string>() == "SubtitleRetimer")!;
        Assert.Equal("Subtitle Retimer", listed["DisplayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_login_only_the_empty_page_and_its_files_answer()
    {
        JellyfinServer.RequireEnabled();

        using var page = await server.SendAsync(HttpMethod.Get, "Retimer");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await BodyAsync(page);
        Assert.Contains("<title>Subtitle Retimer</title>", html, StringComparison.Ordinal);

        // The page may load only its own script and styles, and nothing is written into it.
        var policy = string.Join(' ', page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);

        foreach (var (path, type, marker) in new[] { ("Retimer/app.js", "text/javascript", "'use strict'"), ("Retimer/app.css", "text/css", "--primary") })
        {
            using var file = await server.SendAsync(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(type, file.Content.Headers.ContentType!.MediaType);
            Assert.Contains(marker, await BodyAsync(file), StringComparison.Ordinal);
        }

        // With a slash at the end the page's relative links would break, so it sends the browser on.
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var plain = new HttpClient(handler);
        using var slash = await plain.GetAsync(new Uri(server.BaseAddress, "Retimer/"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, slash.StatusCode);
        Assert.Equal("../Retimer", slash.Headers.Location!.OriginalString);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "Retimer/Status"),
            (HttpMethod.Get, "Retimer/Libraries"),
            (HttpMethod.Get, "Retimer/Shows/" + server.ShowId),
            (HttpMethod.Get, "Retimer/Jobs"),
            (HttpMethod.Post, "Retimer/Jobs"),
            (HttpMethod.Delete, "Retimer/Jobs"),
            (HttpMethod.Get, $"Retimer/Items/{server.Episodes[3]}/Files"),
            (HttpMethod.Delete, $"Retimer/Items/{server.Episodes[3]}/Subtitle"),
            (HttpMethod.Get, "Retimer/Settings"),
            (HttpMethod.Put, "Retimer/Settings"),
            (HttpMethod.Post, "Retimer/Test/jimaku")
        })
        {
            using var response = await server.SendAsync(method, path);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)response.StatusCode} without a login");
        }
    }

    [Fact]
    public async Task A_normal_user_browses_but_cannot_fetch_or_open_the_settings()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(server.StandInSettings());

        using var statusResponse = await server.SendAsync(HttpMethod.Get, "Retimer/Status", server.ViewerToken);
        var text = await BodyAsync(statusResponse);
        var status = JsonNode.Parse(text)!;
        Assert.Equal(server.PluginVersion, status["Version"]!.GetValue<string>());
        Assert.Equal(JellyfinServer.ViewerName, status["UserName"]!.GetValue<string>());
        Assert.False(status["IsAdministrator"]!.GetValue<bool>());
        Assert.False(status["MayFetch"]!.GetValue<bool>());
        Assert.True(status["RetimerReady"]!.GetValue<bool>());

        // The answer carries no key and not the address of Retimer API.
        foreach (var secret in new[] { JellyfinServer.GoodJimakuKey, JellyfinServer.GoodRetimerKey, JellyfinServer.GoodOpenRouterKey, server.HelperAddress.Authority })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        // Browsing works with a normal login: the library, its show, the episodes with their state.
        var libraries = (await server.GetJsonAsync("Retimer/Libraries", server.ViewerToken))!.AsArray();
        var library = Assert.Single(libraries)!;
        var shows = (await server.GetJsonAsync($"Retimer/Libraries/{library["Id"]!.GetValue<string>()}/Shows", server.ViewerToken))!.AsArray();
        var show = Assert.Single(shows)!;
        Assert.Equal(server.ShowId, show["Id"]!.GetValue<string>());
        Assert.Equal(3, show["Total"]!.GetValue<int>());
        Assert.Equal("ja", (await server.GetJsonAsync("Retimer/Shows/" + server.ShowId, server.ViewerToken))!["Language"]!.GetValue<string>());

        foreach (var (method, path, body) in new (HttpMethod, string, JsonNode?)[]
        {
            (HttpMethod.Post, "Retimer/Jobs", new JsonObject { ["ItemIds"] = new JsonArray(server.Episodes[5]), ["Kind"] = "get" }),
            (HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Language", new JsonObject { ["Language"] = "en" }),
            (HttpMethod.Delete, $"Retimer/Items/{server.Episodes[5]}/Subtitle", null),
            (HttpMethod.Delete, "Retimer/Jobs", null),
            (HttpMethod.Get, "Retimer/Settings", null),
            (HttpMethod.Put, "Retimer/Settings", new JsonObject { ["FetchAccess"] = "everyone" }),
            (HttpMethod.Post, "Retimer/Test/jimaku", null),
            (HttpMethod.Post, "Retimer/Test/retimer", null),
            (HttpMethod.Get, $"Plugins/{server.PluginGuid}/Configuration", null),
            (HttpMethod.Get, "web/ConfigurationPages", null)
        })
        {
            using var response = await server.SendAsync(method, path, server.ViewerToken, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {(int)response.StatusCode} to a normal user");
        }
    }

    [Fact]
    public async Task An_administrator_saves_settings_and_no_key_comes_back()
    {
        JellyfinServer.RequireEnabled();
        var settings = server.StandInSettings();
        settings["JimakuApiKey"] = "key & <one>";
        settings["RemoveSongs"] = true;
        settings["Models"] = new JsonArray(
            new JsonObject { ["Id"] = "anthropic/claude-haiku-5.5", ["Name"] = "Haiku 5.5" },
            new JsonObject { ["Id"] = "example/other-model", ["Name"] = "Other" });
        settings["ModelInUse"] = "example/other-model";

        var saved = await server.SaveSettingsAsync(settings);
        Assert.True(saved["HasJimakuApiKey"]!.GetValue<bool>());
        Assert.True(saved["RemoveSongs"]!.GetValue<bool>());
        Assert.Equal("example/other-model", saved["ModelInUse"]!.GetValue<string>());

        using var read = await server.SendAsync(HttpMethod.Get, "Retimer/Settings", server.AdminToken);
        Assert.Equal("no-store", read.Headers.CacheControl!.ToString());
        var text = await BodyAsync(read);
        foreach (var secret in new[] { "key &", JellyfinServer.GoodRetimerKey, JellyfinServer.GoodOpenRouterKey })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        // The keys are on the server's disk, where Jellyfin keeps every plugin's settings.
        var file = Path.Combine(server.DataDirectory, "plugins", "configurations", "Jellyfin.Plugin.Retimer.xml");
        var xml = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.Contains("<JimakuApiKey>key &amp; &lt;one&gt;</JimakuApiKey>", xml, StringComparison.Ordinal);
        Assert.Contains("<RetimerApiKey>" + JellyfinServer.GoodRetimerKey + "</RetimerApiKey>", xml, StringComparison.Ordinal);

        // An empty key field keeps the stored key, and a value the plugin does not know is refused.
        await server.SaveSettingsAsync(new JsonObject { ["JimakuApiKey"] = string.Empty, ["RemoveSongs"] = false });
        Assert.Contains("key &amp; &lt;one&gt;", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using var bad = await server.SendAsync(HttpMethod.Put, "Retimer/Settings", server.AdminToken, new JsonObject { ["SendAudio"] = "sometimes" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_checks_tell_a_good_key_a_bad_key_and_a_dead_address_apart()
    {
        JellyfinServer.RequireEnabled();

        async Task<JsonNode> CheckAsync(string target, JsonObject change, JsonNode? body = null)
        {
            var settings = server.StandInSettings();
            foreach (var pair in change)
            {
                settings[pair.Key] = pair.Value?.DeepClone();
            }

            await server.SaveSettingsAsync(settings);
            using var response = await server.SendAsync(HttpMethod.Post, "Retimer/Test/" + target, server.AdminToken, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await BodyAsync(response))!;
        }

        var model = new JsonObject { ["ModelId"] = "anthropic/claude-haiku-5.5" };

        Assert.True((await CheckAsync("jimaku", []))["Ok"]!.GetValue<bool>());
        Assert.Equal("Unauthorized", (await CheckAsync("jimaku", new JsonObject { ["JimakuApiKey"] = "wrong" }))["Status"]!.GetValue<string>());

        var retimer = await CheckAsync("retimer", []);
        Assert.True(retimer["Ok"]!.GetValue<bool>());
        Assert.Contains("up to 6 subtitle tracks", retimer["Message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Unauthorized", (await CheckAsync("retimer", new JsonObject { ["RetimerApiKey"] = "rt_wrong" }))["Status"]!.GetValue<string>());
        Assert.False((await CheckAsync("retimer", new JsonObject { ["RetimerUrl"] = "http://127.0.0.1:9" }))["Ok"]!.GetValue<bool>());

        Assert.True((await CheckAsync("model", [], model))["Ok"]!.GetValue<bool>());
        Assert.False((await CheckAsync("model", new JsonObject { ["OpenRouterApiKey"] = "sk-or-wrong" }, model))["Ok"]!.GetValue<bool>());

        using var unknown = await server.SendAsync(HttpMethod.Post, "Retimer/Test/elsewhere", server.AdminToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_subtitle_is_found_retimed_against_the_videos_tracks_saved_and_listed_at_once()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(server.StandInSettings());
        Assert.Equal("none", (await EpisodeAsync(3))["State"]!.GetValue<string>());
        int before;
        lock (server.RetimerSubmissions)
        {
            before = server.RetimerSubmissions.Count;
        }

        var started = await StartAsync(3);
        Assert.Equal(1, started["Started"]!.GetValue<int>());
        var job = await server.WaitForJobAsync(server.Episodes[3]);

        Assert.True(job["State"]!.GetValue<string>() == "saved", "The job ended as " + job.ToJsonString());
        Assert.Contains("“Full”", job["Detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.EndsWith("Moved 7.5 s later.", job["Detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(job["CostUsd"]!.GetValue<double>() > 0.002);

        // Retimer API got both tracks of the video, cut out by ffmpeg, with what Jellyfin knows about each.
        RetimerSubmission sent;
        lock (server.RetimerSubmissions)
        {
            Assert.Equal(before + 1, server.RetimerSubmissions.Count);
            sent = server.RetimerSubmissions[^1];
        }

        Assert.EndsWith(".srt", sent.TargetName, StringComparison.Ordinal);
        Assert.False(sent.HasAudio);
        Assert.Equal(2, sent.References.Count);
        Assert.Contains("Good morning, Mistress Frieren.", sent.References[0], StringComparison.Ordinal);
        Assert.Contains("FRIEREN", sent.References[1], StringComparison.Ordinal);
        var tracks = sent.Options["reference_tracks"]!.AsArray();
        Assert.Equal(["Full", "Signs"], tracks.Select(track => track!["title"]!.GetValue<string>()));
        Assert.All(tracks, track => Assert.Equal("eng", track!["language"]!.GetValue<string>()));
        Assert.True(tracks[0]!["default"]!.GetValue<bool>());

        // The file is next to the video, it is what Retimer API returned, and Jellyfin lists it without a scan.
        var saved = await File.ReadAllTextAsync(SavedPath(3), TestContext.Current.CancellationToken);
        Assert.Contains(JellyfinServer.RetimedStamp, saved, StringComparison.Ordinal);
        Assert.Contains("おはようございます", saved, StringComparison.Ordinal);
        var external = Assert.Single(await SubtitleStreamsAsync(3), stream => stream["IsExternal"]!.GetValue<bool>());
        Assert.Equal("jpn", external["Language"]!.GetValue<string>());

        var episode = await EpisodeAsync(3, server.ViewerToken);
        Assert.Equal("retimed", episode["State"]!.GetValue<string>());
        Assert.True(episode["FromPlugin"]!.GetValue<bool>());
        Assert.Equal("[Group] Sousou no Frieren - 03.ja.srt", episode["FileName"]!.GetValue<string>());

        // The model was asked for the file and for the language, through OpenRouter's chat API.
        lock (server.ModelCalls)
        {
            Assert.Contains("map_files", server.ModelCalls);
            Assert.Contains("language_verdict", server.ModelCalls);
        }

        // A second run for the same episode finds the same two tracks, although Jellyfin now numbers them differently.
        Assert.Equal(1, (await StartAsync(3, "retime"))["Started"]!.GetValue<int>());
        var again = await server.WaitForJobAsync(server.Episodes[3]);
        Assert.True(again["State"]!.GetValue<string>() == "saved", "The second job ended as " + again.ToJsonString());
        lock (server.RetimerSubmissions)
        {
            Assert.Equal(before + 2, server.RetimerSubmissions.Count);
            Assert.Equal(sent.References, server.RetimerSubmissions[^1].References);
        }

        // Removing it takes the file away and Jellyfin's entry with it.
        using (var removed = await server.SendAsync(HttpMethod.Delete, $"Retimer/Items/{server.Episodes[3]}/Subtitle", server.AdminToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.False(File.Exists(SavedPath(3)));
        Assert.DoesNotContain(await SubtitleStreamsAsync(3), stream => stream["IsExternal"]!.GetValue<bool>());
        Assert.Equal("none", (await EpisodeAsync(3))["State"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_Retimer_API_switched_off_the_subtitle_is_saved_as_downloaded()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(server.StandInSettings(retimer: false));
        int before;
        lock (server.RetimerSubmissions)
        {
            before = server.RetimerSubmissions.Count;
        }

        Assert.Equal(1, (await StartAsync(4))["Started"]!.GetValue<int>());
        var job = await server.WaitForJobAsync(server.Episodes[4]);

        Assert.True(job["State"]!.GetValue<string>() == "raw", "The job ended as " + job.ToJsonString());
        var saved = await File.ReadAllTextAsync(SavedPath(4), TestContext.Current.CancellationToken);
        Assert.Contains("00:00:01,000", saved, StringComparison.Ordinal);
        Assert.DoesNotContain(JellyfinServer.RetimedStamp, saved, StringComparison.Ordinal);
        Assert.Equal("raw", (await EpisodeAsync(4))["State"]!.GetValue<string>());
        lock (server.RetimerSubmissions)
        {
            Assert.Equal(before, server.RetimerSubmissions.Count);
        }
    }

    [Fact]
    public async Task A_video_without_a_subtitle_track_is_saved_without_retiming_and_says_why()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(server.StandInSettings());

        Assert.Equal(1, (await StartAsync(5))["Started"]!.GetValue<int>());
        var job = await server.WaitForJobAsync(server.Episodes[5]);

        Assert.True(job["State"]!.GetValue<string>() == "raw", "The job ended as " + job.ToJsonString());
        Assert.Contains("no text subtitle track", job["Detail"]!.GetValue<string>(), StringComparison.Ordinal);
        var episode = await EpisodeAsync(5);
        Assert.Equal("raw", episode["State"]!.GetValue<string>());
        Assert.True(episode["CanRetime"]!.GetValue<bool>());
        Assert.True(File.Exists(SavedPath(5)));
    }

    [Fact]
    public async Task The_day_limit_and_a_language_without_a_source_stop_a_job_with_a_sentence()
    {
        JellyfinServer.RequireEnabled();
        var settings = server.StandInSettings();
        var used = (await server.GetJsonAsync("Retimer/Status", server.AdminToken))!["Usage"]!["Jobs"]!.GetValue<int>();
        settings["MaxJobsPerDay"] = Math.Max(1, used);
        await server.SaveSettingsAsync(settings);
        if (used == 0)
        {
            // The limit is at one job, so the first one runs and uses the day up.
            await StartAsync(5, "another");
            await server.WaitForJobAsync(server.Episodes[5]);
        }

        var refused = await StartAsync(5, "another");
        Assert.Equal(0, refused["Started"]!.GetValue<int>());
        Assert.Contains("Today's limit of", refused["Refused"]!.GetValue<string>(), StringComparison.Ordinal);

        // Jimaku has Japanese only, and no other source is set up.
        await server.SaveSettingsAsync(server.StandInSettings());
        using (var english = await server.SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Language", server.AdminToken, new JsonObject { ["Language"] = "en" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, english.StatusCode);
        }

        try
        {
            var show = (await server.GetJsonAsync("Retimer/Shows/" + server.ShowId, server.AdminToken))!;
            Assert.Equal("en", show["Language"]!.GetValue<string>());
            Assert.True(show["OwnLanguage"]!.GetValue<bool>());

            // Episode 5 has no English subtitle, the others carry English tracks.
            var skipped = await StartAsync(5);
            Assert.Equal(0, skipped["Started"]!.GetValue<int>());
            Assert.Contains("No subtitle source is set up for English", skipped["SkippedBecause"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("present", (await EpisodeAsync(4))["State"]!.GetValue<string>());

            using var unknown = await server.SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Language", server.AdminToken, new JsonObject { ["Language"] = "xx" });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        finally
        {
            using var back = await server.SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Language", server.AdminToken, new JsonObject { ["Language"] = string.Empty });
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }
    }

    [Fact]
    public async Task The_picker_lists_the_sources_files_for_an_episode()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(server.StandInSettings());

        var files = (await server.GetJsonAsync($"Retimer/Items/{server.Episodes[4]}/Files", server.AdminToken))!;

        Assert.Equal("ja", files["Language"]!.GetValue<string>());
        var source = Assert.Single(files["Sources"]!.AsArray())!;
        Assert.Equal("4321", source["EntryId"]!.GetValue<string>());
        var first = files["Files"]!.AsArray()[0]!;
        Assert.Equal("[Group] Sousou no Frieren - 04.ja.srt", first["Name"]!.GetValue<string>());
        Assert.True(first["Fits"]!.GetValue<bool>());

        // Files of other episodes are listed too, so a wrong guess can be corrected by hand.
        Assert.Contains(files["Files"]!.AsArray(), file => file!["Name"]!.GetValue<string>().Contains(" - 03", StringComparison.Ordinal) && !file["Fits"]!.GetValue<bool>());

        // A normal user may look, and another user's library item stays out of reach of a made-up id.
        using var viewer = await server.SendAsync(HttpMethod.Get, $"Retimer/Items/{server.Episodes[4]}/Files", server.ViewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        using var missing = await server.SendAsync(HttpMethod.Get, $"Retimer/Items/{Guid.NewGuid():N}/Files", server.AdminToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
