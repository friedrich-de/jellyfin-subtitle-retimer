using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Retimer;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Retimer.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly World _w = new();
    private readonly FakeRetimer _connection = new();
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _service = new SettingsService(_w.Settings, _w.Library, _w.Sources, _w.Picker, _connection, _w.Retimer, _w.Queue, _w.Watch, _w.Store);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _w.Dispose();

    [Fact]
    public void The_settings_are_shown_without_any_key()
    {
        _w.Settings.Current = new PluginConfiguration
        {
            JimakuApiKey = "jimaku-secret-0123",
            OpenSubtitlesApiKey = "os-secret-4567",
            OpenSubtitlesUsername = "al",
            OpenSubtitlesPassword = "os-password-89",
            RetimerUrl = "https://retimer.example.org",
            RetimerApiKey = "rt_secret-key",
            OpenRouterApiKey = "sk-or-secret"
        };

        var view = _service.Get();
        var json = JsonSerializer.Serialize(view);

        Assert.True(view.HasJimakuApiKey && view.HasOpenSubtitlesApiKey && view.HasOpenSubtitlesPassword && view.HasRetimerApiKey && view.HasOpenRouterApiKey);
        Assert.Equal("al", view.OpenSubtitlesUsername);
        Assert.Equal("https://retimer.example.org", view.RetimerUrl);
        foreach (var secret in new[] { "jimaku-secret-0123", "os-secret-4567", "os-password-89", "rt_secret-key", "sk-or-secret" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        Assert.Equal("https://github.com/friedrich-de/subtitle-retimer", view.RetimerProjectUrl);
        Assert.Contains(view.Libraries, library => library.Name == "Anime" && !library.Hidden);
    }

    [Fact]
    public void A_fresh_install_has_Retimer_API_off_audio_never_and_Haiku_as_the_model()
    {
        var view = _service.Get();

        Assert.False(view.RetimerEnabled);
        Assert.Equal("never", view.SendAudio);
        Assert.False(view.ConvertPictureSubtitles);
        Assert.Equal("ja", view.SubtitleLanguage);
        Assert.Equal("managers", view.FetchAccess);
        Assert.Equal("anthropic/claude-haiku-5.5", view.ModelInUse);
        Assert.Equal("Haiku 5.5", Assert.Single(view.Models).Name);
        Assert.True(view.MaxJobsPerDay > 0 && view.MaxCostPerDay > 0);
    }

    [Fact]
    public void A_key_field_left_empty_keeps_the_stored_key_and_a_typed_one_replaces_it()
    {
        _w.Settings.Current = new PluginConfiguration { JimakuApiKey = "old-jimaku", RetimerApiKey = "old-retimer", OpenRouterApiKey = "old-openrouter" };

        var (view, error) = _service.Save(new SettingsUpdate { JimakuApiKey = "", RetimerApiKey = "  new-retimer  ", RetimerEnabled = true, ClearSecrets = ["OpenRouterApiKey"] });

        Assert.Null(error);
        Assert.Equal("old-jimaku", _w.Settings.Reveal(_w.Settings.Current.JimakuApiKey));
        Assert.Equal("new-retimer", _w.Settings.Reveal(_w.Settings.Current.RetimerApiKey));
        Assert.Equal(string.Empty, _w.Settings.Current.OpenRouterApiKey);

        // What is stored is encrypted, also the key that was there before in plain.
        Assert.StartsWith(KeyProtector.Prefix, _w.Settings.Current.JimakuApiKey, StringComparison.Ordinal);
        Assert.StartsWith(KeyProtector.Prefix, _w.Settings.Current.RetimerApiKey, StringComparison.Ordinal);
        Assert.True(_w.Settings.Current.RetimerEnabled);
        Assert.True(view!.HasJimakuApiKey && view.HasRetimerApiKey);
        Assert.False(view.HasOpenRouterApiKey);
        Assert.Equal(1, _w.Settings.Saves);
        Assert.True(_w.Library.Invalidations > 0);
    }

    [Fact]
    public void What_a_request_leaves_out_stays_as_it_is()
    {
        _w.Settings.Current = new PluginConfiguration { RemoveSongs = true, SendAudio = "always", MaxJobsPerDay = 7, SubtitleLanguage = "en" };

        _service.Save(new SettingsUpdate { RemoveStyling = true });

        Assert.True(_w.Settings.Current.RemoveSongs);
        Assert.True(_w.Settings.Current.RemoveStyling);
        Assert.Equal("always", _w.Settings.Current.SendAudio);
        Assert.Equal(7, _w.Settings.Current.MaxJobsPerDay);
        Assert.Equal("en", _w.Settings.Current.SubtitleLanguage);
    }

    [Theory]
    [InlineData("{\"SubtitleLanguage\":\"klingon\"}")]
    [InlineData("{\"PreferredSource\":\"subdl\"}")]
    [InlineData("{\"SendAudio\":\"sometimes\"}")]
    [InlineData("{\"OutputFormat\":\"vtt\"}")]
    [InlineData("{\"Models\":[]}")]
    [InlineData("{\"FetchAccess\":\"nobody\"}")]
    [InlineData("{\"RetimerUrl\":\"ftp://retimer.example.org\"}")]
    [InlineData("{\"RetimerUrl\":\"https://user:pw@retimer.example.org\"}")]
    [InlineData("{\"Models\":[{\"Id\":\"bad id with spaces\"}]}")]
    [InlineData("{\"Models\":[{\"Id\":\"\"}]}")]
    [InlineData("{\"MaxJobsPerDay\":-1}")]
    [InlineData("{\"MaxCostPerDay\":-0.5}")]
    [InlineData("{\"ClearSecrets\":[\"PluginId\"]}")]
    public void A_value_that_makes_no_sense_is_refused_and_nothing_is_saved(string json)
    {
        var update = JsonSerializer.Deserialize<SettingsUpdate>(json)!;

        var (view, error) = _service.Save(update);

        Assert.Null(view);
        Assert.Equal(StatusCodes.Status400BadRequest, error!.Status);
        Assert.Equal(0, _w.Settings.Saves);
    }

    [Fact]
    public void Models_can_be_added_and_the_model_in_use_is_always_one_of_the_list()
    {
        var update = new SettingsUpdate
        {
            Models = [new ModelEntry { Id = "anthropic/claude-haiku-5.5", Name = "Haiku 5.5" }, new ModelEntry { Id = " google/gemini-3-flash ", Name = "" }, new ModelEntry { Id = "google/gemini-3-flash" }],
            ModelInUse = "google/gemini-3-flash"
        };

        Assert.Null(_service.Save(update).Error);
        Assert.Equal(["anthropic/claude-haiku-5.5", "google/gemini-3-flash"], _w.Settings.Current.Models.Select(m => m.Id));
        Assert.Equal("google/gemini-3-flash", _w.Settings.Current.Models[1].Name);
        Assert.Equal("google/gemini-3-flash", _w.Settings.Current.ModelInUse);

        Assert.Null(_service.Save(new SettingsUpdate { Models = [new ModelEntry { Id = "anthropic/claude-haiku-5.5" }] }).Error);
        Assert.Equal("anthropic/claude-haiku-5.5", _w.Settings.Current.ModelInUse);

        // The list is never empty, because the plugin picks no file without a model.
        Assert.Equal(StatusCodes.Status400BadRequest, _service.Save(new SettingsUpdate { Models = [] }).Error!.Status);
        Assert.Equal("anthropic/claude-haiku-5.5", _w.Settings.Current.ModelInUse);
    }

    [Fact]
    public void A_settings_file_without_a_model_gets_the_default_one_on_the_next_save()
    {
        _w.Settings.Current = new PluginConfiguration { Models = [], ModelInUse = string.Empty };

        Assert.Null(_service.Save(new SettingsUpdate { RemoveSongs = true }).Error);

        Assert.Equal("anthropic/claude-haiku-5.5", Assert.Single(_w.Settings.Current.Models).Id);
        Assert.Equal("anthropic/claude-haiku-5.5", _w.Settings.Current.ModelInUse);
    }

    [Fact]
    public void Libraries_can_be_hidden_from_the_page()
    {
        Assert.Null(_service.Save(new SettingsUpdate { HiddenLibraryIds = [FakeLibrary.LibraryId, FakeLibrary.LibraryId, Guid.Empty] }).Error);

        Assert.Equal([FakeLibrary.LibraryId], _w.Settings.Current.HiddenLibraryIds);
        Assert.True(_service.Get().Libraries.Single().Hidden);
    }

    [Fact]
    public async Task The_checks_say_in_one_sentence_what_they_found()
    {
        _w.Settings.Current.OpenRouterApiKey = "or-key";
        _w.Settings.Current.Models = [new ModelEntry { Id = "test/model", Name = "Test Model" }];
        _w.Model.Tools["answer"] = _ => new JsonObject { ["episode"] = 7 };

        var jimaku = await _service.TestAsync("jimaku", null, Token);
        var retimer = await _service.TestAsync("Retimer", null, Token);
        var model = await _service.TestAsync("model", "test/model", Token);

        Assert.True(jimaku!.Ok);
        Assert.True(retimer!.Ok);
        Assert.Contains("up to 6 subtitle tracks", retimer.Message, StringComparison.Ordinal);
        Assert.True(model!.Ok);
        Assert.Contains("Test Model answered", model.Message, StringComparison.Ordinal);
        Assert.Null(await _service.TestAsync("subdl", null, Token));

        // The question to the model is paid with the server's key, so the day's use counts it.
        Assert.Equal(_w.Model.CostPerCall, _w.Queue.Usage().CostUsd, 6);
    }

    [Fact]
    public async Task An_older_Retimer_API_is_named_as_such_by_the_check()
    {
        _w.Retimer.Features = new RetimerFeatures { Ocr = true };

        var result = await _service.TestAsync("retimer", null, Token);

        Assert.True(result!.Ok);
        Assert.Contains("older release", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_fails_the_Retimer_API_check()
    {
        _connection.Status = RetimerConnectionStatus.Unauthorized;

        var result = await _service.TestAsync("retimer", null, Token);

        Assert.False(result!.Ok);
        Assert.Equal("Unauthorized", result.Status);
    }

    [Fact]
    public async Task The_model_test_asks_only_models_of_the_list_and_passes_on_a_failure()
    {
        _w.Settings.Current.OpenRouterApiKey = "or-key";
        _w.Settings.Current.Models = [new ModelEntry { Id = "test/model", Name = "Test Model" }];

        var stranger = await _service.TestAsync("model", "openai/some-expensive-model", Token);
        Assert.False(stranger!.Ok);
        Assert.Empty(_w.Model.Calls);

        var noTool = await _service.TestAsync("model", "test/model", Token);
        Assert.False(noTool!.Ok);
        Assert.Contains("did not call the tool", noTool.Message, StringComparison.Ordinal);

        _w.Model.Failure = new ModelException(ModelError.Unauthorized, "OpenRouter rejected the key.");
        var rejected = await _service.TestAsync("model", null, Token);
        Assert.False(rejected!.Ok);

        _w.Settings.Current.OpenRouterApiKey = string.Empty;
        Assert.Contains("OpenRouter API key first", (await _service.TestAsync("model", "test/model", Token))!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Watch_mode_keeps_its_days_within_bounds_and_watches_only_shows_that_come_after_it_was_switched_on()
    {
        var at = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        _w.Now = at;

        Assert.NotNull(_service.Save(new SettingsUpdate { WatchDays = -1 }).Error);
        Assert.NotNull(_service.Save(new SettingsUpdate { WatchDays = 4000 }).Error);
        Assert.Equal(30, _service.Get().WatchDays);
        Assert.Equal(3.0, _service.Get().WatchEveryHours);

        var (view, error) = _service.Save(new SettingsUpdate { WatchNewShows = true, WatchDays = 0 });
        Assert.Null(error);
        Assert.True(view!.WatchNewShows);
        Assert.Equal(0, view.WatchDays);
        Assert.Equal(at, _w.Store.Read(data => data.WatchNewShowsSince));

        // Saving again while it is on keeps the day it was switched on.
        _w.Now = at.AddDays(2);
        _service.Save(new SettingsUpdate { WatchNewShows = true, WatchDays = 14 });
        Assert.Equal(at, _w.Store.Read(data => data.WatchNewShowsSince));

        _service.Save(new SettingsUpdate { WatchNewShows = false });
        _service.Save(new SettingsUpdate { WatchNewShows = true });
        Assert.Equal(at.AddDays(2), _w.Store.Read(data => data.WatchNewShowsSince));
    }
}
