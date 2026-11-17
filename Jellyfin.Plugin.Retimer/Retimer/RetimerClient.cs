using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Retimer;

/// <summary>
/// Calls the retimer's HTTP API with the address and key from the plugin's settings.
/// </summary>
public sealed class RetimerClient : IRetimerClient
{
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
        var key = configuration.RetimerApiKey?.Trim();
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
}
