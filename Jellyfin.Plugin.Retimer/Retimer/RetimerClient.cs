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

namespace Jellyfin.Plugin.Retimer.Retimer;

/// <summary>
/// Calls the retimer's HTTP API with the address and key from the plugin's settings.
/// </summary>
public sealed class RetimerClient : IRetimerClient, IRetimerApi
{
    private static readonly TimeSpan _featuresLifetime = TimeSpan.FromMinutes(5);

    private readonly Lock _featuresLock = new();
    private (string Key, DateTimeOffset At, RetimerFeatures Features)? _features;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginSettings _settings;
    private readonly ILogger<RetimerClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetimerClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="settings">The plugin's settings.</param>
    /// <param name="logger">The logger.</param>
    public RetimerClient(IHttpClientFactory httpClientFactory, IPluginSettings settings, ILogger<RetimerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Turns the address from the settings into a base address that ends in a slash.
    /// </summary>
    /// <param name="address">The address as the administrator typed it.</param>
    /// <param name="baseAddress">The usable base address.</param>
    /// <returns><c>false</c> when the address is not an absolute http or https address.</returns>
    internal static bool TryGetBaseAddress(string? address, out Uri baseAddress)
    {
        baseAddress = null!;
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !Uri.TryCreate(trimmed.TrimEnd('/') + "/", UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        baseAddress = parsed;
        return true;
    }

    /// <inheritdoc />
    public async Task<RetimerConnectionStatus> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var configuration = _settings.Current;
        var key = _settings.Reveal(configuration.RetimerApiKey).Trim();
        if (string.IsNullOrWhiteSpace(configuration.RetimerUrl) || string.IsNullOrEmpty(key))
        {
            return RetimerConnectionStatus.NotConfigured;
        }

        if (!TryGetBaseAddress(configuration.RetimerUrl, out var baseAddress))
        {
            return RetimerConnectionStatus.InvalidAddress;
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        try
        {
            // Listing the key's own jobs is the cheapest call that needs a valid key.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "v1/jobs"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return RetimerConnectionStatus.Unauthorized;
            }

            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                return RetimerConnectionStatus.Ok;
            }

            _logger.LogInformation("The retimer check got status {Status} from {Address}", (int)response.StatusCode, baseAddress);
            return RetimerConnectionStatus.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation(ex, "The retimer at {Address} could not be reached", baseAddress);
            return RetimerConnectionStatus.Unreachable;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "The retimer at {Address} did not answer in time", baseAddress);
            return RetimerConnectionStatus.Unreachable;
        }
    }

    /// <inheritdoc />
    async Task<RetimerFeatures> IRetimerApi.GetFeaturesAsync(CancellationToken cancellationToken)
    {
        var (baseAddress, key) = Connection();
        var cacheKey = baseAddress.AbsoluteUri + "\n" + key;
        lock (_featuresLock)
        {
            if (_features is { } cached && cached.Key == cacheKey && DateTimeOffset.UtcNow - cached.At < _featuresLifetime)
            {
                return cached.Features;
            }
        }

        RetimerFeatures features;
        try
        {
            var json = await SendJsonAsync(HttpMethod.Get, "v1/features", null, cancellationToken).ConfigureAwait(false);
            var names = (json["features"] as JsonArray ?? []).Select(n => n?.ToString()).ToHashSet(StringComparer.Ordinal);
            var limits = json["limits"];
            var defaults = new RetimerFeatures();
            features = new RetimerFeatures
            {
                ReferenceTracks = names.Contains("reference_tracks"),
                RemoveSignsAndCredits = names.Contains("remove_signs_and_credits"),
                Ocr = names.Contains("ocr"),
                MaxReferenceSubtitles = names.Contains("reference_tracks") ? (int)Math.Clamp(Number(limits?["reference_subtitles"]) ?? 6, 1, 20) : 1,
                RequestBytes = (long)(Number(limits?["request_bytes"]) ?? defaults.RequestBytes),
                AudioBytes = (long)(Number(limits?["audio_bytes"]) ?? defaults.AudioBytes),
                AudioMinutes = Number(limits?["audio_minutes"]) ?? defaults.AudioMinutes,
                ImageSubtitleBytes = (long)(Number(limits?["image_subtitle_bytes"]) ?? defaults.ImageSubtitleBytes)
            };
        }
        catch (RetimerException ex) when (ex.Error == RetimerError.NotFound)
        {
            // An older server has no such call. It reads picture subtitles, and takes one reference subtitle.
            features = new RetimerFeatures { Ocr = true };
        }

        lock (_featuresLock)
        {
            _features = (cacheKey, DateTimeOffset.UtcNow, features);
        }

        return features;
    }

    /// <inheritdoc />
    async Task<RetimerJob> IRetimerApi.SubmitAsync(RetimerSubmission submission, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(FilePart(submission.Target), "target", UploadName(submission.TargetName));
        foreach (var reference in submission.References)
        {
            form.Add(FilePart(reference.Content), "reference_subtitle", UploadName(reference.FileName));
        }

        FileStream? audio = null;
        try
        {
            if (submission.AudioPath is not null)
            {
                audio = new FileStream(submission.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                var part = new StreamContent(audio);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "reference_audio", UploadName(Path.GetFileName(submission.AudioPath)));
            }

            form.Add(new StringContent(submission.Options.ToJsonString(), Encoding.UTF8), "options");
            return ReadJob(await SendJsonAsync(HttpMethod.Post, "v1/jobs", form, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (audio is not null)
            {
                await audio.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    async Task<RetimerJob> IRetimerApi.SubmitOcrAsync(string fileName, byte[] content, string? language, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(FilePart(content), "subtitle", UploadName(fileName));
        var options = new JsonObject { ["output_format"] = "srt" };
        if (!string.IsNullOrEmpty(language))
        {
            options["language"] = language;
        }

        form.Add(new StringContent(options.ToJsonString(), Encoding.UTF8), "options");
        return ReadJob(await SendJsonAsync(HttpMethod.Post, "v1/ocr", form, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    async Task<RetimerJob> IRetimerApi.GetJobAsync(string id, CancellationToken cancellationToken) =>
        ReadJob(await SendJsonAsync(HttpMethod.Get, "v1/jobs/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    async Task<byte[]> IRetimerApi.GetResultAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/jobs/" + Uri.EscapeDataString(id) + "/result", null, cancellationToken).ConfigureAwait(false);
        await ThrowUnlessSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    async Task IRetimerApi.DeleteJobAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, "v1/jobs/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false);
        }
        catch (RetimerException ex)
        {
            _logger.LogDebug(ex, "A finished job could not be removed from Retimer API");
        }
    }

    /// <summary>
    /// Reads a job out of Retimer API's answer.
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <returns>The job.</returns>
    internal static RetimerJob ReadJob(JsonNode json)
    {
        var summary = json["summary"];
        var reference = summary?["reference"];
        var candidates = new List<RetimerCandidate>();
        foreach (var candidate in reference?["candidates"] as JsonArray ?? [])
        {
            if (candidate is null)
            {
                continue;
            }

            candidates.Add(new RetimerCandidate(
                (int)(Number(candidate["index"]) ?? candidates.Count),
                Text(candidate["language"]),
                Text(candidate["title"]),
                Text(candidate["verdict"]) ?? string.Empty,
                Text(candidate["reason"])));
        }

        return new RetimerJob
        {
            Id = Text(json["id"]) ?? string.Empty,
            Status = Text(json["status"]) ?? string.Empty,
            Stage = Text(json["stage"]),
            Outcome = Text(json["outcome"]),
            ErrorCode = Text(json["error"]?["code"]),
            ErrorMessage = Text(json["error"]?["message"]),
            CostUsd = Number(summary?["cost_usd"]) ?? 0,
            Confidence = Number(summary?["confidence"]),
            Warnings = (summary?["warnings"] as JsonArray ?? []).Select(w => w?.ToString() ?? string.Empty).Where(w => w.Length > 0).ToList(),
            Fit = Text(summary?["fit"]),
            ShiftsMs = (summary?["blocks"] as JsonArray ?? [])
                .Select(block => Number(block?["shift_ms"]))
                .Where(shift => shift is not null)
                .Select(shift => (int)Math.Round(shift!.Value))
                .ToList(),
            HasReferenceReport = reference is JsonObject,
            ChosenReference = reference is JsonObject && Number(reference["chosen"]) is { } chosen ? (int)chosen : null,
            ReferenceReason = Text(reference?["reason"]),
            Candidates = candidates
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static ByteArrayContent FilePart(byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    // A plain file name for the upload: the service reads the extension, and odd characters only cause trouble in a header.
    private static string UploadName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));
        if (inner.Equals(".sup", StringComparison.OrdinalIgnoreCase))
        {
            extension = inner + extension;
        }

        var stem = new string(Path.GetFileNameWithoutExtension(fileName).Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ' ').Take(60).ToArray()).Trim();
        return (stem.Length == 0 ? "subtitle" : stem) + extension.ToLowerInvariant();
    }

    private (Uri BaseAddress, string Key) Connection()
    {
        var configuration = _settings.Current;
        var key = _settings.Reveal(configuration.RetimerApiKey).Trim();
        if (string.IsNullOrWhiteSpace(configuration.RetimerUrl) || string.IsNullOrEmpty(key))
        {
            throw new RetimerException(RetimerError.NotConfigured, "Retimer API's address or key is missing in the settings.");
        }

        if (!TryGetBaseAddress(configuration.RetimerUrl, out var baseAddress))
        {
            throw new RetimerException(RetimerError.InvalidAddress, "Retimer API's address must start with http:// or https://.");
        }

        return (baseAddress, key);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var (baseAddress, key) = Connection();
        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        // An upload with audio can take longer than the usual limit for a call.
        client.Timeout = content is null ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(10);
        using var request = new HttpRequestMessage(method, new Uri(baseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RetimerException(RetimerError.Unavailable, "Retimer API could not be reached.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetimerException(RetimerError.Unavailable, "Retimer API did not answer in time.", ex);
        }
    }

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, content, cancellationToken).ConfigureAwait(false);
        await ThrowUnlessSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
                ?? throw new RetimerException(RetimerError.Unavailable, "Retimer API sent an empty answer.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new RetimerException(RetimerError.Unavailable, "Retimer API sent an answer that could not be read.", ex);
        }
    }

    private async Task ThrowUnlessSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? detail = null;
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            detail = Text(json?["error"]?["message"]) ?? Text(json?["detail"]);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // Not JSON: the status says enough.
        }

        var status = (int)response.StatusCode;
        _logger.LogInformation("Retimer API answered with status {Status}: {Detail}", status, detail);
        var said = string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail;
        throw status switch
        {
            401 or 403 => new RetimerException(RetimerError.Unauthorized, "Retimer API rejected the key."),
            404 => new RetimerException(RetimerError.NotFound, "Retimer API does not know this." + said),
            413 => new RetimerException(RetimerError.TooLarge, "The upload is larger than Retimer API accepts." + said),
            422 => new RetimerException(RetimerError.Refused, "Retimer API could not use what was sent." + said),
            429 => new RetimerException(RetimerError.LimitReached, "A limit of Retimer API is reached." + said) { RetryAfter = response.Headers.RetryAfter?.Delta },
            _ => new RetimerException(RetimerError.Unavailable, string.Create(CultureInfo.InvariantCulture, $"Retimer API answered with status {status}.") + said)
        };
    }
}
