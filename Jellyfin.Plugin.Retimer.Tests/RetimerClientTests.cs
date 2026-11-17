using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests;

public class RetimerClientTests
{
    private readonly StubHandler _handler = new();
    private readonly StubSettings _settings = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RetimerClient Client(string url, string key)
    {
        _settings.Current = new PluginConfiguration { RetimerUrl = url, RetimerApiKey = key };
        return new RetimerClient(new StubHttpClientFactory(_handler), _settings, NullLogger<RetimerClient>.Instance);
    }

    [Theory]
    [InlineData("https://retimer.example.org", "https://retimer.example.org/")]
    [InlineData("https://retimer.example.org/", "https://retimer.example.org/")]
    [InlineData("  http://192.168.1.20:8000// ", "http://192.168.1.20:8000/")]
    [InlineData("https://example.org/retimer", "https://example.org/retimer/")]
    public void An_address_becomes_a_base_address_with_one_trailing_slash(string address, string expected)
    {
        Assert.True(RetimerClient.TryGetBaseAddress(address, out var baseAddress));
        Assert.Equal(expected, baseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("retimer.example.org")]
    [InlineData("ftp://retimer.example.org")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:secret@retimer.example.org")]
    [InlineData("https://retimer.example.org/?key=1")]
    [InlineData("https://retimer.example.org/#x")]
    public void Anything_but_a_plain_http_address_is_refused(string? address)
    {
        Assert.False(RetimerClient.TryGetBaseAddress(address, out _));
    }

    [Theory]
    [InlineData("", "rt_key")]
    [InlineData("https://retimer.example.org", "")]
    [InlineData("   ", "   ")]
    public async Task Without_an_address_or_a_key_nothing_is_sent(string url, string key)
    {
        Assert.Equal(RetimerConnectionStatus.NotConfigured, await Client(url, key).CheckConnectionAsync(Token));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task With_a_bad_address_nothing_is_sent()
    {
        Assert.Equal(RetimerConnectionStatus.InvalidAddress, await Client("retimer.example.org", "rt_key").CheckConnectionAsync(Token));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task The_check_lists_the_keys_jobs_with_a_bearer_key()
    {
        _handler.Json("""{"jobs":[]}""");

        var status = await Client("https://example.org/retimer/", " rt_key ").CheckConnectionAsync(Token);

        Assert.Equal(RetimerConnectionStatus.Ok, status);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://example.org/retimer/v1/jobs", request.Url.AbsoluteUri);
        Assert.Equal("Bearer rt_key", request.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_reported_as_unauthorized(HttpStatusCode code)
    {
        _handler.Json("""{"detail":"A valid API key is required"}""", code);

        Assert.Equal(RetimerConnectionStatus.Unauthorized, await Client("https://retimer.example.org", "rt_wrong").CheckConnectionAsync(Token));
    }

    [Fact]
    public async Task Another_website_at_the_address_does_not_pass_as_a_retimer()
    {
        _handler.Text("<html>Welcome to nginx</html>", "text/html");

        Assert.Equal(RetimerConnectionStatus.Unreachable, await Client("https://example.org", "rt_key").CheckConnectionAsync(Token));
    }

    [Fact]
    public async Task A_network_error_and_a_server_error_count_as_unreachable()
    {
        _handler.Throws(new HttpRequestException("no route to host")).Json("{}", HttpStatusCode.BadGateway);
        var client = Client("https://retimer.example.org", "rt_key");

        Assert.Equal(RetimerConnectionStatus.Unreachable, await client.CheckConnectionAsync(Token));
        Assert.Equal(RetimerConnectionStatus.Unreachable, await client.CheckConnectionAsync(Token));
    }
}
