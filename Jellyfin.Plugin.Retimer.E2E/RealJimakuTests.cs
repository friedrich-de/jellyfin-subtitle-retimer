using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// The one test that talks to the real jimaku.cc, to confirm what the stand-in assumes.
/// It runs only where JIMAKU_API_KEY is set. AniList id 10087 is Fate/Zero, the series in the
/// project's test library.
/// </summary>
public class RealJimakuTests(JellyfinServerWithRealJimaku server) : IClassFixture<JellyfinServerWithRealJimaku>
{
    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Jimaku_finds_a_known_series_and_its_files_with_a_real_key()
    {
        JellyfinServer.RequireEnabled();
        Assert.SkipUnless(JellyfinServerWithRealJimaku.HasRealKey, "Set JIMAKU_API_KEY to run the search against the real jimaku.cc.");
        var key = JellyfinServerWithRealJimaku.RealKey;

        // A wrong key first: Jimaku itself says no, and the plugin passes that on in words.
        await server.SaveSettingsAsync("e2e-wrong-key", string.Empty, string.Empty);
        var rejected = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/Jimaku", server.AdminToken)))!;
        Assert.Equal("Unauthorized", rejected["Status"]!.GetValue<string>());

        await server.SaveSettingsAsync(key!, string.Empty, string.Empty);
        var accepted = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/Jimaku", server.AdminToken)))!;
        Assert.True(accepted["Ok"]!.GetValue<bool>(), accepted.ToJsonString());

        // A normal user searches by AniList id.
        var byId = (await server.GetJsonAsync("Retimer/Jimaku/Entries?anilistId=10087", server.ViewerToken))!.AsArray();
        var entry = Assert.Single(byId)!;
        Assert.Equal(10087, entry["AnilistId"]!.GetValue<int>());
        var entryId = entry["Id"]!.GetValue<long>();
        Assert.False(string.IsNullOrWhiteSpace(entry["Name"]!.GetValue<string>()));

        // The title finds the same entry among its matches.
        var byTitle = (await server.GetJsonAsync("Retimer/Jimaku/Entries?query=Fate%2FZero", server.ViewerToken))!.AsArray();
        Assert.Contains(byTitle, e => e!["Id"]!.GetValue<long>() == entryId);

        // Files for one episode are fewer than all files, and each has a name and a download address.
        var all = (await server.GetJsonAsync($"Retimer/Jimaku/Entries/{entryId}/Files", server.ViewerToken))!.AsArray();
        var episode = (await server.GetJsonAsync($"Retimer/Jimaku/Entries/{entryId}/Files?episode=2", server.ViewerToken))!.AsArray();
        Assert.NotEmpty(episode);
        Assert.True(episode.Count <= all.Count, $"episode filter returned {episode.Count} of {all.Count} files");
        foreach (var file in episode)
        {
            Assert.False(string.IsNullOrWhiteSpace(file!["Name"]!.GetValue<string>()));
            Assert.StartsWith("https://", file["Url"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(file["Size"]!.GetValue<long>() > 0);
        }

        // Nothing the normal user received carries the key.
        Assert.DoesNotContain(key!, byId.ToJsonString() + byTitle.ToJsonString() + all.ToJsonString(), StringComparison.Ordinal);

        using var missing = await server.SendAsync(HttpMethod.Get, "Retimer/Jimaku/Entries/999999999/Files", server.ViewerToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
