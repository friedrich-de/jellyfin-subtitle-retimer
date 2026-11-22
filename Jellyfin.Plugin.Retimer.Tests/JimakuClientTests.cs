using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests;

public class JimakuClientTests
{
    // Shaped after the examples in Jimaku's published API description (jimaku.cc/api/openapi.json).
    private const string FrierenEntry = """
        {
          "id": 4321,
          "name": "Sousou no Frieren",
          "english_name": "Frieren: Beyond Journey’s End",
          "japanese_name": "葬送のフリーレン",
          "anilist_id": 154587,
          "tmdb_id": "tv:209867",
          "creator_id": 7,
          "last_modified": "2024-03-22T15:04:05.123456Z",
          "flags": { "anime": true, "movie": false, "adult": false, "external": false, "unverified": false },
          "notes": null
        }
        """;

    private const string SparseEntry = """
        { "id": 9, "name": "Only A Name", "flags": {}, "last_modified": "2023-01-02T03:04:05Z" }
        """;

    private const string Files = """
        [
          { "name": "[Group] Sousou no Frieren - 03.ja.srt", "url": "https://jimaku.cc/entry/4321/download/x-03.srt", "size": 34567, "last_modified": "2023-10-14T12:00:00Z" },
          { "name": "Frieren S01 (1-28).zip", "url": "https://jimaku.cc/entry/4321/download/batch.zip", "size": 1048576, "last_modified": "2024-03-22T15:04:05Z" }
        ]
        """;

    private static readonly Uri _baseAddress = new("https://jimaku.test/api/");

    private readonly StubHandler _handler = new();
    private readonly StubSettings _settings = new() { Current = new PluginConfiguration { JimakuApiKey = "test-key" } };
    private readonly List<TimeSpan> _waits = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private JimakuClient Client() => new(
        new StubHttpClientFactory(_handler),
        _settings,
        NullLogger<JimakuClient>.Instance,
        _baseAddress,
        (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Search_by_AniList_id_sends_the_bare_key_and_names_the_client()
    {
        _handler.Json("[" + FrierenEntry + "]");

        var entries = await Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 154587 }, Token);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://jimaku.test/api/entries/search?anilist_id=154587", request.Url.AbsoluteUri);
        Assert.Equal("test-key", request.Authorization);
        Assert.StartsWith("jellyfin-subtitle-retimer/", request.UserAgent, StringComparison.Ordinal);

        var entry = Assert.Single(entries);
        Assert.Equal(4321, entry.Id);
        Assert.Equal("Sousou no Frieren", entry.Name);
        Assert.Equal("Frieren: Beyond Journey’s End", entry.EnglishName);
        Assert.Equal("葬送のフリーレン", entry.JapaneseName);
        Assert.Equal(154587, entry.AnilistId);
        Assert.Equal("tv:209867", entry.TmdbId);
        Assert.True(entry.Flags.Anime);
        Assert.False(entry.Flags.Movie);
        Assert.Equal(new DateTimeOffset(2024, 3, 22, 15, 4, 5, TimeSpan.Zero).AddTicks(1234560), entry.LastModified);
    }

    [Fact]
    public async Task Search_reads_an_entry_that_has_only_the_required_fields()
    {
        _handler.Json("[" + SparseEntry + "]");

        var entry = Assert.Single(await Client().SearchEntriesAsync(new JimakuEntryQuery { Title = "x" }, Token));

        Assert.Equal(9, entry.Id);
        Assert.Null(entry.AnilistId);
        Assert.Null(entry.TmdbId);
        Assert.Null(entry.JapaneseName);
        Assert.False(entry.Flags.Anime);
    }

    [Fact]
    public async Task Search_by_TMDB_id_escapes_the_colon()
    {
        _handler.Json("[]");

        var entries = await Client().SearchEntriesAsync(new JimakuEntryQuery { TmdbId = JimakuTmdbId.Tv(209867) }, Token);

        Assert.Empty(entries);
        Assert.Equal("https://jimaku.test/api/entries/search?tmdb_id=tv%3A209867", Assert.Single(_handler.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task Search_by_title_escapes_it_and_passes_the_anime_switch()
    {
        _handler.Json("[]");

        await Client().SearchEntriesAsync(new JimakuEntryQuery { Title = "  葬送の フリーレン & co ", Anime = false }, Token);

        var url = Assert.Single(_handler.Requests).Url;
        Assert.Equal("/api/entries/search", url.AbsolutePath);
        Assert.Equal("?query=%E8%91%AC%E9%80%81%E3%81%AE%20%E3%83%95%E3%83%AA%E3%83%BC%E3%83%AC%E3%83%B3%20%26%20co&anime=false", url.Query);
    }

    [Theory]
    [InlineData("tv:")]
    [InlineData("209867")]
    [InlineData("show:1")]
    [InlineData("tv:12 OR 1=1")]
    public async Task Search_rejects_a_malformed_TMDB_id_before_sending(string tmdbId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { TmdbId = tmdbId }, Token));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Search_with_nothing_to_search_for_is_rejected_before_sending()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { Title = "   " }, Token));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Without_a_key_nothing_is_sent(string key)
    {
        _settings.Current = new PluginConfiguration { JimakuApiKey = key };

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));

        Assert.Equal(JimakuError.NotConfigured, exception.Error);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task The_key_is_read_at_call_time_and_trimmed()
    {
        var client = Client();
        _settings.Current = new PluginConfiguration { JimakuApiKey = "  changed-key\n" };
        _handler.Json("[]");

        await client.SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token);

        Assert.Equal("changed-key", Assert.Single(_handler.Requests).Authorization);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_as_unauthorized()
    {
        _handler.Json("""{"error":"invalid API key","code":401}""", HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));

        Assert.Equal(JimakuError.Unauthorized, exception.Error);
        Assert.DoesNotContain("test-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_rate_limit_is_handed_back_with_the_wait()
    {
        _handler.Json("""{"error":"rate limited","code":429}""", HttpStatusCode.TooManyRequests, ("x-ratelimit-reset-after", "42.5"));

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));

        Assert.Equal(JimakuError.RateLimited, exception.Error);
        Assert.Equal(TimeSpan.FromSeconds(42.5), exception.RetryAfter);
        Assert.Single(_handler.Requests);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task A_short_rate_limit_is_waited_out_once()
    {
        _handler
            .Json("""{"error":"rate limited","code":429}""", HttpStatusCode.TooManyRequests, ("x-ratelimit-reset-after", "0.98"))
            .Json("[" + FrierenEntry + "]");

        var entries = await Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 154587 }, Token);

        Assert.Single(entries);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.98), Assert.Single(_waits));
    }

    [Fact]
    public async Task A_second_rate_limit_in_a_row_is_handed_back()
    {
        _handler
            .Json("{}", HttpStatusCode.TooManyRequests, ("x-ratelimit-reset-after", "1"))
            .Json("{}", HttpStatusCode.TooManyRequests, ("x-ratelimit-reset-after", "1"));

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));

        Assert.Equal(JimakuError.RateLimited, exception.Error);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task A_rate_limit_without_a_wait_is_handed_back_at_once()
    {
        _handler.Json("{}", HttpStatusCode.TooManyRequests);

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));

        Assert.Equal(JimakuError.RateLimited, exception.Error);
        Assert.Null(exception.RetryAfter);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task A_server_error_a_network_error_and_an_unreadable_answer_all_count_as_unavailable()
    {
        _handler
            .Json("oops", HttpStatusCode.InternalServerError)
            .Throws(new HttpRequestException("connection refused"))
            .Text("<html>maintenance</html>", "text/html");

        for (var i = 0; i < 3; i++)
        {
            var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));
            Assert.Equal(JimakuError.Unavailable, exception.Error);
        }
    }

    [Fact]
    public async Task A_bad_request_carries_Jimakus_own_explanation()
    {
        _handler.Json("""{"error":"invalid tmdb id","code":400}""", HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, Token));

        Assert.Equal(JimakuError.BadRequest, exception.Error);
        Assert.Contains("invalid tmdb id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_are_listed_for_one_episode()
    {
        _handler.Json(Files);

        var files = await Client().GetFilesAsync(4321, 3, Token);

        Assert.Equal("https://jimaku.test/api/entries/4321/files?episode=3", Assert.Single(_handler.Requests).Url.AbsoluteUri);
        Assert.Equal(2, files.Count);
        Assert.Equal("[Group] Sousou no Frieren - 03.ja.srt", files[0].Name);
        Assert.Equal("https://jimaku.cc/entry/4321/download/x-03.srt", files[0].Url);
        Assert.Equal(34567, files[0].Size);
        Assert.Equal(new DateTimeOffset(2023, 10, 14, 12, 0, 0, TimeSpan.Zero), files[0].LastModified);
        Assert.Equal(1048576, files[1].Size);
    }

    [Fact]
    public async Task Files_without_an_episode_ask_for_all()
    {
        _handler.Json("[]");

        Assert.Empty(await Client().GetFilesAsync(4321, null, Token));
        Assert.Equal("https://jimaku.test/api/entries/4321/files", Assert.Single(_handler.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task Files_of_an_unknown_entry_are_reported_as_not_found()
    {
        _handler.Json("""{"error":"entry not found","code":404}""", HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().GetFilesAsync(1, null, Token));

        Assert.Equal(JimakuError.NotFound, exception.Error);
    }

    [Fact]
    public async Task Negative_numbers_are_rejected_before_sending()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client().GetFilesAsync(-1, null, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client().GetFilesAsync(1, -1, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Client().SearchEntriesAsync(new JimakuEntryQuery { AnilistId = -5 }, Token));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Find_stops_at_the_AniList_id_when_it_matches()
    {
        _handler.Json("[" + FrierenEntry + "]");

        var entries = await Client().FindEntriesAsync(
            new JimakuEntryQuery { AnilistId = 154587, TmdbId = "tv:209867", Title = "Frieren" },
            Token);

        Assert.Single(entries);
        Assert.Equal("?anilist_id=154587", Assert.Single(_handler.Requests).Url.Query);
    }

    [Fact]
    public async Task Find_falls_back_from_AniList_to_TMDB_to_the_title()
    {
        _handler.Json("[]").Json("[]").Json("[" + FrierenEntry + "]");

        var entries = await Client().FindEntriesAsync(
            new JimakuEntryQuery { AnilistId = 154587, TmdbId = "tv:209867", Title = "Frieren" },
            Token);

        Assert.Single(entries);
        Assert.Equal(
            ["?anilist_id=154587", "?tmdb_id=tv%3A209867", "?query=Frieren"],
            _handler.Requests.ConvertAll(r => r.Url.Query));
    }

    [Fact]
    public async Task Find_skips_what_is_not_known_and_returns_nothing_when_nothing_matches()
    {
        _handler.Json("[]").Json("[]");

        var entries = await Client().FindEntriesAsync(new JimakuEntryQuery { TmdbId = "movie:129", Title = "Spirited Away", Anime = true }, Token);

        Assert.Empty(entries);
        Assert.Equal(
            ["?tmdb_id=movie%3A129&anime=true", "?query=Spirited%20Away&anime=true"],
            _handler.Requests.ConvertAll(r => r.Url.Query));
    }

    [Fact]
    public async Task Find_with_nothing_known_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindEntriesAsync(new JimakuEntryQuery(), Token));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Find_does_not_hide_a_failure_behind_the_next_step()
    {
        _handler.Json("{}", HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<JimakuException>(() => Client().FindEntriesAsync(
            new JimakuEntryQuery { AnilistId = 1, Title = "x" },
            Token));

        Assert.Equal(JimakuError.Unauthorized, exception.Error);
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData("tv:1", true)]
    [InlineData("movie:129", true)]
    [InlineData("tv:209867", true)]
    [InlineData("TV:1", false)]
    [InlineData("tv:-1", false)]
    [InlineData("tv:1\n", false)]
    [InlineData(" tv:1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TMDB_ids_are_checked_strictly(string? value, bool valid)
    {
        Assert.Equal(valid, JimakuTmdbId.IsValid(value));
    }

    [Fact]
    public void TMDB_ids_are_written_in_Jimakus_form()
    {
        Assert.Equal("tv:209867", JimakuTmdbId.Tv(209867));
        Assert.Equal("movie:129", JimakuTmdbId.Movie(129));
    }
}
