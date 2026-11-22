using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// Watch mode inside a real Jellyfin: the switch for a season, Jellyfin's scheduled task, and a new
/// episode that gets its subtitle by itself. One test, because each step builds on the one before.
/// </summary>
public class WatchTests(JellyfinServerForWatch server) : IClassFixture<JellyfinServerForWatch>
{
    private const string StartedBy = "Watch mode";

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private async Task<JsonArray> JobsAsync() => (await server.GetJsonAsync("Retimer/Jobs?since=0", server.AdminToken))!["Jobs"]!.AsArray();

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string token, JsonNode? body = null)
    {
        using var response = await server.SendAsync(method, path, token, body);
        return response.StatusCode;
    }

    private async Task<JsonNode> TaskAsync()
    {
        var tasks = (await server.GetJsonAsync("ScheduledTasks", server.AdminToken))!.AsArray();
        return Assert.Single(tasks, task => task!["Key"]!.GetValue<string>() == "SubtitleRetimerWatch")!;
    }

    private async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, string what, int seconds = 90)
        where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (await probe() is { } found)
            {
                return found;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Waited {seconds} s for {what}.\n" + server.TailOfServerOutput());
    }

    [Fact]
    public async Task A_watched_season_gets_its_subtitles_by_itself_and_a_new_episode_too()
    {
        JellyfinServer.RequireEnabled();
        await server.SaveSettingsAsync(server.StandInSettings(retimer: false));

        // Jellyfin lists the check among its own scheduled tasks, every 3 hours.
        var task = await TaskAsync();
        Assert.Equal("Check watched shows for subtitles", task["Name"]!.GetValue<string>());
        var trigger = Assert.Single(task["Triggers"]!.AsArray())!;
        Assert.Equal("IntervalTrigger", trigger["Type"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger["IntervalTicks"]!.GetValue<long>());

        // A normal user may not switch it, nor start the check; an unknown season is refused.
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Watch", server.ViewerToken, new JsonObject { ["Watch"] = true }));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "Retimer/Watch/Check", server.ViewerToken));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Watch", server.AdminToken, new JsonObject { ["Watch"] = true, ["Season"] = "9" }));

        var show = (await server.GetJsonAsync("Retimer/Shows/" + server.ShowId, server.AdminToken))!;
        var season = Assert.Single(show["Seasons"]!.AsArray())!;
        Assert.Equal("1", season["Key"]!.GetValue<string>());
        Assert.False(season["Watched"]!.GetValue<bool>());
        Assert.Equal(3.0, show["WatchEveryHours"]!.GetValue<double>());

        // Switching the season on checks the show at once: a job for each of its three episodes.
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Watch", server.AdminToken, new JsonObject { ["Watch"] = true, ["Season"] = "1" }));
        foreach (var episode in new[] { 3, 4, 5 })
        {
            var job = await server.WaitForJobAsync(server.Episodes[episode]);
            Assert.Equal(StartedBy, job["StartedBy"]!.GetValue<string>());
            Assert.Equal("raw", job["State"]!.GetValue<string>());
        }

        var poster = Assert.Single((await server.GetJsonAsync("Retimer/Libraries", server.AdminToken))!.AsArray())!;
        var shows = (await server.GetJsonAsync($"Retimer/Libraries/{poster["Id"]!.GetValue<string>()}/Shows", server.ViewerToken))!.AsArray();
        Assert.True(Assert.Single(shows)!["Watched"]!.GetValue<bool>());

        // Jellyfin's own run of the task finds nothing new, and starts no job.
        var before = (await JobsAsync()).Count;
        var taskId = task["Id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Post, "ScheduledTasks/Running/" + taskId, server.AdminToken));
        await WaitForAsync(
            async () =>
            {
                var now = await TaskAsync();
                return now["State"]!.GetValue<string>() == "Idle" && now["LastExecutionResult"] is JsonObject result ? result : null;
            },
            "the scheduled task to finish");
        var watching = (await server.GetJsonAsync("Retimer/Watch", server.AdminToken))!;
        var last = watching["LastCheck"]!;
        Assert.Equal(1, last["Shows"]!.GetValue<int>());
        Assert.Equal(0, last["LookedFor"]!.GetValue<int>());
        Assert.Equal(0, last["JobsStarted"]!.GetValue<int>());
        Assert.True(last["Problem"] is null, "The check stopped: " + last["Problem"]);
        Assert.True(watching["MayCheck"]!.GetValue<bool>());
        var listed = Assert.Single(watching["Shows"]!.AsArray())!;
        Assert.Equal(JellyfinServer.ShowName, listed["Name"]!.GetValue<string>());
        Assert.Empty(listed["Waiting"]!.AsArray());
        Assert.Equal(before, (await JobsAsync()).Count);

        // A new episode comes into the library: the show is checked a few seconds later by itself.
        await server.MakeEpisodeAsync(6, withTracks: true);
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Post, "Library/Refresh", server.AdminToken));
        var added = await WaitForAsync(
            async () =>
            {
                var items = (await server.GetJsonAsync("Items?Recursive=true&IncludeItemTypes=Episode", server.AdminToken))!["Items"]!.AsArray();
                return items.FirstOrDefault(item => item!["IndexNumber"]?.GetValue<int>() == 6)?["Id"]!.GetValue<string>();
            },
            "Jellyfin to find episode 6");
        var newId = Guid.Parse(added).ToString("N");
        var fresh = await WaitForAsync(
            async () => (await JobsAsync()).FirstOrDefault(job => job!["ItemId"]!.GetValue<string>() == newId),
            "watch mode to start a job for episode 6",
            seconds: JellyfinServer.WatchDelaySeconds + 120);
        Assert.Equal(StartedBy, fresh["StartedBy"]!.GetValue<string>());
        Assert.Equal("raw", (await server.WaitForJobAsync(newId))["State"]!.GetValue<string>());

        // Switched off, nothing is listed as watched any more.
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Put, $"Retimer/Shows/{server.ShowId}/Watch", server.AdminToken, new JsonObject { ["Watch"] = false }));
        Assert.Empty((await server.GetJsonAsync("Retimer/Watch", server.AdminToken))!["Shows"]!.AsArray());
        Assert.Equal(HttpStatusCode.Accepted, await SendAsync(HttpMethod.Post, "Retimer/Watch/Check", server.AdminToken));
    }
}
