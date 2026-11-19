using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Jimaku;

/// <summary>
/// Calls the Jimaku API (https://jimaku.cc/api/docs) with the key from the plugin's settings.
/// </summary>
public sealed class JimakuClient : IJimakuClient
{
    /// <summary>
    /// Where the Jimaku API lives.
    /// </summary>
    public static readonly Uri DefaultBaseAddress = new("https://jimaku.cc/api/");

    /// <summary>
    /// For tests only: when this environment variable holds an http or https address, the client
    /// calls it in place of <see cref="DefaultBaseAddress"/>. It is read once, when the server starts.
    /// </summary>
    public const string BaseAddressVariable = "RETIMER_JIMAKU_API_URL";

    /// <summary>
    /// The longest rate-limit wait the client sits out itself before it gives the call back as failed.
    /// </summary>
    internal static readonly TimeSpan MaxAutomaticWait = TimeSpan.FromSeconds(5);

    // Jimaku writes its fields in snake_case: english_name, anilist_id, last_modified.
    private static readonly JsonSerializerOptions _wireFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static readonly ProductInfoHeaderValue _userAgent = new(
        "jellyfin-plugin-retimer",
        typeof(JimakuClient).Assembly.GetName().Version?.ToString() ?? "0");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginSettings _settings;
    private readonly ILogger<JimakuClient> _logger;
    private readonly Uri _baseAddress;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Initializes a new instance of the <see cref="JimakuClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="settings">The plugin's settings.</param>
    /// <param name="logger">The logger.</param>
    public JimakuClient(IHttpClientFactory httpClientFactory, IPluginSettings settings, ILogger<JimakuClient> logger)
        : this(httpClientFactory, settings, logger, ResolveBaseAddress(Environment.GetEnvironmentVariable(BaseAddressVariable)), Task.Delay)
    {
        if (_baseAddress != DefaultBaseAddress)
        {
            logger.LogWarning("Jimaku calls go to {Address} instead of jimaku.cc, because {Variable} is set", _baseAddress, BaseAddressVariable);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JimakuClient"/> class for tests.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="settings">The plugin's settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="baseAddress">The API address, ending in a slash.</param>
    /// <param name="delay">How to wait out a short rate limit.</param>
    internal JimakuClient(
        IHttpClientFactory httpClientFactory,
        IPluginSettings settings,
        ILogger<JimakuClient> logger,
        Uri baseAddress,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
        _baseAddress = baseAddress;
        _delay = delay;
    }

    /// <summary>
    /// Picks the API address: the override when it is a usable http or https address, otherwise jimaku.cc.
    /// </summary>
    /// <param name="overrideAddress">The value of <see cref="BaseAddressVariable"/>, if any.</param>
    /// <returns>The address, ending in a slash.</returns>
    internal static Uri ResolveBaseAddress(string? overrideAddress) => ResolveBaseAddress(overrideAddress, DefaultBaseAddress);

    /// <summary>
    /// Picks an API address: the override when it is a usable http or https address, otherwise the fallback.
    /// </summary>
    /// <param name="overrideAddress">The value of the environment variable, if any.</param>
    /// <param name="fallback">The service's own address.</param>
    /// <returns>The address, ending in a slash.</returns>
    internal static Uri ResolveBaseAddress(string? overrideAddress, Uri fallback)
    {
        var trimmed = overrideAddress?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && Uri.TryCreate(trimmed.TrimEnd('/') + "/", UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            return parsed;
        }

        return fallback;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JimakuEntry>> SearchEntriesAsync(JimakuEntryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var title = query.Title?.Trim();
        if (query.AnilistId is null && query.TmdbId is null && string.IsNullOrEmpty(title))
        {
            throw new ArgumentException("A search needs an AniList id, a TMDB id or a title.", nameof(query));
        }

        if (query.AnilistId is < 0)
        {
            throw new ArgumentException("An AniList id cannot be negative.", nameof(query));
        }

        if (query.TmdbId is not null && !JimakuTmdbId.IsValid(query.TmdbId))
        {
            throw new ArgumentException("A TMDB id must look like tv:123 or movie:123.", nameof(query));
        }

        var parameters = new List<KeyValuePair<string, string>>();
        if (query.AnilistId is { } anilistId)
        {
            parameters.Add(new("anilist_id", anilistId.ToString(CultureInfo.InvariantCulture)));
        }

        if (query.TmdbId is not null)
        {
            parameters.Add(new("tmdb_id", query.TmdbId));
        }

        if (!string.IsNullOrEmpty(title))
        {
            parameters.Add(new("query", title));
        }

        if (query.Anime is { } anime)
        {
            parameters.Add(new("anime", anime ? "true" : "false"));
        }

        return GetListAsync<JimakuEntry>("entries/search" + ToQueryString(parameters), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JimakuEntry>> FindEntriesAsync(JimakuEntryQuery lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        // Ids settle most cases, so they come first. The title is the fallback because Jimaku matches it loosely.
        var steps = new List<JimakuEntryQuery>();
        if (lookup.AnilistId is not null)
        {
            steps.Add(new JimakuEntryQuery { AnilistId = lookup.AnilistId, Anime = lookup.Anime });
        }

        if (lookup.TmdbId is not null)
        {
            steps.Add(new JimakuEntryQuery { TmdbId = lookup.TmdbId, Anime = lookup.Anime });
        }

        if (!string.IsNullOrWhiteSpace(lookup.Title))
        {
            steps.Add(new JimakuEntryQuery { Title = lookup.Title, Anime = lookup.Anime });
        }

        if (steps.Count == 0)
        {
            throw new ArgumentException("A lookup needs an AniList id, a TMDB id or a title.", nameof(lookup));
        }

        foreach (var step in steps)
        {
            var entries = await SearchEntriesAsync(step, cancellationToken).ConfigureAwait(false);
            if (entries.Count > 0)
            {
                return entries;
            }
        }

        return [];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JimakuFile>> GetFilesAsync(long entryId, int? episode, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryId);
        if (episode is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episode), "An episode number cannot be negative.");
        }

        var parameters = new List<KeyValuePair<string, string>>();
        if (episode is { } number)
        {
            parameters.Add(new("episode", number.ToString(CultureInfo.InvariantCulture)));
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"entries/{entryId}/files");
        return GetListAsync<JimakuFile>(path + ToQueryString(parameters), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        // The address comes from Jimaku's own list, and still it is only followed when it stays at Jimaku.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || address.Scheme != _baseAddress.Scheme
            || !string.Equals(address.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase)
            || address.Port != _baseAddress.Port)
        {
            throw new JimakuException(JimakuError.BadRequest, "The file's address does not point at Jimaku.");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.Add(_userAgent);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new JimakuException(JimakuError.NotFound, "Jimaku no longer has this file.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new JimakuException(JimakuError.RateLimited, "Jimaku's rate limit was hit.", ReadRetryAfter(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new JimakuException(
                    JimakuError.Unavailable,
                    string.Create(CultureInfo.InvariantCulture, $"Jimaku answered with status {(int)response.StatusCode}."));
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new JimakuException(JimakuError.BadRequest, "The file is larger than the plugin accepts.");
            }

            using var buffer = new System.IO.MemoryStream();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        throw new JimakuException(JimakuError.BadRequest, "The file is larger than the plugin accepts.");
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            return buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new JimakuException(JimakuError.Unavailable, "Jimaku could not be reached.", innerException: ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JimakuException(JimakuError.Unavailable, "Jimaku did not answer in time.", innerException: ex);
        }
    }

    private static string ToQueryString(List<KeyValuePair<string, string>> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var (name, value) in parameters)
        {
            builder.Append(builder.Length == 0 ? '?' : '&')
                .Append(name)
                .Append('=')
                .Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        // Jimaku's own header first: seconds, with a fraction.
        if (response.Headers.TryGetValues("x-ratelimit-reset-after", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            && seconds < TimeSpan.MaxValue.TotalSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return response.Headers.RetryAfter?.Delta;
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(_wireFormat, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(error?.Error) ? null : error.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var key = _settings.Reveal(_settings.Current.JimakuApiKey).Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new JimakuException(JimakuError.NotConfigured, "No Jimaku API key is set in the plugin's settings.");
        }

        var url = new Uri(_baseAddress, relativeUrl);
        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Jimaku takes the bare key, without a scheme such as "Bearer".
            request.Headers.TryAddWithoutValidation("Authorization", key);
            request.Headers.UserAgent.Add(_userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new JimakuException(JimakuError.Unavailable, "Jimaku could not be reached.", innerException: ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new JimakuException(JimakuError.Unavailable, "Jimaku did not answer in time.", innerException: ex);
            }

            using (response)
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        try
                        {
                            var items = await response.Content.ReadFromJsonAsync<List<T>>(_wireFormat, cancellationToken).ConfigureAwait(false);
                            return items ?? [];
                        }
                        catch (Exception ex) when (ex is JsonException or NotSupportedException)
                        {
                            throw new JimakuException(JimakuError.Unavailable, "Jimaku sent an answer that could not be read.", innerException: ex);
                        }

                    case HttpStatusCode.Unauthorized:
                        throw new JimakuException(JimakuError.Unauthorized, "Jimaku rejected the API key.");

                    case HttpStatusCode.NotFound:
                        throw new JimakuException(JimakuError.NotFound, "Jimaku has no such entry.");

                    case HttpStatusCode.BadRequest:
                        var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                        throw new JimakuException(JimakuError.BadRequest, "Jimaku rejected the request" + (detail is null ? "." : ": " + detail));

                    case HttpStatusCode.TooManyRequests:
                        var retryAfter = ReadRetryAfter(response);
                        if (attempt == 0 && retryAfter is { } wait && wait <= MaxAutomaticWait)
                        {
                            _logger.LogDebug("Jimaku's rate limit was hit; waiting {Seconds:F1} s and trying once more", wait.TotalSeconds);
                            await _delay(wait, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        throw new JimakuException(JimakuError.RateLimited, "Jimaku's rate limit was hit.", retryAfter);

                    default:
                        throw new JimakuException(
                            JimakuError.Unavailable,
                            string.Create(CultureInfo.InvariantCulture, $"Jimaku answered with status {(int)response.StatusCode}."));
                }
            }
        }
    }

    private sealed class ApiError
    {
        public string? Error { get; set; }
    }
}
