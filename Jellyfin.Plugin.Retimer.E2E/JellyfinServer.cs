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
/// It also plays the plugin repository and stand-ins for Jimaku, OpenRouter and Retimer API on a local port,
/// and it gives the server a small library: one show with three episodes made by ffmpeg.
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
    public const string GoodOpenRouterKey = "sk-or-e2e-good-key";
    public const string ShowName = "Sousou no Frieren";

    /// <summary>The time the stand-in Retimer API moves the first line to, so a saved file shows that it went through it.</summary>
    public const string RetimedStamp = "00:00:08,500";
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
    private int _retimerJobs;
    private readonly Dictionary<string, byte[]> _retimerResults = [];

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

    public string MediaDirectory => Path.Combine(_root, "media");

    public string SeasonDirectory => Path.Combine(MediaDirectory, "shows", ShowName + " (2023)", "Season 01");

    public string OpenRouterUrl => new Uri(HelperAddress, "openrouter").AbsoluteUri;

    /// <summary>Gets how long after a new episode the plugin checks a watched show, in seconds: 3 minutes on a real server.</summary>
    public static int WatchDelaySeconds => 8;

    public string ViewerId { get; private set; } = string.Empty;

    public string ShowId { get; private set; } = string.Empty;

    /// <summary>Gets the ids of the library's episodes by episode number: 3 and 4 hold two English tracks, 5 holds none.</summary>
    public Dictionary<int, string> Episodes { get; } = [];

    /// <summary>Gets what the stand-in Retimer API was sent with each job, oldest first.</summary>
    public List<RetimerSubmission> RetimerSubmissions { get; } = [];

    /// <summary>Gets the tools the stand-in OpenRouter was asked to call, oldest first.</summary>
    public List<string> ModelCalls { get; } = [];

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
    /// Gets the episodes the stand-in Jimaku lists a file for, when it is asked for all files of the show.
    /// </summary>
    protected virtual IReadOnlyList<int> ListedEpisodes => [3, 4, 5];

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

            using (var created = await SendAsync(HttpMethod.Post, "Users/New", AdminToken, new JsonObject { ["Name"] = ViewerName, ["Password"] = ViewerPassword }))
            {
                created.EnsureSuccessStatusCode();
                ViewerId = (await created.Content.ReadFromJsonAsync<JsonNode>(_stopping.Token))!["Id"]!.GetValue<string>();
            }

            ViewerToken = await LoginAsync(ViewerName, ViewerPassword);
            await CreateLibraryAsync();
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
    /// Saves the plugin's settings the way its settings screen does, and returns the answer.
    /// </summary>
    public async Task<JsonNode> SaveSettingsAsync(JsonObject settings)
    {
        using var response = await SendAsync(HttpMethod.Put, "Retimer/Settings", AdminToken, settings);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"PUT Retimer/Settings answered {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>
    /// Settings that point the plugin at this fixture's stand-ins, with good keys everywhere.
    /// </summary>
    public JsonObject StandInSettings(bool retimer = true) => new()
    {
        ["SubtitleLanguage"] = "ja",
        ["JimakuEnabled"] = true,
        ["JimakuApiKey"] = GoodJimakuKey,
        ["OpenSubtitlesEnabled"] = false,
        ["RetimerEnabled"] = retimer,
        ["RetimerUrl"] = RetimerUrl,
        ["RetimerApiKey"] = GoodRetimerKey,
        ["SendAudio"] = "never",
        ["OpenRouterApiKey"] = GoodOpenRouterKey,
        ["FetchAccess"] = "managers",
        ["MaxJobsPerDay"] = 0,
        ["MaxCostPerDay"] = 0
    };

    /// <summary>
    /// Waits until the plugin's queue is empty and returns the newest job of the item.
    /// </summary>
    public async Task<JsonNode> WaitForJobAsync(string itemId)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            var jobs = (await GetJsonAsync("Retimer/Jobs?since=0", AdminToken))!["Jobs"]!.AsArray();
            var mine = jobs.Where(job => job!["ItemId"]!.GetValue<string>() == itemId).ToList();
            if (mine.Count > 0 && mine.All(job => job!["State"]!.GetValue<string>() is not ("queued" or "working")))
            {
                return mine.OrderByDescending(job => job!["Created"]!.GetValue<string>(), StringComparer.Ordinal).First()!;
            }

            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The plugin's job did not end within 90 s.\n" + TailOfServerOutput());
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
            else if (path.StartsWith("/retimer/v1/", StringComparison.Ordinal))
            {
                (status, body, contentType) = await AnswerAsRetimerAsync(context.Request);
            }
            else if (path == "/openrouter/chat/completions")
            {
                (status, var json) = await AnswerAsOpenRouterAsync(context.Request);
                body = Encoding.UTF8.GetBytes(json);
            }
            else if (path.StartsWith("/jimaku/files/", StringComparison.Ordinal))
            {
                // A subtitle file of the stand-in Jimaku: a few Japanese lines.
                body = Encoding.UTF8.GetBytes(JapaneseSubtitle);
                contentType = "application/x-subrip";
                status = path.EndsWith(".srt", StringComparison.Ordinal) ? 200 : 404;
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
        string Listed(int episode) => $$"""{"name":"[Group] Sousou no Frieren - 0{{episode}}.ja.srt","url":"{{new Uri(HelperAddress, $"jimaku/files/x-0{episode}.srt")}}","size":34567,"last_modified":"2023-10-14T12:00:00Z"}""";
        var batch = $$"""{"name":"Frieren S01 (1-28).zip","url":"{{new Uri(HelperAddress, "jimaku/files/batch.zip")}}","size":1048576,"last_modified":"2024-03-22T15:04:05Z"}""";

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
                null => "[" + string.Join(",", ListedEpisodes.Select(Listed)) + "," + batch + "]",
                "3" => "[" + Listed(3) + "]",
                _ => "[]"
            }, null);
        }

        return (404, """{"error":"entry not found","code":404}""", null);
    }

    private const string JapaneseSubtitle = """
        1
        00:00:01,000 --> 00:00:04,000
        おはようございます、フリーレン様。

        2
        00:00:06,000 --> 00:00:09,500
        今日はいい天気ですね。

        3
        00:00:12,000 --> 00:00:15,000
        魔法を集めるのが私の趣味なんだ。

        4
        00:00:20,000 --> 00:00:24,000
        そろそろ出発しましょうか。

        5
        00:00:30,000 --> 00:00:33,000
        ヒンメルならそうしたってことだよ。

        6
        00:00:40,000 --> 00:00:44,000
        人間の寿命は短いってわかっていたのに。

        """;

    private const string EnglishTrack = """
        1
        00:00:08,500 --> 00:00:11,500
        Good morning, Mistress Frieren.

        2
        00:00:13,500 --> 00:00:17,000
        Lovely weather today.

        3
        00:00:19,500 --> 00:00:22,500
        Collecting magic is my hobby.

        4
        00:00:27,500 --> 00:00:31,500
        Shall we set out?

        """;

    private const string SignsTrack = """
        1
        00:00:02,000 --> 00:00:04,000
        FRIEREN

        """;

    /// <summary>
    /// The stand-in Retimer API: features, a job that is finished at once, its result and its removal,
    /// in the shapes of the Retimer API's README.
    /// </summary>
    private async Task<(int Status, byte[] Body, string ContentType)> AnswerAsRetimerAsync(HttpListenerRequest request)
    {
        static (int, byte[], string) Json(int status, string json) => (status, Encoding.UTF8.GetBytes(json), "application/json");

        var authorization = request.Headers["Authorization"];
        lock (RetimerKeysSeen)
        {
            RetimerKeysSeen.Add(authorization);
        }

        if (authorization != "Bearer " + GoodRetimerKey)
        {
            return Json(401, """{"detail":"A valid API key is required: Authorization: Bearer <key>."}""");
        }

        var path = request.Url!.AbsolutePath["/retimer/v1/".Length..];
        if (path == "features")
        {
            return Json(200, """{"features":["matched_reference","ocr","reference_tracks","remove_signs_and_credits"],"limits":{"reference_subtitles":6}}""");
        }

        if (path == "jobs" && request.HttpMethod == "GET")
        {
            return Json(200, """{"jobs":[]}""");
        }

        if (path == "jobs" && request.HttpMethod == "POST")
        {
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, _stopping.Token);
            var parts = ReadMultipart(request.ContentType ?? string.Empty, buffer.ToArray());
            var target = parts.First(part => part.Name == "target");
            var id = "e2e-job-" + Interlocked.Increment(ref _retimerJobs).ToString(CultureInfo.InvariantCulture);
            var references = parts.Where(part => part.Name == "reference_subtitle").ToList();
            lock (RetimerSubmissions)
            {
                RetimerSubmissions.Add(new RetimerSubmission(
                    target.FileName,
                    references.Select(part => Encoding.UTF8.GetString(part.Content)).ToList(),
                    parts.Any(part => part.Name == "reference_audio"),
                    JsonNode.Parse(Encoding.UTF8.GetString(parts.First(part => part.Name == "options").Content))!));
                _retimerResults[id] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(target.Content).Replace("00:00:01,000", RetimedStamp, StringComparison.Ordinal));
            }

            return Json(202, $$"""{"id":"{{id}}","status":"queued"}""");
        }

        if (path.StartsWith("jobs/", StringComparison.Ordinal))
        {
            var rest = path["jobs/".Length..];
            if (request.HttpMethod == "DELETE")
            {
                return (204, [], "application/json");
            }

            if (rest.EndsWith("/result", StringComparison.Ordinal))
            {
                lock (RetimerSubmissions)
                {
                    return _retimerResults.TryGetValue(rest[..^"/result".Length], out var result)
                        ? (200, result, "application/x-subrip")
                        : Json(404, """{"detail":"No such job."}""");
                }
            }

            // The first track is the dialogue track, the second holds one sign.
            return Json(200, """
                {"id":"JOB","status":"finished","stage":"done","outcome":"synced",
                 "summary":{"cost_usd":0.002,"confidence":0.93,"warnings":[],"fit":"constant",
                  "blocks":[{"from_ms":null,"to_ms":null,"shift_ms":7500,"lines":3,"confidence":0.93}],
                  "reference":{"chosen":0,"reason":"The first track fits.","candidates":[
                   {"index":0,"language":"eng","title":"Full","verdict":"chosen","reason":null},
                   {"index":1,"language":"eng","title":"Signs","verdict":"too_little_dialogue","reason":null}]}}}
                """.Replace("JOB", rest, StringComparison.Ordinal));
        }

        return Json(404, """{"detail":"Not found."}""");
    }

    /// <summary>
    /// The stand-in OpenRouter. It answers the three questions the plugin asks a model with a forced tool call,
    /// in the shape of OpenRouter's chat API.
    /// </summary>
    private async Task<(int Status, string Json)> AnswerAsOpenRouterAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = JsonNode.Parse(await reader.ReadToEndAsync(_stopping.Token))!;
        if (request.Headers["Authorization"] != "Bearer " + GoodOpenRouterKey)
        {
            return (401, """{"error":{"code":401,"message":"No auth credentials found"}}""");
        }

        var offered = (body["tools"] as JsonArray ?? []).Select(tool => tool!["function"]!["name"]!.GetValue<string>()).ToList();
        var name = body["tool_choice"] is JsonObject forced
            ? forced["function"]!["name"]!.GetValue<string>()
            : offered.Contains("choose_entry") ? "choose_entry" : offered.FirstOrDefault() ?? "none";
        lock (ModelCalls)
        {
            ModelCalls.Add(name);
        }

        JsonObject arguments;
        if (name == "choose_entry")
        {
            arguments = new JsonObject { ["entry_id"] = "4321", ["reason"] = "The title fits." };
        }
        else if (name == "language_verdict")
        {
            arguments = new JsonObject { ["language"] = "ja", ["second_language"] = null, ["reason"] = "Kana and kanji." };
        }
        else if (name == "map_files")
        {
            // Every listed file whose name holds " - NN" goes to episode NN.
            var user = body["messages"]![1]!["content"]!.GetValue<string>();
            var episodes = new JsonArray();
            foreach (var line in user[user.IndexOf("Files:\n", StringComparison.Ordinal)..].Split('\n').Skip(1))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+) .* - (\d{2})\b");
                if (match.Success)
                {
                    episodes.Add(new JsonObject
                    {
                        ["episode"] = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                        ["files"] = new JsonArray(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)),
                        ["reason"] = "The name carries the episode."
                    });
                }
            }

            arguments = new JsonObject { ["episodes"] = episodes };
        }
        else
        {
            arguments = new JsonObject { ["ok"] = true };
        }

        var answer = new JsonObject
        {
            ["id"] = "gen-e2e",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "call_1",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() }
                    })
                }
            }),
            ["usage"] = new JsonObject { ["cost"] = 0.0004 }
        };
        return (200, answer.ToJsonString());
    }

    /// <summary>
    /// Splits a multipart/form-data body into its parts. The bytes of each part are kept as they came.
    /// </summary>
    private static List<(string Name, string FileName, byte[] Content)> ReadMultipart(string contentType, byte[] body)
    {
        var boundary = contentType[(contentType.IndexOf("boundary=", StringComparison.Ordinal) + "boundary=".Length)..].Trim('"');
        var text = Encoding.Latin1.GetString(body);
        var parts = new List<(string, string, byte[])>();
        foreach (var piece in text.Split("--" + boundary, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = piece.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            var headers = piece[..split];
            var content = piece[(split + 4)..];
            if (content.EndsWith("\r\n", StringComparison.Ordinal))
            {
                content = content[..^2];
            }

            static string Value(string headers, string key)
            {
                var match = System.Text.RegularExpressions.Regex.Match(headers, key + "=\"?([^\";\r\n]*)\"?");
                return match.Success ? match.Groups[1].Value : string.Empty;
            }

            parts.Add((Value(headers, " name"), Value(headers, "filename"), Encoding.Latin1.GetBytes(content)));
        }

        return parts;
    }

    /// <summary>
    /// Makes three small episodes with ffmpeg and gives them to the server as a library that asks no
    /// metadata service: episodes 3 and 4 hold two English subtitle tracks, episode 5 holds none.
    /// </summary>
    /// <summary>
    /// Makes a one-minute video for an episode of the show in its season folder: with an English full track
    /// and an English signs track inside, or with no subtitle at all.
    /// </summary>
    public async Task MakeEpisodeAsync(int episode, bool withTracks)
    {
        var full = Path.Combine(_root, "full.srt");
        var signs = Path.Combine(_root, "signs.srt");
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=160x90:r=2",
            "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono"
        };
        if (withTracks)
        {
            arguments.AddRange(["-i", full, "-i", signs]);
        }

        arguments.AddRange(["-t", "60", "-map", "0:v", "-map", "1:a"]);
        if (withTracks)
        {
            arguments.AddRange(
            [
                "-map", "2:s", "-map", "3:s", "-c:s", "srt",
                "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "title=Full", "-disposition:s:0", "default",
                "-metadata:s:s:1", "language=eng", "-metadata:s:s:1", "title=Signs", "-disposition:s:1", "0"
            ]);
        }

        arguments.AddRange(["-c:v", "mpeg4", "-c:a", "aac", "-metadata:s:a:0", "language=jpn"]);
        arguments.Add(Path.Combine(SeasonDirectory, string.Create(CultureInfo.InvariantCulture, $"{ShowName} - S01E{episode:00}.mkv")));

        var ffmpeg = Environment.GetEnvironmentVariable("RETIMER_E2E_FFMPEG");
        var start = new ProcessStartInfo(string.IsNullOrEmpty(ffmpeg) ? "ffmpeg" : ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync(_stopping.Token);
        await process.WaitForExitAsync(_stopping.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not make a test video: " + errors);
        }
    }

    private async Task CreateLibraryAsync()
    {
        Directory.CreateDirectory(SeasonDirectory);
        var full = Path.Combine(_root, "full.srt");
        var signs = Path.Combine(_root, "signs.srt");
        await File.WriteAllTextAsync(full, EnglishTrack, _stopping.Token);
        await File.WriteAllTextAsync(signs, SignsTrack, _stopping.Token);
        foreach (var episode in new[] { 3, 4, 5 })
        {
            await MakeEpisodeAsync(episode, withTracks: episode != 5);
        }

        static JsonObject NoFetchers(string type) => new() { ["Type"] = type, ["MetadataFetchers"] = new JsonArray(), ["ImageFetchers"] = new JsonArray() };
        var options = new JsonObject
        {
            ["LibraryOptions"] = new JsonObject
            {
                ["PathInfos"] = new JsonArray(new JsonObject { ["Path"] = Path.Combine(MediaDirectory, "shows") }),
                ["EnableRealtimeMonitor"] = false,
                ["TypeOptions"] = new JsonArray(NoFetchers("Series"), NoFetchers("Season"), NoFetchers("Episode"))
            }
        };
        using (var created = await SendAsync(HttpMethod.Post, "Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true", AdminToken, options))
        {
            created.EnsureSuccessStatusCode();
        }

        // The scan runs in the background. It is done when all three episodes are there with their tracks read.
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(120))
        {
            var items = (await GetJsonAsync("Items?Recursive=true&IncludeItemTypes=Episode&Fields=MediaStreams", AdminToken))!["Items"]!.AsArray();
            if (items.Count == 3 && items.All(item => item!["MediaStreams"] is JsonArray { Count: >= 2 } && item["IndexNumber"] is not null && item["SeriesId"] is not null))
            {
                foreach (var item in items)
                {
                    Episodes[item!["IndexNumber"]!.GetValue<int>()] = Guid.Parse(item["Id"]!.GetValue<string>()).ToString("N");
                    ShowId = Guid.Parse(item["SeriesId"]!.GetValue<string>()).ToString("N");
                }

                return;
            }

            await Task.Delay(1000, _stopping.Token);
        }

        throw new TimeoutException("Jellyfin did not finish scanning the test library within 120 s.");
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

        // The plugin reads these once, when the server starts.
        start.Environment["RETIMER_OPENROUTER_API_URL"] = OpenRouterUrl;
        start.Environment["RETIMER_WATCH_DELAY_SECONDS"] = WatchDelaySeconds.ToString(CultureInfo.InvariantCulture);
        start.Environment.Remove("OPENROUTER_API_KEY");
        start.Environment.Remove("ANTHROPIC_RETIMER_API_KEY");
        if (UseStandInJimaku)
        {
            start.Environment["RETIMER_JIMAKU_API_URL"] = new Uri(HelperAddress, "jimaku/api").AbsoluteUri;
        }
        else
        {
            start.Environment.Remove("RETIMER_JIMAKU_API_URL");
        }

        // The server gets its Jimaku key through the settings, like on a real installation.
        start.Environment.Remove("JIMAKU_API_KEY");

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

/// <summary>
/// What one job brought to the stand-in Retimer API.
/// </summary>
public sealed record RetimerSubmission(string TargetName, List<string> References, bool HasAudio, JsonNode Options);
