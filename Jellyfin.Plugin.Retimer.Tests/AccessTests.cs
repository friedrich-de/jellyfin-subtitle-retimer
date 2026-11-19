using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Sources;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jellyfin.Plugin.Retimer.Tests;

/// <summary>
/// Who may call what: checked on the plugin's own code, so a new endpoint cannot slip out without a login.
/// </summary>
public class AccessTests
{
    private static readonly string[] _knownControllers = [nameof(RetimerController), nameof(RetimerSettingsController)];

    private static readonly string[] _openActions =
    [
        "RetimerController.GetIcon", "RetimerController.GetManifest", "RetimerController.GetScript",
        "RetimerController.GetStyles", "RetimerController.GetUserPage", "RetimerController.GetWorker"
    ];

    private static readonly Type[] _controllers = typeof(Plugin).Assembly.GetTypes()
        .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
        .ToArray();

    [Fact]
    public void The_plugin_has_exactly_the_two_known_controllers()
    {
        Assert.Equal(_knownControllers, _controllers.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Only_the_page_and_its_files_answer_without_a_login()
    {
        foreach (var controller in _controllers)
        {
            Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
            Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        var open = _controllers
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(method => method.DeclaringType!.Name + "." + method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(_openActions, open);
    }

    [Fact]
    public void The_settings_and_the_tests_are_for_administrators_only()
    {
        var policy = typeof(RetimerSettingsController).GetCustomAttribute<AuthorizeAttribute>()!.Policy;

        Assert.Equal(Policies.RequiresElevation, policy);
        foreach (var method in typeof(RetimerSettingsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttributes<HttpMethodAttribute>().Any())
            {
                Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>());
            }
        }
    }

    [Theory]
    [InlineData("api.opensubtitles.com", true)]
    [InlineData("vip-api.opensubtitles.com", true)]
    [InlineData("opensubtitles.com", true)]
    [InlineData(" API.OpenSubtitles.com ", true)]
    [InlineData("evil-opensubtitles.com", false)]
    [InlineData("opensubtitles.com.evil.org", false)]
    [InlineData("evil.org#.opensubtitles.com", false)]
    [InlineData("evil.org/x.opensubtitles.com", false)]
    [InlineData("evil.org?.opensubtitles.com", false)]
    [InlineData("user@api.opensubtitles.com", false)]
    [InlineData("api.opensubtitles.com:8443", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_login_answer_may_only_send_the_key_on_to_an_opensubtitles_host(string? host, bool taken)
    {
        Assert.Equal(taken, OpenSubtitlesSource.TryLoginBaseAddress(host, out var address));
        if (taken)
        {
            Assert.Equal("https", address!.Scheme);
            Assert.EndsWith("opensubtitles.com", address.Host, StringComparison.Ordinal);
        }
    }
}
