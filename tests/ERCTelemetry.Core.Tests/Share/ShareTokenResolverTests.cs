using System.Net;
using ERCTelemetry.Core.Share;
using ERCTelemetry.Core.Tests.Llm;
using Xunit;

namespace ERCTelemetry.Core.Tests.Share;

/// <summary>ShareTokenResolver: settings override, /api/config fetch, built-in default
/// fallback and short-TTL caching — the auto-token behaviour that lets Enduser share a
/// session without configuring a token.</summary>
public sealed class ShareTokenResolverTests
{
    private const string BaseUrl = "https://telemetrie.erdi-erc.de";

    [Fact]
    public async Task Settings_token_wins_without_any_http_call()
    {
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no call expected"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => "user-token", handler);

        var token = await resolver.GetTokenAsync();

        Assert.Equal("user-token", token);
        Assert.Null(handler.LastRequest);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Fetches_token_from_config_endpoint_when_settings_token_is_empty()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"shareToken":"server-token"}"""));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler);

        var token = await resolver.GetTokenAsync();

        Assert.Equal("server-token", token);
        Assert.Equal($"{BaseUrl}/api/config", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
    }

    [Fact]
    public async Task Falls_back_to_builtin_default_when_config_is_unreachable()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.InternalServerError));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "default-token");

        var token = await resolver.GetTokenAsync();

        Assert.Equal("default-token", token);
    }

    [Fact]
    public async Task Falls_back_to_builtin_default_when_config_has_no_token()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "default-token");

        var token = await resolver.GetTokenAsync();

        Assert.Equal("default-token", token);
    }

    [Fact]
    public async Task Returns_null_when_every_source_is_empty()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.ServiceUnavailable));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "");

        var token = await resolver.GetTokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public async Task Caches_the_fetched_token_within_the_ttl()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"shareToken":"server-token"}"""));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, cacheTtl: TimeSpan.FromMinutes(5));

        _ = await resolver.GetTokenAsync();
        var second = await resolver.GetTokenAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("server-token", second);
    }

    [Fact]
    public async Task Refetches_after_the_cache_ttl_expired()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"shareToken":"server-token"}"""));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, cacheTtl: TimeSpan.FromMilliseconds(1));

        _ = await resolver.GetTokenAsync();
        await Task.Delay(5);
        var token = await resolver.GetTokenAsync();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("server-token", token);
    }

    [Fact]
    public async Task Settings_token_trims_whitespace()
    {
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no call expected"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => "  user-token  ", handler);

        var token = await resolver.GetTokenAsync();

        Assert.Equal("user-token", token);
    }

    [Fact]
    public async Task Falls_back_to_default_when_config_host_is_unreachable()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("no route to host"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "default-token");

        var token = await resolver.GetTokenAsync();

        Assert.Equal("default-token", token);
    }

    [Fact]
    public async Task Falls_back_to_default_when_config_request_times_out()
    {
        var handler = new FakeHttpHandler(_ => throw new TaskCanceledException("timeout"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "default-token");

        var token = await resolver.GetTokenAsync();

        Assert.Equal("default-token", token);
    }

    [Fact]
    public async Task Falls_back_to_default_when_config_answer_is_not_valid_json()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("this is not json"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "default-token");

        var token = await resolver.GetTokenAsync();

        Assert.Equal("default-token", token);
    }

    [Fact]
    public async Task Keeps_the_last_good_token_when_a_refetch_fails()
    {
        FakeHttpHandler? handler = null;
        handler = new FakeHttpHandler(req =>
            handler!.RequestCount == 1
                ? FakeHttpHandler.Json("""{"shareToken":"server-token"}""")
                : throw new HttpRequestException("server unreachable"));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, cacheTtl: TimeSpan.FromMilliseconds(1));

        var first = await resolver.GetTokenAsync();
        await Task.Delay(5);
        var second = await resolver.GetTokenAsync();

        Assert.Equal("server-token", first);
        // The refetch failed after the TTL expired — keep serving the last-known-good
        // token through the outage instead of degrading to the built-in default.
        Assert.Equal("server-token", second);
        Assert.Equal(2, handler!.RequestCount);
    }

    [Fact]
    public async Task Propagates_callers_cancellation_instead_of_falling_back()
    {
        var handler = new FakeHttpHandler(_ => throw new OperationCanceledException());
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, handler, defaultToken: "default-token");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // HttpClient wraps the handler's cancellation as TaskCanceledException (a
        // subclass of OperationCanceledException) — any of the two must propagate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.GetTokenAsync(cts.Token));
    }
}
