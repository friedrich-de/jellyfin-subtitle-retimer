using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Tests.Support;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Retimer.Tests;

public sealed class RetimerControllerTests : IDisposable
{
    private readonly World _w = new();
    private readonly FakeCallers _callers = new();
    private readonly PluginServices _services;

    public RetimerControllerTests()
    {
        _callers.Caller = _w.Admin;
        _services = new PluginServices(
            _w.Page,
            new SettingsService(_w.Settings, _w.Library, _w.Sources, _w.Picker, new FakeRetimer(), _w.Retimer, _w.Queue),
            _callers);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    private static ControllerContext Context() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
        }
    };

    private RetimerController Controller() => new(_services) { ControllerContext = Context() };

    private RetimerSettingsController SettingsController() => new(_services) { ControllerContext = Context() };

    private static ProblemDetails Problem(ActionResult result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private static T Value<T>(ActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static string Read(ActionResult result, string expectedType)
    {
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(expectedType, file.ContentType);
        using var reader = new StreamReader(file.FileStream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_page_and_its_two_files_are_served_with_a_policy_that_allows_only_this_server()
    {
        var controller = Controller();

        var page = Read(controller.GetUserPage(), "text/html; charset=utf-8");
        var script = Read(controller.GetScript(), "text/javascript; charset=utf-8");
        var styles = Read(controller.GetStyles(), "text/css; charset=utf-8");

        Assert.Contains("<script src=\"Retimer/app.js\"></script>", page, StringComparison.Ordinal);
        Assert.Contains("href=\"Retimer/app.css\"", page, StringComparison.Ordinal);
        Assert.Contains("'/Retimer/Status'", script, StringComparison.Ordinal);
        Assert.Contains("--primary: #00a4dc", styles, StringComparison.Ordinal);

        var headers = controller.Response.Headers;
        var policy = headers.ContentSecurityPolicy.ToString();
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("img-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public void The_page_needs_no_inline_script_or_style_so_the_policy_can_stay_strict()
    {
        var page = Read(Controller().GetUserPage(), "text/html; charset=utf-8");
        var script = Read(Controller().GetScript(), "text/javascript; charset=utf-8");

        Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" style=", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" onclick=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", page, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", page, StringComparison.Ordinal);

        // Text from the server is always set as text, never as markup.
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setAttribute('style'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_page_and_its_files_can_be_fetched_without_a_login()
    {
        Assert.NotNull(typeof(RetimerController).GetCustomAttribute<AuthorizeAttribute>());

        var open = typeof(RetimerController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name)
            .Order()
            .ToList();

        Assert.Equal(["GetScript", "GetStyles", "GetUserPage"], open);
    }

    [Fact]
    public void Every_settings_call_needs_an_administrator()
    {
        var authorize = typeof(RetimerSettingsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(Policies.RequiresElevation, authorize?.Policy);
        var actions = typeof(RetimerSettingsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        Assert.All(actions, action => Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>()));
        Assert.All(actions, action => Assert.NotNull(action.GetCustomAttribute<HttpMethodAttribute>()));

        // And nothing about settings or tests sits on the controller every user reaches.
        var open = typeof(RetimerController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
            .Select(a => a.Template ?? string.Empty);
        Assert.DoesNotContain(open, template => template.StartsWith("Settings", StringComparison.OrdinalIgnoreCase) || template.StartsWith("Test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_request_without_a_user_behind_it_gets_nothing()
    {
        var videos = _w.FateZero();
        _callers.Caller = null;
        var controller = Controller();

        Problem(await controller.GetStatus(), StatusCodes.Status403Forbidden);
        Problem(await controller.GetLibraries(), StatusCodes.Status403Forbidden);
        Problem(await controller.GetShows(FakeLibrary.LibraryId), StatusCodes.Status403Forbidden);
        Problem(await controller.GetShow(videos[0].ShowId), StatusCodes.Status403Forbidden);
        Problem(await controller.GetJobs(), StatusCodes.Status403Forbidden);
        Problem(await controller.StartJobs(new StartJobsRequest { ItemIds = [videos[0].Id] }), StatusCodes.Status403Forbidden);
        Problem(await controller.GetFiles(videos[0].Id, Token), StatusCodes.Status403Forbidden);
        Problem(await controller.RemoveSubtitle(videos[0].Id, Token), StatusCodes.Status403Forbidden);
        Problem(await controller.SetLanguage(videos[0].ShowId, new SetLanguageRequest { Language = "en" }), StatusCodes.Status403Forbidden);
        Problem(await controller.ClearJobs(), StatusCodes.Status403Forbidden);
        Assert.Empty(_w.Queue.Since(0));
    }

    [Fact]
    public async Task A_viewer_can_look_but_not_fetch()
    {
        var videos = _w.FateZero();
        _callers.Caller = _w.Viewer;
        var controller = Controller();

        Assert.False(Value<StatusView>(await controller.GetStatus()).MayFetch);
        Assert.Equal("Fate/Zero", Value<ShowDetailView>(await controller.GetShow(videos[0].ShowId)).Name);
        var refused = Problem(await controller.StartJobs(new StartJobsRequest { ItemIds = [videos[0].Id] }), StatusCodes.Status403Forbidden);
        Assert.Contains("may not get or change subtitles", refused.Detail, StringComparison.Ordinal);
        Problem(await controller.GetFiles(videos[0].Id, Token), StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task What_the_server_spent_is_only_told_to_those_who_may_fetch()
    {
        var videos = _w.FateZero();
        _callers.Caller = _w.Manager;
        Value<StartView>(await Controller().StartJobs(new StartJobsRequest { ItemIds = [videos[0].Id] }));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.NotNull(Value<StatusView>(await Controller().GetStatus()).Usage);

        _callers.Caller = _w.Viewer;
        var controller = Controller();
        var jobs = Value<JobsView>(await controller.GetJobs());

        // The viewer sees that the job ran, since the show is theirs to see, but no figures.
        var job = Assert.Single(jobs.Jobs);
        Assert.Equal(0, job.CostUsd);
        Assert.False(job.MayCancel);
        Assert.Equal(string.Empty, job.StepText);
        Assert.Null(jobs.Usage);
        Assert.Null(Value<StatusView>(await controller.GetStatus()).Usage);
    }

    [Fact]
    public async Task A_manager_gets_a_subtitle_through_the_endpoints()
    {
        var videos = _w.FateZero();
        _callers.Caller = _w.Manager;
        var controller = Controller();

        var started = Value<StartView>(await controller.StartJobs(new StartJobsRequest { ItemIds = [videos[0].Id] }));
        await _w.Queue.Idle.WaitAsync(TimeSpan.FromSeconds(20), Token);
        var jobs = Value<JobsView>(await controller.GetJobs());
        var show = Value<ShowDetailView>(await controller.GetShow(videos[0].ShowId));

        Assert.Equal(1, started.Started);
        Assert.Equal("raw", Assert.Single(jobs.Jobs).State);
        Assert.Equal(1, jobs.Usage!.Jobs);
        Assert.Equal("raw", show.Seasons[0].Episodes[0].State);
        Assert.Empty(Value<JobsView>(await controller.GetJobs(jobs.Version)).Jobs);

        Assert.IsType<NoContentResult>(await controller.RemoveSubtitle(videos[0].Id, Token));
        Assert.Equal("none", Value<ShowDetailView>(await controller.GetShow(videos[0].ShowId)).Seasons[0].Episodes[0].State);
    }

    [Fact]
    public async Task What_does_not_exist_is_answered_with_404_and_a_sentence()
    {
        var controller = Controller();

        Assert.NotNull(Problem(await controller.GetShow(Guid.NewGuid()), StatusCodes.Status404NotFound).Detail);
        Problem(await controller.GetShows(Guid.NewGuid()), StatusCodes.Status404NotFound);
        Problem(await controller.CancelJob("nope"), StatusCodes.Status404NotFound);
        Problem(await controller.RetryJob("nope"), StatusCodes.Status404NotFound);
        Problem(await controller.GetFiles(Guid.NewGuid(), Token), StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task The_settings_endpoints_never_answer_with_a_key()
    {
        _w.Settings.Current = new PluginConfiguration { JimakuApiKey = "jimaku-secret-0123", RetimerApiKey = "rt_secret-key-4567", OpenRouterApiKey = "sk-or-secret-89", OpenSubtitlesPassword = "os-password" };
        var controller = SettingsController();

        var read = JsonSerializer.Serialize(Value<SettingsView>(controller.GetSettings()));
        var saved = JsonSerializer.Serialize(Value<SettingsView>(controller.SaveSettings(new SettingsUpdate { RetimerApiKey = "rt_new-secret", RemoveSongs = true })));
        var tested = JsonSerializer.Serialize(Value<ConnectionTestResponse>(await controller.Test("retimer", null, Token)));

        foreach (var answer in new[] { read, saved, tested })
        {
            foreach (var secret in new[] { "jimaku-secret-0123", "rt_secret-key-4567", "rt_new-secret", "sk-or-secret-89", "os-password" })
            {
                Assert.DoesNotContain(secret, answer, StringComparison.Ordinal);
            }
        }

        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("rt_new-secret", _w.Settings.Reveal(_w.Settings.Current.RetimerApiKey));
        Problem(controller.SaveSettings(new SettingsUpdate { SendAudio = "sometimes" }), StatusCodes.Status400BadRequest);
        Problem(await controller.Test("nothing", null, Token), StatusCodes.Status404NotFound);
    }
}
