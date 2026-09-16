using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ERCTelemetry.Core.Share;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ERCTelemetry.ShareServer.Tests;

/// <summary>Server-side Twitch login handshake: start → callback (code exchange against a
/// stubbed Twitch API) → poll status → delete. Verifies the double-redirect guard (the
/// same code is only exchanged once), the nonce binding on status/delete, the once-only
/// token delivery and the parallel-callback guard (a losing exchange cannot clobber a win).
/// Shares the "ShareServer" collection (see <see cref="ShareServerTests"/>).</summary>
[Collection("ShareServer")]
public sealed class TwitchLoginTests : IClassFixture<TwitchLoginFixture>
{
    private readonly TwitchLoginFixture _fixture;

    public TwitchLoginTests(TwitchLoginFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Start_returns_authorize_url_state_and_nonce()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/twitch/login/start");
        AddToken(request);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var state = root.GetProperty("state").GetString();
        var nonce = root.GetProperty("nonce").GetString();
        var url = root.GetProperty("authorizeUrl").GetString();
        Assert.Equal(32, state?.Length); // Guid N
        Assert.Equal(32, nonce?.Length); // Guid N
        Assert.StartsWith("https://id.twitch.tv/oauth2/authorize?", url);
        Assert.Contains("client_id=8cekqatgkipstivsw63q2b44hati81", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Ftelemetrie.erdi-erc.de%2Ftwitch%2Fcallback", url);
        Assert.Contains($"state={state}", url);
    }

    [Fact]
    public async Task Start_requires_token()
    {
        var response = await _fixture.Client.PostAsync("/twitch/login/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Callback_with_error_marks_login_failed()
    {
        var (state, nonce) = await StartLoginAsync();

        var callback = await _fixture.Client.GetAsync($"/twitch/callback?error=access_denied&state={state}");
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("Login abgebrochen", await callback.Content.ReadAsStringAsync());

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("error", status.RootElement.GetProperty("status").GetString());
        Assert.Equal("access_denied", status.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Callback_with_arbitrary_error_does_not_echo_raw_provider_input()
    {
        // M2: a provider error that is not the known user cancel is stored as the generic
        // message — raw provider input must never flow back to the app via the status poll.
        var (state, nonce) = await StartLoginAsync();

        var callback = await _fixture.Client.GetAsync(
            $"/twitch/callback?error=provider_hiccup_%3Cscript%3Ex%3C%2Fscript%3E&state={state}");
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("error", status.RootElement.GetProperty("status").GetString());
        Assert.Equal("Login fehlgeschlagen", status.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Callback_error_after_a_success_does_not_clobber_the_result()
    {
        // M1/LOW-3 guard on the direct-error path: a straggling error callback (a second
        // provider redirect) arriving after the success must not turn the stored token
        // into an error.
        var (state, nonce) = await StartLoginAsync();
        Assert.Contains("Login erfolgreich",
            await (await _fixture.Client.GetAsync($"/twitch/callback?code={NewCode()}&state={state}"))
                .Content.ReadAsStringAsync());
        await _fixture.Client.GetAsync($"/twitch/callback?error=access_denied&state={state}");

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        var root = status.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("fake-token", root.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Callback_with_unknown_state_shows_error_page()
    {
        var response = await _fixture.Client.GetAsync("/twitch/callback?code=abc&state=unknown");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Login abgelaufen", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_exchanges_code_and_returns_token()
    {
        var (state, nonce) = await StartLoginAsync();
        var code = NewCode();

        var callback = await _fixture.Client.GetAsync($"/twitch/callback?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("Login erfolgreich", await callback.Content.ReadAsStringAsync());

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        var root = status.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("fake-token", root.GetProperty("token").GetString());
        Assert.Equal("fake-refresh", root.GetProperty("refreshToken").GetString());
        Assert.Equal("playerone", root.GetProperty("user").GetProperty("login").GetString());
        Assert.Equal("Player One", root.GetProperty("user").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Callback_double_redirect_exchanges_only_once()
    {
        var (state, nonce) = await StartLoginAsync();
        var code = NewCode();
        var before = _fixture.Twitch.TokenRequests;

        var first = await _fixture.Client.GetAsync($"/twitch/callback?code={code}&state={state}");
        Assert.Contains("Login erfolgreich", await first.Content.ReadAsStringAsync());
        var second = await _fixture.Client.GetAsync($"/twitch/callback?code={code}&state={state}");
        Assert.Contains("Login erfolgreich", await second.Content.ReadAsStringAsync());

        // The stub is shared across the class, so assert the delta: exactly one exchange.
        Assert.Equal(before + 1, _fixture.Twitch.TokenRequests);
        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("success", status.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Parallel_callbacks_do_not_clobber_a_success()
    {
        // Two genuinely parallel callbacks with the same code: both read the pending
        // attempt, both hit the stub — which redeems each code exactly once, so the loser
        // gets "invalid code" and lands in the catch. Its error must not overwrite the
        // winner's success (LOW 3).
        var (state, nonce) = await StartLoginAsync();
        var code = NewCode();
        var url = $"/twitch/callback?code={code}&state={state}";

        await Task.WhenAll(
            _fixture.Client.GetAsync(url),
            _fixture.Client.GetAsync(url));

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        var root = status.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("fake-token", root.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Status_requires_token()
    {
        var response = await _fixture.Client.GetAsync("/twitch/login/status?state=abc");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_requires_the_login_nonce()
    {
        var (state, _) = await StartLoginAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/twitch/login/status?state={state}");
        AddToken(request);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_rejects_a_wrong_nonce()
    {
        var (state, _) = await StartLoginAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/twitch/login/status?state={state}");
        AddToken(request);
        request.Headers.Add(ShareConstants.LoginNonceHeader, "wrong-nonce");
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_delivers_the_token_only_once()
    {
        var (state, nonce) = await StartLoginAsync();
        await _fixture.Client.GetAsync($"/twitch/callback?code={NewCode()}&state={state}");

        // First poll delivers the token and removes the attempt…
        var first = await GetStatusAsync(state, nonce);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"token\":\"fake-token\"", await first.Content.ReadAsStringAsync());

        // …a second poll of the same state is "notfound".
        var second = await GetStatusAsync(state, nonce);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Delete_requires_the_login_nonce_and_removes_attempt()
    {
        var (state, nonce) = await StartLoginAsync();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/twitch/login/{state}");
        AddToken(delete);
        delete.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        var deleteResponse = await _fixture.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var status = await GetStatusAsync(state, nonce);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }

    [Fact]
    public async Task Delete_with_wrong_nonce_keeps_the_attempt()
    {
        var (state, nonce) = await StartLoginAsync();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/twitch/login/{state}");
        AddToken(delete);
        delete.Headers.Add(ShareConstants.LoginNonceHeader, "wrong-nonce");
        var deleteResponse = await _fixture.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode); // idempotent

        // The attempt is still alive — a correct-nonce poll answers pending.
        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("pending", status.RootElement.GetProperty("status").GetString());
    }

    private static string NewCode() => Guid.NewGuid().ToString("N");

    private async Task<(string State, string Nonce)> StartLoginAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/twitch/login/start");
        AddToken(request);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("state").GetString()!,
                doc.RootElement.GetProperty("nonce").GetString()!);
    }

    private async Task<HttpResponseMessage> GetStatusAsync(string state, string nonce)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/twitch/login/status?state={state}");
        AddToken(request);
        request.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<string> GetStatusJsonAsync(string state, string nonce)
    {
        var response = await GetStatusAsync(state, nonce);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AddToken(HttpRequestMessage request) =>
        request.Headers.Add(ShareConstants.TokenHeader, TwitchLoginFixture.Token);
}

/// <summary>One WebApplicationFactory per test class with a stubbed Twitch API: the
/// ShareServer's token/users endpoints point at a local stub that answers with a fake
/// token + user, counts token requests (for the double-redirect guard test) and rejects
/// already-redeemed codes (for the parallel-callback race).</summary>
public sealed class TwitchLoginFixture : IDisposable
{
    public const string Token = "test-token";
    public const string ClientSecret = "test-secret";

    private readonly TwitchStub _twitch;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _root;

    public TwitchLoginFixture()
    {
        _twitch = new TwitchStub();
        _root = Path.Combine(Path.GetTempPath(), $"erc-twitch-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Share__Token", Token);
        Environment.SetEnvironmentVariable("Share__RootPath", _root);
        Environment.SetEnvironmentVariable("Share__TwitchTokenEndpoint", $"{_twitch.BaseUrl}/token");
        Environment.SetEnvironmentVariable("Share__TwitchUsersEndpoint", $"{_twitch.BaseUrl}/users");
        Environment.SetEnvironmentVariable("Share__TwitchClientSecret", ClientSecret);
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public HttpClient Client { get; }

    public TwitchStub Twitch => _twitch;

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _twitch.Dispose();
        Environment.SetEnvironmentVariable("Share__Token", null);
        Environment.SetEnvironmentVariable("Share__RootPath", null);
        Environment.SetEnvironmentVariable("Share__TwitchTokenEndpoint", null);
        Environment.SetEnvironmentVariable("Share__TwitchUsersEndpoint", null);
        Environment.SetEnvironmentVariable("Share__TwitchClientSecret", null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Minimal stand-in for the Twitch token + users API: answers with a fake token
/// and user, counts how often the token endpoint was hit, and redeems each authorization
/// code exactly once (a re-used code gets Twitch's "Invalid code" error).</summary>
public sealed class TwitchStub : IDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte> _redeemedCodes = new();
    private int _tokenRequests;

    public TwitchStub()
    {
        // Find a free port, then host the stub there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        _app.MapPost("/token", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _tokenRequests);
            // Twitch requires the client secret in the exchange — reject the request if
            // the server did not send it, so the tests prove the secret is forwarded.
            var form = await ctx.Request.ReadFormAsync();
            if (form["client_secret"] != TwitchLoginFixture.ClientSecret)
            {
                return Results.Json(new { status = 400, message = "Invalid client credentials" }, statusCode: 400);
            }

            // A code can be exchanged exactly once — a re-used one is Twitch's "Invalid code".
            if (!_redeemedCodes.TryAdd(form["code"].ToString() ?? string.Empty, 0))
            {
                return Results.Json(new { status = 400, message = "Invalid code" }, statusCode: 400);
            }

            return Results.Json(new
            {
                access_token = "fake-token",
                refresh_token = "fake-refresh",
                expires_in = 3600,
                scope = new[] { "chat:read", "chat:edit" },
            });
        });
        _app.MapGet("/users", () => Results.Json(new
        {
            data = new[]
            {
                new { id = "123", login = "playerone", display_name = "Player One" },
            },
        }));
        _app.StartAsync().GetAwaiter().GetResult();
        BaseUrl = $"http://127.0.0.1:{port}";
    }

    public string BaseUrl { get; }

    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();
}
