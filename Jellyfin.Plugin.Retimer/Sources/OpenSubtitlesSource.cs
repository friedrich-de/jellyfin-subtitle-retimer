using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Sources;

/// <summary>
/// OpenSubtitles (opensubtitles.com) as a subtitle source. It is asked for one episode or film at a
/// time, by ids where the library has them. It hands out files only to a login, a limited number a day.
/// </summary>
internal sealed class OpenSubtitlesSource : ISubtitleSource, IDisposable
{
    public const string SourceId = "opensubtitles";

    /// <summary>
    /// For tests only: when this environment variable holds an http or https address, the source
    /// calls it in place of api.opensubtitles.com. It is read once, when the server starts.
    /// </summary>
    public const string BaseAddressVariable = "RETIMER_OPENSUBTITLES_API_URL";

    private const long MaxDownloadBytes = 16L * 1024 * 1024;

    private static readonly Uri _defaultBaseAddress = new("https://api.opensubtitles.com/api/v1/");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginSettings _settings;
    private readonly ILogger<OpenSubtitlesSource> _logger;
    private readonly Uri _configuredBaseAddress;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly string _userAgent = "jellyfin-plugin-retimer v" + (typeof(OpenSubtitlesSource).Assembly.GetName().Version?.ToString(3) ?? "0");

    private string? _token;
    private string? _tokenFor;
    private DateTimeOffset _tokenUntil;
    private Uri? _loginBaseAddress;

    public OpenSubtitlesSource(IHttpClientFactory httpClientFactory, IPluginSettings settings, ILogger<OpenSubtitlesSource> logger)
        : this(httpClientFactory, settings, logger, Jimaku.JimakuClient.ResolveBaseAddress(Environment.GetEnvironmentVariable(BaseAddressVariable), _defaultBaseAddress))
    {
    }

    internal OpenSubtitlesSource(IHttpClientFactory httpClientFactory, IPluginSettings settings, ILogger<OpenSubtitlesSource> logger, Uri baseAddress)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
        _configuredBaseAddress = baseAddress;
    }

    public string Id => SourceId;

    public string Name => "OpenSubtitles";

    public bool IsReady
    {
        get
        {
            var configuration = _settings.Current;
            return configuration.OpenSubtitlesEnabled
                && !string.IsNullOrWhiteSpace(configuration.OpenSubtitlesApiKey)
                && !string.IsNullOrWhiteSpace(configuration.OpenSubtitlesUsername)
                && !string.IsNullOrWhiteSpace(configuration.OpenSubtitlesPassword);
        }
    }

    public bool HasEntries => false;

    /// <summary>Gets how many downloads OpenSubtitles said are left today, once it has said so.</summary>
    public int? RemainingDownloads { get; private set; }

    public bool Covers(string language) => true;

    public void Dispose() => _loginLock.Dispose();

    public Task<IReadOnlyList<SourceEntry>> SearchEntriesAsync(VideoQuery video, string? title, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceEntry>>([]);

    public async Task<IReadOnlyList<SourceFile>> ListFilesAsync(VideoQuery video, string? entryId, CancellationToken cancellationToken)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = LanguageParameter(video.Language),
            ["ai_translated"] = "exclude"
        };

        var byId = true;
        if (video.IsMovie)
        {
            parameters["type"] = "movie";
            if (NumericImdbId(video.ImdbId) is { } imdb)
            {
                parameters["imdb_id"] = imdb;
            }
            else if (video.TmdbId is { } tmdb)
            {
                parameters["tmdb_id"] = tmdb.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                byId = false;
                parameters["query"] = video.Title.ToLowerInvariant();
                if (video.Year is { } year)
                {
                    parameters["year"] = year.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        else
        {
            if (video.Season is null || video.Episode is null)
            {
                return [];
            }

            parameters["type"] = "episode";
            parameters["season_number"] = video.Season.Value.ToString(CultureInfo.InvariantCulture);
            parameters["episode_number"] = video.Episode.Value.ToString(CultureInfo.InvariantCulture);
            if (NumericImdbId(video.EpisodeImdbId) is { } episodeImdb)
            {
                parameters["imdb_id"] = episodeImdb;
            }
            else if (NumericImdbId(video.ImdbId) is { } parentImdb)
            {
                parameters["parent_imdb_id"] = parentImdb;
            }
            else if (video.TmdbId is { } parentTmdb)
            {
                parameters["parent_tmdb_id"] = parentTmdb.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                byId = false;
                parameters["query"] = video.Title.ToLowerInvariant();
            }
        }

        // OpenSubtitles redirects a search whose parameters are not sorted and in lower case.
        var query = string.Join('&', parameters.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        var answer = await SendAsync(HttpMethod.Get, "subtitles?" + query, null, withLogin: false, cancellationToken).ConfigureAwait(false);

        var files = new List<SourceFile>();
        foreach (var item in answer?["data"] as JsonArray ?? [])
        {
            var attributes = item?["attributes"];
            var file = (attributes?["files"] as JsonArray)?.FirstOrDefault();
            var fileId = Read<long?>(file?["file_id"]);
            if (attributes is null || fileId is null)
            {
                continue;
            }

            var release = Read<string>(attributes["release"]);
            var fileName = Read<string>(file?["file_name"]);
            var name = !string.IsNullOrWhiteSpace(fileName) ? fileName : (!string.IsNullOrWhiteSpace(release) ? release : "subtitle " + fileId);
            if (!Picking.SubtitleText.IsTextSubtitle(name))
            {
                name += ".srt";
            }

            var details = attributes["feature_details"];
            var sameEpisode = video.IsMovie
                || (Read<int?>(details?["season_number"]) == video.Season && Read<int?>(details?["episode_number"]) == video.Episode);

            files.Add(new SourceFile
            {
                Source = SourceId,
                Id = fileId.Value.ToString(CultureInfo.InvariantCulture),
                Name = SafeName(name),
                Release = release,
                Downloads = Read<int?>(attributes["download_count"]),
                HearingImpaired = Read<bool?>(attributes["hearing_impaired"]) ?? false,
                MachineTranslated = (Read<bool?>(attributes["machine_translated"]) ?? false) || (Read<bool?>(attributes["ai_translated"]) ?? false),
                Trusted = Read<bool?>(attributes["from_trusted"]) ?? false,
                Modified = DateTimeOffset.TryParse(Read<string>(attributes["upload_date"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var uploaded) ? uploaded : null,

                // A search by title can bring another show with the same name, so only an id search counts.
                ForThisVideo = byId && sameEpisode
            });
        }

        return files;
    }

    public async Task<DownloadedFile> DownloadAsync(SourceFile file, CancellationToken cancellationToken)
    {
        if (!long.TryParse(file.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId))
        {
            throw new SourceException(SourceError.NotFound, "OpenSubtitles has no such file.");
        }

        var body = new JsonObject { ["file_id"] = fileId, ["sub_format"] = "srt" };
        var answer = await SendAsync(HttpMethod.Post, "download", body, withLogin: true, cancellationToken).ConfigureAwait(false);
        RemainingDownloads = Read<int?>(answer?["remaining"]) ?? RemainingDownloads;

        var link = Read<string>(answer?["link"]);
        if (!Uri.TryCreate(link, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != _configuredBaseAddress.Scheme))
        {
            throw new SourceException(SourceError.Unavailable, "OpenSubtitles gave no usable address for the file.");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException(SourceError.Unavailable, string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles answered the download with status {(int)response.StatusCode}."));
            }

            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new SourceException(SourceError.TooLarge, "The file is larger than the plugin accepts.");
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (content.Length == 0 || content.Length > MaxDownloadBytes)
            {
                throw new SourceException(SourceError.Unavailable, "OpenSubtitles sent an empty or oversized file.");
            }

            // The file was asked for as SRT, whatever its name at the source says.
            var name = Path.ChangeExtension(file.Name, ".srt");
            return new DownloadedFile(name, content);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(SourceError.Unavailable, "The file could not be fetched from OpenSubtitles.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceException(SourceError.Unavailable, "OpenSubtitles did not answer in time.", ex);
        }
    }

    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        if (string.IsNullOrWhiteSpace(configuration.OpenSubtitlesApiKey))
        {
            throw new SourceException(SourceError.NotConfigured, "Enter the OpenSubtitles API key first.");
        }

        if (string.IsNullOrWhiteSpace(configuration.OpenSubtitlesUsername) || string.IsNullOrWhiteSpace(configuration.OpenSubtitlesPassword))
        {
            throw new SourceException(SourceError.NotConfigured, "Enter the user name and the password too. OpenSubtitles hands out files only to a login.");
        }

        // A fresh login, so a changed password is noticed.
        _token = null;
        var user = await SendAsync(HttpMethod.Get, "infos/user", null, withLogin: true, cancellationToken).ConfigureAwait(false);
        var remaining = Read<int?>(user?["data"]?["remaining_downloads"]);
        RemainingDownloads = remaining ?? RemainingDownloads;
        return remaining is null
            ? "OpenSubtitles accepted the key and the login."
            : string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles accepted the key and the login. {remaining} downloads are left today.");
    }

    internal static string LanguageParameter(string language) => language switch
    {
        // OpenSubtitles splits these by region, and the values have to be sorted.
        "zh" => "zh-cn,zh-tw",
        "pt" => "pt-br,pt-pt",
        _ => language.ToLowerInvariant()
    };

    private static string? NumericImdbId(string? imdbId)
    {
        var digits = imdbId?.Trim().TrimStart('t', 'T').TrimStart('0');
        return !string.IsNullOrEmpty(digits) && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private static string SafeName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        return cleaned.Length > 180 ? cleaned[..180] : cleaned;
    }

    private static T? Read<T>(JsonNode? node)
    {
        if (node is null)
        {
            return default;
        }

        try
        {
            return node.GetValue<T>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // A number sent as text, or the other way round.
            var text = node.ToString();
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            try
            {
                return (T?)Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
            }
            catch (Exception inner) when (inner is InvalidCastException or FormatException or OverflowException)
            {
                return default;
            }
        }
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var identity = configuration.OpenSubtitlesApiKey + "\n" + configuration.OpenSubtitlesUsername + "\n" + configuration.OpenSubtitlesPassword;

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _tokenFor == identity && DateTimeOffset.UtcNow < _tokenUntil)
            {
                return _token;
            }

            if (string.IsNullOrWhiteSpace(configuration.OpenSubtitlesUsername) || string.IsNullOrWhiteSpace(configuration.OpenSubtitlesPassword))
            {
                throw new SourceException(SourceError.NotConfigured, "OpenSubtitles hands out files only to a login. Add the user name and the password in the settings.");
            }

            var body = new JsonObject { ["username"] = configuration.OpenSubtitlesUsername.Trim(), ["password"] = configuration.OpenSubtitlesPassword };
            _token = null;
            _loginBaseAddress = null;
            var answer = await SendOnceAsync(HttpMethod.Post, "login", body, token: null, cancellationToken).ConfigureAwait(false);
            var token = Read<string>(answer?["token"]);
            if (string.IsNullOrEmpty(token))
            {
                throw new SourceException(SourceError.Unauthorized, "OpenSubtitles did not accept the login.");
            }

            // The answer names the host this account has to use from now on, such as the one for VIP accounts.
            var host = Read<string>(answer?["base_url"]);
            if (_configuredBaseAddress == _defaultBaseAddress
                && !string.IsNullOrWhiteSpace(host)
                && host.EndsWith("opensubtitles.com", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate("https://" + host.Trim() + "/api/v1/", UriKind.Absolute, out var loginBase))
            {
                _loginBaseAddress = loginBase;
            }

            _token = token;
            _tokenFor = identity;
            _tokenUntil = DateTimeOffset.UtcNow.AddHours(12);
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, bool withLogin, CancellationToken cancellationToken)
    {
        if (!withLogin)
        {
            return await SendOnceAsync(method, path, body, token: null, cancellationToken).ConfigureAwait(false);
        }

        var token = await LoginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendOnceAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceException ex) when (ex.Error == SourceError.Unauthorized)
        {
            // The login has run out: sign in once more and try again.
            _token = null;
            token = await LoginAsync(cancellationToken).ConfigureAwait(false);
            return await SendOnceAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> SendOnceAsync(HttpMethod method, string path, JsonNode? body, string? token, CancellationToken cancellationToken)
    {
        var key = _settings.Current.OpenSubtitlesApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SourceException(SourceError.NotConfigured, "No OpenSubtitles API key is set in the plugin's settings.");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(method, new Uri(_loginBaseAddress ?? _configuredBaseAddress, path));
        request.Headers.TryAddWithoutValidation("Api-Key", key);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(SourceError.Unavailable, "OpenSubtitles could not be reached.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceException(SourceError.Unavailable, "OpenSubtitles did not answer in time.", ex);
        }

        using (response)
        {
            JsonNode? json = null;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
                // An error page instead of JSON; the status below says what happened.
            }

            if (response.IsSuccessStatusCode)
            {
                return json ?? throw new SourceException(SourceError.Unavailable, "OpenSubtitles sent an answer that could not be read.");
            }

            var detail = Read<string>(json?["message"]);
            switch ((int)response.StatusCode)
            {
                case 401:
                    throw new SourceException(
                        SourceError.Unauthorized,
                        path == "login" ? "OpenSubtitles rejected the user name or the password." : "OpenSubtitles rejected the login.");
                case 403:
                    throw new SourceException(SourceError.Unauthorized, "OpenSubtitles rejected the API key.");
                case 406:
                    RemainingDownloads = 0;
                    var reset = Read<string>(json?["reset_time"]);
                    throw new SourceException(
                        SourceError.DownloadLimit,
                        "OpenSubtitles' download limit for today is reached." + (string.IsNullOrWhiteSpace(reset) ? string.Empty : " It resets in " + reset + "."));
                case 429:
                    throw new SourceException(SourceError.RateLimited, "OpenSubtitles' rate limit was hit.")
                    {
                        RetryAfter = response.Headers.RetryAfter?.Delta
                    };
                case 400 when path == "login":
                    throw new SourceException(SourceError.Unauthorized, "OpenSubtitles rejected the login" + (detail is null ? "." : ": " + detail));
                default:
                    _logger.LogInformation("OpenSubtitles answered {Path} with status {Status}", path.Split('?')[0], (int)response.StatusCode);
                    throw new SourceException(
                        SourceError.Unavailable,
                        string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles answered with status {(int)response.StatusCode}") + (detail is null ? "." : ": " + detail));
            }
        }
    }
}
