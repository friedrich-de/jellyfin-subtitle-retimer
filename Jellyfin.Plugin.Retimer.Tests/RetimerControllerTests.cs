using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Tests.Support;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Retimer.Tests;

public class RetimerControllerTests
{
    private const string JimakuKey = "jimaku-secret-key-0123";
    private const string RetimerKey = "rt_secret-key-4567";

    private readonly StubSettings _settings = new();
    private readonly FakeJimaku _jimaku = new();
    private readonly FakeRetimer _retimer = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RetimerController Controller(bool administrator = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, administrator ? "admin" : "viewer") };
        if (administrator)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        }

        var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        return new RetimerController(_settings, _jimaku, _retimer)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                    RequestServices = services
                }
            }
        };
    }

    private static ProblemDetails Problem(ActionResult result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public void Status_says_what_is_set_up_and_never_carries_a_key()
    {
        _settings.Current = new PluginConfiguration { JimakuApiKey = JimakuKey, RetimerUrl = "https://retimer.example.org", RetimerApiKey = RetimerKey };

        var status = Controller().GetStatus().Value!;

        Assert.True(status.JimakuConfigured);
        Assert.True(status.RetimerConfigured);
        Assert.False(status.IsAdministrator);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), status.Version);

        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain(JimakuKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(RetimerKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("retimer.example.org", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_with_nothing_set_up_says_so_and_knows_an_administrator()
    {
        var status = Controller(administrator: true).GetStatus().Value!;

        Assert.False(status.JimakuConfigured);
        Assert.False(status.RetimerConfigured);
        Assert.True(status.IsAdministrator);
    }

    [Fact]
    public void The_retimer_counts_as_set_up_only_with_both_address_and_key()
    {
        _settings.Current = new PluginConfiguration { RetimerUrl = "https://retimer.example.org" };
        Assert.False(Controller().GetStatus().Value!.RetimerConfigured);

        _settings.Current = new PluginConfiguration { RetimerApiKey = RetimerKey };
        Assert.False(Controller().GetStatus().Value!.RetimerConfigured);
    }

    [Fact]
    public async Task Finding_entries_passes_ids_and_title_on()
    {
        var result = await Controller().FindEntries(154587, " tv:209867 ", "Frieren", null, Token);

        var entries = Assert.IsAssignableFrom<IReadOnlyList<JimakuEntry>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(4321, Assert.Single(entries).Id);
        var lookup = Assert.Single(_jimaku.Lookups);
        Assert.Equal(154587, lookup.AnilistId);
        Assert.Equal("tv:209867", lookup.TmdbId);
        Assert.Equal("Frieren", lookup.Title);
        Assert.Null(lookup.Anime);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "  ", "")]
    [InlineData(-1, null, null)]
    [InlineData(null, "209867", null)]
    [InlineData(null, "tv:abc", "Frieren")]
    public async Task Finding_entries_refuses_bad_input_without_calling_Jimaku(int? anilistId, string? tmdbId, string? query)
    {
        var result = await Controller().FindEntries(anilistId, tmdbId, query, null, Token);

        Problem(result.Result!, StatusCodes.Status400BadRequest);
        Assert.Empty(_jimaku.Lookups);
    }

    [Theory]
    [InlineData(JimakuError.NotConfigured, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(JimakuError.Unauthorized, StatusCodes.Status502BadGateway)]
    [InlineData(JimakuError.Unavailable, StatusCodes.Status502BadGateway)]
    [InlineData(JimakuError.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(JimakuError.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(JimakuError.RateLimited, StatusCodes.Status429TooManyRequests)]
    public async Task A_Jimaku_failure_becomes_a_fitting_status(JimakuError error, int expectedStatus)
    {
        _jimaku.Failure = new JimakuException(error, "What went wrong.");

        var entries = await Controller().FindEntries(1, null, null, null, Token);
        var files = await Controller().GetFiles(1, null, Token);

        Assert.Equal("What went wrong.", Problem(entries.Result!, expectedStatus).Detail);
        Assert.Equal(error.ToString(), Problem(files.Result!, expectedStatus).Title);
    }

    [Fact]
    public async Task A_rate_limit_tells_the_caller_how_long_to_wait()
    {
        _jimaku.Failure = new JimakuException(JimakuError.RateLimited, "Slow down.", TimeSpan.FromSeconds(41.2));
        var controller = Controller();

        var result = await controller.FindEntries(1, null, null, null, Token);

        Problem(result.Result!, StatusCodes.Status429TooManyRequests);
        Assert.Equal("42", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Files_are_listed_for_an_episode()
    {
        var result = await Controller().GetFiles(4321, 3, Token);

        var files = Assert.IsAssignableFrom<IReadOnlyList<JimakuFile>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("03.srt", Assert.Single(files).Name);
        Assert.Equal((4321L, (int?)3), Assert.Single(_jimaku.FileRequests));
    }

    [Fact]
    public async Task A_negative_episode_is_refused()
    {
        var result = await Controller().GetFiles(4321, -1, Token);

        Problem(result.Result!, StatusCodes.Status400BadRequest);
        Assert.Empty(_jimaku.FileRequests);
    }

    [Fact]
    public async Task The_Jimaku_check_reports_success_and_failure_in_words()
    {
        var ok = (await Controller(administrator: true).TestJimaku(Token)).Value!;
        Assert.True(ok.Ok);
        Assert.Equal("Ok", ok.Status);
        Assert.Equal(1, Assert.Single(_jimaku.Searches).AnilistId);

        _jimaku.Failure = new JimakuException(JimakuError.Unauthorized, "Jimaku rejected the API key.");
        var failed = (await Controller(administrator: true).TestJimaku(Token)).Value!;
        Assert.False(failed.Ok);
        Assert.Equal("Unauthorized", failed.Status);
        Assert.Equal("Jimaku rejected the API key.", failed.Message);
    }

    [Theory]
    [InlineData(RetimerConnectionStatus.Ok, true)]
    [InlineData(RetimerConnectionStatus.NotConfigured, false)]
    [InlineData(RetimerConnectionStatus.InvalidAddress, false)]
    [InlineData(RetimerConnectionStatus.Unreachable, false)]
    [InlineData(RetimerConnectionStatus.Unauthorized, false)]
    public async Task The_retimer_check_reports_each_outcome(RetimerConnectionStatus status, bool ok)
    {
        _retimer.Status = status;

        var response = (await Controller(administrator: true).TestRetimer(Token)).Value!;

        Assert.Equal(ok, response.Ok);
        Assert.Equal(status.ToString(), response.Status);
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
    }

    [Fact]
    public async Task The_user_page_is_served_as_html_and_holds_no_settings()
    {
        _settings.Current = new PluginConfiguration { JimakuApiKey = JimakuKey, RetimerApiKey = RetimerKey };
        var controller = Controller();

        var file = Assert.IsType<FileStreamResult>(controller.GetUserPage());

        Assert.Equal("text/html; charset=utf-8", file.ContentType);
        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
        var policy = controller.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        using var reader = new StreamReader(file.FileStream);
        var html = await reader.ReadToEndAsync(Token);
        Assert.Contains("<title>Subtitle Retimer</title>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(JimakuKey, html, StringComparison.Ordinal);
        Assert.DoesNotContain(RetimerKey, html, StringComparison.Ordinal);
    }

    // The rules below are the plugin's whole access model, so a change to them should be a deliberate one.
    [Fact]
    public void Every_endpoint_needs_a_login_except_the_empty_page()
    {
        Assert.NotNull(typeof(RetimerController).GetCustomAttribute<AuthorizeAttribute>());

        var anonymous = Actions().Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null).Select(m => m.Name);

        Assert.Equal([nameof(RetimerController.GetUserPage)], anonymous);
    }

    [Fact]
    public void Only_the_connection_checks_are_for_administrators_and_they_all_are()
    {
        var elevated = Actions()
            .Where(m => m.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == Policies.RequiresElevation))
            .Select(m => m.Name)
            .Order();

        Assert.Equal([nameof(RetimerController.TestJimaku), nameof(RetimerController.TestRetimer)], elevated);
    }

    [Fact]
    public void No_endpoint_returns_the_settings()
    {
        foreach (var action in Actions())
        {
            Assert.DoesNotContain(nameof(PluginConfiguration), action.ReturnType.ToString(), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<MethodInfo> Actions() => typeof(RetimerController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes().Any(a => a is IActionHttpMethodProvider));
}
