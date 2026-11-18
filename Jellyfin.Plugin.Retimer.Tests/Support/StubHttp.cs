using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Retimer.Tests.Support;

/// <summary>
/// What one request looked like when it left the client.
/// </summary>
public sealed record SentRequest(HttpMethod Method, Uri Url, string? Authorization, string? UserAgent);

/// <summary>
/// Answers requests from a queue and remembers what was sent. Nothing leaves the process.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public List<SentRequest> Requests { get; } = [];

    /// <summary>Gets the body of each request as text, in the order sent. Empty for a request without one.</summary>
    public List<string> Bodies { get; } = [];

    public StubHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        _answers.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        return this;
    }

    public StubHandler Text(string body, string mediaType, HttpStatusCode status = HttpStatusCode.OK)
    {
        _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        return this;
    }

    public StubHandler Throws(Exception exception)
    {
        _answers.Enqueue(() => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add(new SentRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("Authorization", out var authorization) ? string.Join(",", authorization) : null,
            request.Headers.UserAgent.ToString()));

        if (_answers.Count == 0)
        {
            throw new InvalidOperationException("The test queued no answer for " + request.RequestUri);
        }

        return _answers.Dequeue()();
    }
}

public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
