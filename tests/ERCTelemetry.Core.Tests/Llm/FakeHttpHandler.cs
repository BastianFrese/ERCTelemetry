using System.Net;
using System.Text;
using System.Text.Json;

namespace ERCTelemetry.Core.Tests.Llm;

/// <summary>Test double for <see cref="HttpMessageHandler"/>: returns a canned response
/// and records the last request so tests can assert on the URL, headers and body.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Number of requests the handler has served — lets tests assert a cache hit
    /// (count unchanged) vs. a refetch.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Request body captured during SendAsync — the client disposes the request
    /// (and its content) after the call, so tests must read the body here, not later.</summary>
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        RequestCount++;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        }

        return _responder(request);
    }

    /// <summary>A 200 response shaped like Ollama's /api/chat answer.</summary>
    public static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}},"done":true}""",
            Encoding.UTF8, "application/json"),
    };

    /// <summary>A 200 response with an arbitrary JSON body (e.g. /api/tags).</summary>
    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Error(HttpStatusCode status) => new(status);
}
