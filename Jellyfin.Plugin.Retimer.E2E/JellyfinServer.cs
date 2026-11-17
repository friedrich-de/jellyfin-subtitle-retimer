using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.E2E;

/// <summary>
/// A throwaway Jellyfin server with the packaged plugin installed the way a user installs it:
/// from a plugin repository, through Jellyfin's own installer, followed by a restart.
/// It also plays the plugin repository, a stand-in retimer and a stand-in Jimaku on a local port.
/// </summary>
/// <remarks>
/// Needs two environment variables, otherwise the tests are skipped:
/// RETIMER_E2E_JELLYFIN (the server binary) and RETIMER_E2E_PACKAGE_DIR (the output of dev/package.sh).
/// Optional: RETIMER_E2E_JELLYFIN_WEB (web client folder), RETIMER_E2E_FFMPEG, RETIMER_E2E_KEEP=1
/// (keep the server's folder), RETIMER_E2E_REQUIRED=1 (fail instead of skipping).
/// </remarks>
public class JellyfinServer : IAsyncLifetime
{
    public const string AdminName = "admin";
    public const string ViewerName = "viewer";
    public const string GoodRetimerKey = "rt_e2e-good-key";
    public const string GoodJimakuKey = "e2e-good-jimaku-key";
    private const string AdminPassword = "e2e-admin-password";
    private const string ViewerPassword = "e2e-viewer-password";
    private const string ClientHeader = "MediaBrowser Client=\"retimer-e2e\", Device=\"e2e\", DeviceId=\"retimer-e2e-1\", Version=\"1\"";

    private static readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(180);

    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly StringBuilder _serverOutput = new();
    private readonly CancellationTokenSource _stopping = new();
    private string _root = string.Empty;
    private Process? _process;
    private HttpListener? _listener;
    private Task? _listenerLoop;
    private string _zipName = string.Empty;
    private byte[] _zip = [];
    private string _manifest = string.Empty;
    private bool _disposed;

    public static string? JellyfinPath => Environment.GetEnvironmentVariable("RETIMER_E2E_JELLYFIN");

    public static string? PackageDirectory => Environment.GetEnvironmentVariable("RETIMER_E2E_PACKAGE_DIR");

    public static bool Required => Environment.GetEnvironmentVariable("RETIMER_E2E_REQUIRED") == "1";

    public static bool Enabled => !string.IsNullOrEmpty(JellyfinPath) && !string.IsNullOrEmpty(PackageDirectory);

    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1/");

    /// <summary>Gets the address of this fixture's own listener: plugin repository and stand-in retimer.</summary>
    public Uri HelperAddress { get; private set; } = new("http://127.0.0.1/");

    public string RetimerUrl => new Uri(HelperAddress, "retimer").AbsoluteUri;

    public string ServerVersion { get; private set; } = string.Empty;

    public string PluginGuid { get; private set; } = string.Empty;

    public string PluginName { get; private set; } = string.Empty;

    public string PluginVersion { get; private set; } = string.Empty;

    public string AdminToken { get; private set; } = string.Empty;

    public string ViewerToken { get; private set; } = string.Empty;

    public string DataDirectory => Path.Combine(_root, "data");

    /// <summary>Gets the bearer keys the stand-in retimer has seen, newest last.</summary>
    public List<string?> RetimerKeysSeen { get; } = [];

    /// <summary>Gets what the stand-in Jimaku was asked, oldest first: path with query, key, client name.</summary>
    public List<(string PathAndQuery, string? Authorization, string? UserAgent)> JimakuRequests { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the plugin talks to this fixture's stand-in (the default)
    /// or to the real jimaku.cc.
    /// </summary>
    protected virtual bool UseStandInJimaku => true;

    /// <summary>
    /// Gets a value indicating whether this fixture starts a server at all.
    /// </summary>
    protected virtual bool ShouldStart => Enabled;

    /// <summary>
    /// Call first in every test: skips it when no server was asked for.
    /// </summary>
    public static void RequireEnabled()
    {
        Assert.SkipUnless(Enabled, "Set RETIMER_E2E_JELLYFIN and RETIMER_E2E_PACKAGE_DIR, or run dev/e2e.sh.");
    }

    public virtual async ValueTask InitializeAsync()
    {
        if (!Enabled && Required)
        {
            throw new InvalidOperationException("RETIMER_E2E_REQUIRED=1, but RETIMER_E2E_JELLYFIN or RETIMER_E2E_PACKAGE_DIR is not set.");
        }

        if (!ShouldStart)
        {
            return;
        }

        try
        {
            LoadPackage();
            StartHelper();

            _root = Path.Combine(Path.GetTempPath(), "retimer-e2e-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Path.Combine(_root, "config"));
            var port = FreePort();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            await File.WriteAllTextAsync(
                Path.Combine(_root, "config", "network.xml"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                  <InternalHttpPort>{port}</InternalHttpPort>
                  <PublicHttpPort>{port}</PublicHttpPort>
                  <AutoDiscovery>false</AutoDiscovery>
                  <EnableUPnP>false</EnableUPnP>
                  <EnableIPv6>false</EnableIPv6>
                  <LocalNetworkAddresses>
                    <string>127.0.0.1</string>
                  </LocalNetworkAddresses>
                </NetworkConfiguration>
                """);

            // First run: finish the wizard, then install the plugin from the repository.
            StartServer();
            await WaitUntilUpAsync();
            await CompleteWizardAsync();
            AdminToken = await LoginAsync(AdminName, AdminPassword);
            await InstallPluginAsync();

            // A plugin is loaded at start, so the server has to go down and come up again.
            await ShutdownServerAsync();
            StartServer();
            await WaitUntilUpAsync();
            AdminToken = await LoginAsync(AdminName, AdminPassword);

            using var created = await SendAsync(HttpMethod.Post, "Users/New", AdminToken, new JsonObject { ["Name"] = ViewerName, ["Password"] = ViewerPassword });
            created.EnsureSuccessStatusCode();
            ViewerToken = await LoginAsync(ViewerName, ViewerPassword);
        }
        catch (Exception ex)
        {
            var output = TailOfServerOutput();
            await DisposeAsync();
            throw new InvalidOperationException("The Jellyfin test server could not be prepared: " + ex.Message + "\n--- server output ---\n" + output, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync();
        KillServer();
        _listener?.Close();
        if (_listenerLoop is not null)
        {
            try
            {
                await _listenerLoop;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // The listener was closed while it waited for a request.
            }
        }

        _http.Dispose();
        _stopping.Dispose();
        if (_root.Length > 0 && Directory.Exists(_root) && Environment.GetEnvironmentVariable("RETIMER_E2E_KEEP") != "1")
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Sends a request to the server, as the owner of <paramref name="token"/> or without a login.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
        request.Headers.TryAddWithoutValidation("Authorization", token is null ? ClientHeader : ClientHeader + $", Token=\"{token}\"");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<JsonNode?> GetJsonAsync(string path, string? token)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} answered {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text);
    }

    /// <summary>
    /// Saves the plugin's settings the way the settings page does.
    /// </summary>
    public async Task SaveSettingsAsync(string jimakuApiKey, string retimerUrl, string retimerApiKey)
    {
        var settings = new JsonObject { ["JimakuApiKey"] = jimakuApiKey, ["RetimerUrl"] = retimerUrl, ["RetimerApiKey"] = retimerApiKey };
        using var response = await SendAsync(HttpMethod.Post, $"Plugins/{PluginGuid}/Configuration", AdminToken, settings);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public string TailOfServerOutput()
    {
        lock (_serverOutput)
        {
            var lines = _serverOutput.ToString().Split('\n');
            return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 60)));
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void LoadPackage()
    {
        var manifestPath = Path.Combine(PackageDirectory!, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsArray();
        var package = Assert.Single(manifest)!;
        var version = Assert.Single(package["versions"]!.AsArray())!;
        PluginGuid = package["guid"]!.GetValue<string>();
        PluginName = package["name"]!.GetValue<string>();
        PluginVersion = version["version"]!.GetValue<string>();
        _zipName = Path.GetFileName(new Uri(version["sourceUrl"]!.GetValue<string>()).AbsolutePath);
        _zip = File.ReadAllBytes(Path.Combine(PackageDirectory!, _zipName));
        _manifest = manifest.ToJsonString();
    }

    private void StartHelper()
    {
        var port = FreePort();
        HelperAddress = new Uri($"http://127.0.0.1:{port}/");

        // The manifest is served as packaged, except that the download address points at this listener.
        var manifest = JsonNode.Parse(_manifest)!;
        manifest[0]!["versions"]![0]!["sourceUrl"] = new Uri(HelperAddress, "repo/" + _zipName).AbsoluteUri;
        _manifest = manifest.ToJsonString();

        _listener = new HttpListener();
        _listener.Prefixes.Add(HelperAddress.AbsoluteUri);
        _listener.Start();
        _listenerLoop = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested && _listener is { IsListening: true })
        {
            var context = await _listener.GetContextAsync();
            var path = context.Request.Url!.AbsolutePath;
            byte[] body;
            var contentType = "application/json";
            var status = 200;

            if (path == "/repo/manifest.json")
            {
                body = Encoding.UTF8.GetBytes(_manifest);
            }
            else if (path == "/repo/" + _zipName)
            {
                body = _zip;
                contentType = "application/zip";
            }
            else if (path == "/retimer/v1/jobs")
            {
                // The stand-in retimer: retimer-api answers this call with the key's jobs, or 401.
                var authorization = context.Request.Headers["Authorization"];
                lock (RetimerKeysSeen)
                {
                    RetimerKeysSeen.Add(authorization);
                }

                var accepted = authorization == "Bearer " + GoodRetimerKey;
                status = accepted ? 200 : 401;
                body = Encoding.UTF8.GetBytes(accepted ? """{"jobs":[]}""" : """{"detail":"A valid API key is required: Authorization: Bearer <key>."}""");
            }
            else if (path.StartsWith("/jimaku/api/", StringComparison.Ordinal))
            {
                (status, var json, var resetAfter) = AnswerAsJimaku(context.Request);
                body = Encoding.UTF8.GetBytes(json);
                if (resetAfter is not null)
                {
                    context.Response.Headers["x-ratelimit-reset-after"] = resetAfter;
                }
            }
            else
            {
                status = 404;
                body = Encoding.UTF8.GetBytes("{}");
            }

            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    /// <summary>
    /// The stand-in Jimaku. It answers in the shapes of Jimaku's published API description
    /// (jimaku.cc/api/openapi.json): one known series, its files, and the documented errors.
    /// </summary>
    private (int Status, string Json, string? ResetAfter) AnswerAsJimaku(HttpListenerRequest request)
    {
        const string Frieren = """
            {"id":4321,"name":"Sousou no Frieren","english_name":"Frieren: Beyond Journey’s End","japanese_name":"葬送のフリーレン",
             "anilist_id":154587,"tmdb_id":"tv:209867","creator_id":7,"last_modified":"2024-03-22T15:04:05.123456Z",
             "flags":{"anime":true,"movie":false,"adult":false,"external":false,"unverified":false},"notes":null}
            """;
        const string Episode3 = """{"name":"[Group] Sousou no Frieren - 03.ja.srt","url":"https://jimaku.cc/entry/4321/download/x-03.srt","size":34567,"last_modified":"2023-10-14T12:00:00Z"}""";
        const string Batch = """{"name":"Frieren S01 (1-28).zip","url":"https://jimaku.cc/entry/4321/download/batch.zip","size":1048576,"last_modified":"2024-03-22T15:04:05Z"}""";

        var authorization = request.Headers["Authorization"];
        lock (JimakuRequests)
        {
            JimakuRequests.Add((request.Url!.PathAndQuery, authorization, request.UserAgent));
        }

        // Jimaku takes the bare key, with no "Bearer" in front.
        if (authorization != GoodJimakuKey)
        {
            return (401, """{"error":"invalid API key","code":401}""", null);
        }

        var path = request.Url.AbsolutePath["/jimaku/api/".Length..];
        var query = request.QueryString;
        if (path == "entries/search")
        {
            if (query["query"] == "ratelimit")
            {
                return (429, """{"error":"rate limited","code":429}""", "30");
            }

            var known = query["anilist_id"] == "154587"
                || query["tmdb_id"] == "tv:209867"
                || (query["query"]?.Contains("frieren", StringComparison.OrdinalIgnoreCase) ?? false);
            return (200, known ? "[" + Frieren + "]" : "[]", null);
        }

        if (path == "entries/4321/files")
        {
            return (200, query["episode"] switch
            {
                null => "[" + Episode3 + "," + Batch + "]",
                "3" => "[" + Episode3 + "]",
                _ => "[]"
            }, null);
        }

        return (404, """{"error":"entry not found","code":404}""", null);
    }

    private void StartServer()
    {
        var binary = Path.GetFullPath(JellyfinPath!);
        var web = Environment.GetEnvironmentVariable("RETIMER_E2E_JELLYFIN_WEB");
        if (string.IsNullOrEmpty(web))
        {
            web = Path.Combine(Path.GetDirectoryName(binary)!, "jellyfin-web");
        }

        var start = new ProcessStartInfo(binary)
        {
            // Jellyfin drops a few folders into its working directory.
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "--datadir", Path.Combine(_root, "data"),
            "--configdir", Path.Combine(_root, "config"),
            "--cachedir", Path.Combine(_root, "cache"),
            "--logdir", Path.Combine(_root, "log"),
            "--webdir", web
        })
        {
            start.ArgumentList.Add(argument);
        }

        // The plugin reads this once, when the server starts.
        if (UseStandInJimaku)
        {
            start.Environment["RETIMER_JIMAKU_API_URL"] = new Uri(HelperAddress, "jimaku/api").AbsoluteUri;
        }
        else
        {
            start.Environment.Remove("RETIMER_JIMAKU_API_URL");
        }

        var ffmpeg = Environment.GetEnvironmentVariable("RETIMER_E2E_FFMPEG");
        if (!string.IsNullOrEmpty(ffmpeg))
        {
            start.ArgumentList.Add("--ffmpeg");
            start.ArgumentList.Add(ffmpeg);
        }

        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    private void Remember(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_serverOutput)
        {
            _serverOutput.Append(line).Append('\n');
        }
    }

    private async Task WaitUntilUpAsync()
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < _startTimeout)
        {
            if (_process is null || _process.HasExited)
            {
                throw new InvalidOperationException("The server exited while it was starting.");
            }

            try
            {
                // While the server is still starting, a placeholder answers in its place: it serves the
                // public info, calls itself "Degraded" and refuses the rest with 503. Only the real
                // server reports "Healthy".
                using var health = await _http.GetAsync(new Uri(BaseAddress, "health"), _stopping.Token);
                if (health.StatusCode == HttpStatusCode.OK
                    && (await health.Content.ReadAsStringAsync(_stopping.Token)).Trim() == "Healthy")
                {
                    using var response = await _http.GetAsync(new Uri(BaseAddress, "System/Info/Public"), _stopping.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var info = await response.Content.ReadFromJsonAsync<JsonNode>(_stopping.Token);
                        ServerVersion = info?["Version"]?.GetValue<string>() ?? string.Empty;
                        if (ServerVersion.Length > 0)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(500, _stopping.Token);
        }

        throw new TimeoutException($"The server did not answer within {_startTimeout.TotalSeconds:F0} s.");
    }

    private async Task CompleteWizardAsync()
    {
        using (var configuration = await SendAsync(HttpMethod.Post, "Startup/Configuration", body: new JsonObject
        {
            ["UICulture"] = "en-US",
            ["MetadataCountryCode"] = "US",
            ["PreferredMetadataLanguage"] = "en"
        }))
        {
            configuration.EnsureSuccessStatusCode();
        }

        // Asking for the first user creates it; only then can it be named.
        using (var first = await SendAsync(HttpMethod.Get, "Startup/User"))
        {
            first.EnsureSuccessStatusCode();
        }

        using (var user = await SendAsync(HttpMethod.Post, "Startup/User", body: new JsonObject { ["Name"] = AdminName, ["Password"] = AdminPassword }))
        {
            user.EnsureSuccessStatusCode();
        }

        using var complete = await SendAsync(HttpMethod.Post, "Startup/Complete");
        complete.EnsureSuccessStatusCode();
    }

    private async Task<string> LoginAsync(string name, string password)
    {
        using var response = await SendAsync(HttpMethod.Post, "Users/AuthenticateByName", body: new JsonObject { ["Username"] = name, ["Pw"] = password });
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonNode>(_stopping.Token);
        return session!["AccessToken"]!.GetValue<string>();
    }

    private async Task InstallPluginAsync()
    {
        var manifestUrl = new Uri(HelperAddress, "repo/manifest.json").AbsoluteUri;

        // Only this repository, so the test does not depend on Jellyfin's public catalog.
        var repositories = new JsonArray(new JsonObject { ["Name"] = "Subtitle Retimer (e2e)", ["Url"] = manifestUrl, ["Enabled"] = true });
        using (var saved = await SendAsync(HttpMethod.Post, "Repositories", AdminToken, repositories))
        {
            saved.EnsureSuccessStatusCode();
        }

        var packages = (await GetJsonAsync("Packages", AdminToken))!.AsArray();
        var offered = packages.Where(p => Guid.Parse(p!["guid"]!.GetValue<string>()) == Guid.Parse(PluginGuid)).ToList();
        if (offered.Count != 1)
        {
            throw new InvalidOperationException($"The server's catalog offers the plugin {offered.Count} times, expected once. Catalog: {packages.ToJsonString()}");
        }

        var install = string.Create(
            CultureInfo.InvariantCulture,
            $"Packages/Installed/{Uri.EscapeDataString(PluginName)}?assemblyGuid={PluginGuid}&version={PluginVersion}&repositoryUrl={Uri.EscapeDataString(manifestUrl)}");
        using (var started = await SendAsync(HttpMethod.Post, install, AdminToken))
        {
            started.EnsureSuccessStatusCode();
        }

        // The download and unpacking run in the background.
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            var plugins = (await GetJsonAsync("Plugins", AdminToken))!.AsArray();
            if (plugins.Any(p => Guid.Parse(p!["Id"]!.GetValue<string>()) == Guid.Parse(PluginGuid)))
            {
                return;
            }

            await Task.Delay(500, _stopping.Token);
        }

        throw new TimeoutException("Jellyfin did not install the plugin within 60 s.");
    }

    private async Task ShutdownServerAsync()
    {
        using (var response = await SendAsync(HttpMethod.Post, "System/Shutdown", AdminToken))
        {
            response.EnsureSuccessStatusCode();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await _process!.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillServer();
        }

        _process?.Dispose();
        _process = null;
    }

    private void KillServer()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
