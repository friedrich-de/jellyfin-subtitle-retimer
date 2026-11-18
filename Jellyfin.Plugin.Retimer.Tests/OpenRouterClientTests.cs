using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Picking;
using Jellyfin.Plugin.Retimer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Retimer.Tests;

public class OpenRouterClientTests
{
    private const string Answer = """
        {"id":"gen-1","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"call_9","type":"function","function":{"name":"choose_entry","arguments":"{\"entry_id\":\"384\",\"reason\":\"fits\"}"}}]}}],
         "usage":{"cost":0,"cost_details":{"upstream_inference_cost":0.00042}}}
        """;

    private readonly StubHandler _handler = new();
    private readonly StubSettings _settings = new() { Current = new PluginConfiguration { OpenRouterApiKey = " sk-or-test " } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonArray Tools => [new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "choose_entry" } }];

    private static JsonArray Messages => [new JsonObject { ["role"] = "user", ["content"] = "Which entry?" }];

    private OpenRouterClient Client() =>
        new(new StubHttpClientFactory(_handler), _settings, NullLogger<OpenRouterClient>.Instance, new Uri("https://openrouter.example/api/v1/"));

    [Fact]
    public async Task A_call_goes_to_the_chat_address_with_the_key_and_a_forced_tool()
    {
        _handler.Json(Answer);

        await Client().ChatAsync("anthropic/claude-haiku-5.5", Messages, Tools, "choose_entry", 600, Token);

        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("https://openrouter.example/api/v1/chat/completions", sent.Url.AbsoluteUri);
        Assert.Equal("Bearer sk-or-test", sent.Authorization);
        var body = JsonNode.Parse(_handler.Bodies[0])!;
        Assert.Equal("anthropic/claude-haiku-5.5", body["model"]!.GetValue<string>());
        Assert.Equal(600, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(0, body["temperature"]!.GetValue<int>());
        Assert.True(body["usage"]!["include"]!.GetValue<bool>());
        Assert.Equal("choose_entry", body["tool_choice"]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Which entry?", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_forced_tool_the_model_must_still_call_one()
    {
        _handler.Json(Answer);

        await Client().ChatAsync("m/x", Messages, Tools, null, 100, Token);

        Assert.Equal("required", JsonNode.Parse(_handler.Bodies[0])!["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tool_call_and_the_cost_are_read_from_the_answer()
    {
        _handler.Json(Answer);

        var answer = await Client().ChatAsync("m/x", Messages, Tools, "choose_entry", 100, Token);

        var call = Assert.Single(answer.Calls);
        Assert.Equal("choose_entry", call.Name);
        Assert.Equal("384", call.Arguments["entry_id"]!.GetValue<string>());

        // With an account's own upstream key OpenRouter reports its own cost as zero and the upstream's beside it.
        Assert.Equal(0.00042, answer.CostUsd, 6);
    }

    [Theory]
    [InlineData("""{"cost":0.002}""", 0.002)]
    [InlineData("""{"cost":0.002,"cost_details":{"upstream_inference_cost":0.001}}""", 0.003)]
    [InlineData("""{"cost":null}""", 0)]
    [InlineData("""{"cost":-1}""", 0)]
    [InlineData("""{}""", 0)]
    public void The_cost_adds_what_OpenRouter_and_the_upstream_report(string usage, double expected)
    {
        Assert.Equal(expected, OpenRouterClient.ReadCost(JsonNode.Parse(usage)), 6);
    }

    [Fact]
    public async Task Arguments_that_are_not_JSON_count_as_none()
    {
        _handler.Json("""{"choices":[{"message":{"tool_calls":[{"function":{"name":"choose_entry","arguments":"not json"}}]}}]}""");

        var answer = await Client().ChatAsync("m/x", Messages, Tools, "choose_entry", 100, Token);

        Assert.Empty(Assert.Single(answer.Calls).Arguments.AsObject());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, nameof(ModelError.Unauthorized))]
    [InlineData(HttpStatusCode.Forbidden, nameof(ModelError.Unauthorized))]
    [InlineData(HttpStatusCode.PaymentRequired, nameof(ModelError.NoCredit))]
    [InlineData(HttpStatusCode.NotFound, nameof(ModelError.UnknownModel))]
    [InlineData(HttpStatusCode.BadRequest, nameof(ModelError.UnknownModel))]
    [InlineData(HttpStatusCode.TooManyRequests, nameof(ModelError.RateLimited))]
    [InlineData(HttpStatusCode.BadGateway, nameof(ModelError.Unavailable))]
    public async Task A_refusal_becomes_an_error_the_page_can_word(HttpStatusCode status, string expected)
    {
        _handler.Json("""{"error":{"message":"No endpoints found for this model."}}""", status);

        var thrown = await Assert.ThrowsAsync<ModelException>(() => Client().ChatAsync("m/x", Messages, Tools, null, 100, Token));

        Assert.Equal(expected, thrown.Error.ToString());
        Assert.DoesNotContain("sk-or-test", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_inside_an_answer_with_status_200_counts_too()
    {
        _handler.Json("""{"error":{"code":402,"message":"Insufficient credits"}}""");

        var thrown = await Assert.ThrowsAsync<ModelException>(() => Client().ChatAsync("m/x", Messages, Tools, null, 100, Token));

        Assert.Equal(ModelError.NoCredit, thrown.Error);
    }

    [Fact]
    public async Task An_answer_without_a_message_is_a_bad_answer()
    {
        _handler.Json("""{"choices":[]}""");

        var thrown = await Assert.ThrowsAsync<ModelException>(() => Client().ChatAsync("m/x", Messages, Tools, null, 100, Token));

        Assert.Equal(ModelError.BadAnswer, thrown.Error);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        _settings.Current = new PluginConfiguration { OpenRouterApiKey = " " };

        var thrown = await Assert.ThrowsAsync<ModelException>(() => Client().ChatAsync("m/x", Messages, Tools, null, 100, Token));

        Assert.Equal(ModelError.NotConfigured, thrown.Error);
        Assert.Empty(_handler.Requests);
        Assert.Null(Client().Current);
    }

    [Fact]
    public void The_model_in_use_is_one_of_the_list()
    {
        _settings.Current = new PluginConfiguration
        {
            OpenRouterApiKey = "k",
            Models = [new ModelEntry { Id = "a/one", Name = "One" }, new ModelEntry { Id = "b/two", Name = "Two" }],
            ModelInUse = "b/two"
        };
        Assert.Equal("Two", Client().Current!.Name);

        _settings.Current.ModelInUse = "c/three";
        Assert.Null(Client().Current);
    }
}
