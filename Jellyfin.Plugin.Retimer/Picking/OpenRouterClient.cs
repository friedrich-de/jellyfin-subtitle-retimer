using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Picking;

/// <summary>
/// One call of a tool by the model.
/// </summary>
internal sealed record ToolCall(string Id, string Name, JsonNode Arguments);

/// <summary>
/// What the model answered: the tools it called, its text, and what the call cost.
/// </summary>
internal sealed record ChatAnswer(IReadOnlyList<ToolCall> Calls, string? Text, double CostUsd, JsonNode Message);

/// <summary>
/// Why a call to the model failed.
/// </summary>
internal enum ModelError
{
    NotConfigured,
    Unauthorized,
    NoCredit,
    UnknownModel,
    RateLimited,
    BadAnswer,
    Unavailable
}

internal sealed class ModelException : Exception
{
    public ModelException(ModelError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public ModelError Error { get; }
}

/// <summary>
/// Calls a model with tools.
/// </summary>
internal interface IModelClient
{
    /// <summary>Gets the model in use, or <c>null</c> when there is no key or no model.</summary>
    ModelEntry? Current { get; }

    Task<ChatAnswer> ChatAsync(string modelId, JsonArray messages, JsonArray tools, string? forcedTool, int maxTokens, CancellationToken cancellationToken);
}

/// <summary>
/// Calls models through OpenRouter's chat API with the key from the plugin's settings.
/// </summary>
internal sealed class OpenRouterClient : IModelClient
{
    /// <summary>
    /// For tests only: when this environment variable holds an http or https address, the client
    /// calls it in place of openrouter.ai. It is read once, when the server starts.
    /// </summary>
    public const string BaseAddressVariable = "RETIMER_OPENROUTER_API_URL";

    private static readonly Uri _defaultBaseAddress = new("https://openrouter.ai/api/v1/");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginSettings _settings;
    private readonly ILogger<OpenRouterClient> _logger;
    private readonly Uri _baseAddress;

    public OpenRouterClient(IHttpClientFactory httpClientFactory, IPluginSettings settings, ILogger<OpenRouterClient> logger)
        : this(httpClientFactory, settings, logger, JimakuClient.ResolveBaseAddress(Environment.GetEnvironmentVariable(BaseAddressVariable), _defaultBaseAddress))
    {
    }

    internal OpenRouterClient(IHttpClientFactory httpClientFactory, IPluginSettings settings, ILogger<OpenRouterClient> logger, Uri baseAddress)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
        _baseAddress = baseAddress;
    }

    public ModelEntry? Current
    {
        get
        {
            var configuration = _settings.Current;
            if (string.IsNullOrWhiteSpace(configuration.OpenRouterApiKey) || string.IsNullOrWhiteSpace(configuration.ModelInUse))
            {
                return null;
            }

            return configuration.Models.FirstOrDefault(model => string.Equals(model.Id, configuration.ModelInUse, StringComparison.Ordinal));
        }
    }

    public async Task<ChatAnswer> ChatAsync(string modelId, JsonArray messages, JsonArray tools, string? forcedTool, int maxTokens, CancellationToken cancellationToken)
    {
        var key = _settings.Reveal(_settings.Current.OpenRouterApiKey).Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new ModelException(ModelError.NotConfigured, "No OpenRouter API key is set in the plugin's settings.");
        }

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = messages.DeepClone(),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["usage"] = new JsonObject { ["include"] = true }
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = forcedTool is null
                ? "required"
                : new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = forcedTool } };
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("X-Title", "Subtitle Retimer for Jellyfin");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        JsonNode? json;
        int status;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                json = null;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ModelException(ModelError.Unavailable, "OpenRouter could not be reached.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelException(ModelError.Unavailable, "The model did not answer in time.", ex);
        }

        // OpenRouter reports some failures with status 200 and an error object.
        var error = json?["error"];
        if (status is < 200 or >= 300 || error is not null)
        {
            var code = status;
            if (error?["code"] is JsonValue value && value.TryGetValue<int>(out var inner))
            {
                code = inner;
            }

            var detail = error?["message"]?.ToString();
            _logger.LogInformation("OpenRouter answered status {Status} for model {Model}: {Detail}", code, modelId, detail);
            throw code switch
            {
                401 or 403 => new ModelException(ModelError.Unauthorized, "OpenRouter rejected the key."),
                402 => new ModelException(ModelError.NoCredit, "The OpenRouter account has no credit left."),
                400 or 404 => new ModelException(ModelError.UnknownModel, "OpenRouter could not run this model" + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + Shorten(detail))),
                429 => new ModelException(ModelError.RateLimited, "OpenRouter's rate limit was hit."),
                _ => new ModelException(ModelError.Unavailable, string.Create(CultureInfo.InvariantCulture, $"OpenRouter answered with status {code}."))
            };
        }

        var message = (json?["choices"] as JsonArray)?.FirstOrDefault()?["message"];
        if (message is null)
        {
            throw new ModelException(ModelError.BadAnswer, "The model sent an answer that could not be read.");
        }

        var calls = new List<ToolCall>();
        foreach (var call in message["tool_calls"] as JsonArray ?? [])
        {
            var name = call?["function"]?["name"]?.ToString();
            var arguments = call?["function"]?["arguments"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            JsonNode? parsed = null;
            try
            {
                parsed = string.IsNullOrWhiteSpace(arguments) ? new JsonObject() : JsonNode.Parse(arguments);
            }
            catch (JsonException)
            {
                // Arguments that are not JSON count as no arguments.
            }

            calls.Add(new ToolCall(call?["id"]?.ToString() ?? name, name, parsed ?? new JsonObject()));
        }

        return new ChatAnswer(calls, message["content"]?.ToString(), ReadCost(json?["usage"]), message.DeepClone());
    }

    /// <summary>
    /// What the call cost. With an account's own upstream key OpenRouter reports its own cost as zero
    /// and the upstream's separately, so both are added.
    /// </summary>
    internal static double ReadCost(JsonNode? usage)
    {
        static double Number(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<double>(out var number) && number > 0 ? number : 0;

        return Number(usage?["cost"]) + Number(usage?["cost_details"]?["upstream_inference_cost"]);
    }

    private static string Shorten(string text) => text.Length > 160 ? text[..160] + "…" : text;
}
