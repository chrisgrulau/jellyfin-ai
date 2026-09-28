using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ai.Tests;

/// <summary>A request as the fake saw it.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Answers HTTP requests from a script instead of the network: nothing leaves the process.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _replies = new();

    public List<SeenRequest> Seen { get; } = [];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            r.Headers.TryAddWithoutValidation(name, value);
        }

        return r;
    }

    public FakeHttp Reply(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        _replies.Enqueue(_ => Json(body, status, headers));
        return this;
    }

    public FakeHttp Throw(Exception ex)
    {
        _replies.Enqueue(_ => throw ex);
        return this;
    }

    public HttpClient Client() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(",", h.Value);
        }

        Seen.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), headers, body));
        if (_replies.Count == 0)
        {
            throw new InvalidOperationException("No reply scripted for " + request.RequestUri);
        }

        return _replies.Dequeue()(request);
    }
}
