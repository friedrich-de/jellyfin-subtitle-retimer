using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// The one test that talks to the real jimaku.cc, to confirm what the stand-in assumes.
/// It runs only where JIMAKU_API_KEY is set. The fixture's library holds one show, Sousou no Frieren.
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
        // No OpenRouter key is set, so the plugin picks by its rules.
        await server.SaveSettingsAsync(new JsonObject { ["JimakuEnabled"] = true, ["JimakuApiKey"] = "e2e-wrong-key", ["RetimerEnabled"] = false });
        var rejected = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/jimaku", server.AdminToken)))!;
        Assert.Equal("Unauthorized", rejected["Status"]!.GetValue<string>());

        await server.SaveSettingsAsync(new JsonObject { ["JimakuApiKey"] = key });
        var accepted = JsonNode.Parse(await BodyAsync(await server.SendAsync(HttpMethod.Post, "Retimer/Test/jimaku", server.AdminToken)))!;
        Assert.True(accepted["Ok"]!.GetValue<bool>(), accepted.ToJsonString());

        // The title search an administrator uses when the plugin took the wrong show.
        var episode = server.Episodes[3];
        var entries = (await server.GetJsonAsync($"Retimer/Items/{episode}/Entries?query=Sousou%20no%20Frieren", server.AdminToken))!.AsArray();
        Assert.Contains(entries, entry => entry!["Name"]!.GetValue<string>().Contains("Frieren", StringComparison.OrdinalIgnoreCase));

        // The picker for an episode of the test library's show: the real entry and real files.
        var files = (await server.GetJsonAsync($"Retimer/Items/{episode}/Files", server.AdminToken))!;
        var source = Assert.Single(files["Sources"]!.AsArray())!;
        Assert.Equal("jimaku", source["Source"]!.GetValue<string>());
        Assert.NotEmpty(files["Files"]!.AsArray());
        foreach (var file in files["Files"]!.AsArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(file!["Name"]!.GetValue<string>()));
        }

        // Nothing that came back carries the key.
        Assert.DoesNotContain(key!, entries.ToJsonString() + files.ToJsonString(), StringComparison.Ordinal);
    }
}
