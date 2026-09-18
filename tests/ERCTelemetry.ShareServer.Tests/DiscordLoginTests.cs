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

/// <summary>Server-side Discord login handshake: start → callback (code exchange against a
/// stubbed Discord API) → poll status → delete. Verifies the double-redirect guard (the
/// same code is only exchanged once), the nonce binding on status/delete, the once-only
/// token delivery and the parallel-callback guard (a losing exchange cannot clobber a win).
/// Shares the "ShareServer" collection (see <see cref="ShareServerTests"/>).</summary>
[Collection("ShareServer")]
public sealed class DiscordLoginTests : IClassFixture<DiscordLoginFixture>
{
    private readonly DiscordLoginFixture _fixture;

    public DiscordLoginTests(DiscordLoginFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Start_returns_authorize_url_state_and_nonce()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/login/start");
        AddToken(request);
        request.Headers.Add(ShareConstants.InstallIdHeader, _installId);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var state = root.GetProperty("state").GetString();
        var nonce = root.GetProperty("nonce").GetString();
        var url = root.GetProperty("authorizeUrl").GetString();
        Assert.Equal(32, state?.Length); // Guid N
        Assert.Equal(32, nonce?.Length); // Guid N
        // S2: the start binds the attempt to the install and onboards it with a fresh
        // possession secret the app persists.
        Assert.Equal(_installId, root.GetProperty("installId").GetString());
        Assert.Equal(64, root.GetProperty("installSecret").GetString()?.Length); // 32-byte hex
        Assert.StartsWith("https://discord.com/api/v10/oauth2/authorize?", url);
        Assert.Contains("client_id=discord-client-123", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Ftelemetrie.erdi-erc.de%2Fdiscord%2Fcallback", url);
        Assert.Contains("scope=identify%20guilds%20guilds.members.read", url);
        Assert.Contains($"state={state}", url);
        // PKCE (M1): the start call generates a code challenge and puts it in the authorize
        // URL, so the code is bound to the verifier stored on the attempt.
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Matches("code_challenge=[A-Za-z0-9_-]{43}", url);
    }

    [Fact]
    public async Task Start_requires_token()
    {
        var response = await _fixture.Client.PostAsync("/discord/login/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Callback_with_error_marks_login_failed()
    {
        var (state, nonce) = await StartLoginAsync();

        var callback = await _fixture.Client.GetAsync($"/discord/callback?error=access_denied&state={state}");
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
            $"/discord/callback?error=provider_hiccup_%3Cscript%3Ex%3C%2Fscript%3E&state={state}");
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
            await (await _fixture.Client.GetAsync($"/discord/callback?code={NewCode()}&state={state}"))
                .Content.ReadAsStringAsync());
        await _fixture.Client.GetAsync($"/discord/callback?error=access_denied&state={state}");

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        var root = status.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("fake-token", root.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Callback_with_unknown_state_shows_error_page()
    {
        var response = await _fixture.Client.GetAsync("/discord/callback?code=abc&state=unknown");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Login abgelaufen", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_exchanges_code_and_returns_token()
    {
        var (state, nonce) = await StartLoginAsync();
        var code = NewCode();

        var callback = await _fixture.Client.GetAsync($"/discord/callback?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("Login erfolgreich", await callback.Content.ReadAsStringAsync());

        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        var root = status.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("fake-token", root.GetProperty("token").GetString());
        Assert.Equal("fake-refresh", root.GetProperty("refreshToken").GetString());
        Assert.Equal("123", root.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("playerone", root.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("Player One", root.GetProperty("user").GetProperty("globalName").GetString());
    }

    [Fact]
    public async Task Callback_double_redirect_exchanges_only_once()
    {
        var (state, nonce) = await StartLoginAsync();
        var code = NewCode();
        var before = _fixture.Discord.TokenRequests;

        var first = await _fixture.Client.GetAsync($"/discord/callback?code={code}&state={state}");
        Assert.Contains("Login erfolgreich", await first.Content.ReadAsStringAsync());
        var second = await _fixture.Client.GetAsync($"/discord/callback?code={code}&state={state}");
        Assert.Contains("Login erfolgreich", await second.Content.ReadAsStringAsync());

        // The stub is shared across the class, so assert the delta: exactly one exchange.
        Assert.Equal(before + 1, _fixture.Discord.TokenRequests);
        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("success", status.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Parallel_callbacks_do_not_clobber_a_success()
    {
        // Two genuinely parallel callbacks with the same code: both read the pending
        // attempt, both hit the stub — which redeems each code exactly once, so the loser
        // gets "invalid_grant" and lands in the catch. Its error must not overwrite the
        // winner's success (LOW 3).
        var (state, nonce) = await StartLoginAsync();
        var code = NewCode();
        var url = $"/discord/callback?code={code}&state={state}";

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
        var response = await _fixture.Client.GetAsync("/discord/login/status?state=abc");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_requires_the_login_nonce()
    {
        var (state, _) = await StartLoginAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/discord/login/status?state={state}");
        AddToken(request);
        AddInstallHeaders(request);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_rejects_a_wrong_nonce()
    {
        var (state, _) = await StartLoginAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/discord/login/status?state={state}");
        AddToken(request);
        AddInstallHeaders(request);
        request.Headers.Add(ShareConstants.LoginNonceHeader, "wrong-nonce");
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_delivers_the_token_only_once()
    {
        var (state, nonce) = await StartLoginAsync();
        await _fixture.Client.GetAsync($"/discord/callback?code={NewCode()}&state={state}");

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

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/discord/login/{state}");
        AddToken(delete);
        AddInstallHeaders(delete);
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

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/discord/login/{state}");
        AddToken(delete);
        AddInstallHeaders(delete);
        delete.Headers.Add(ShareConstants.LoginNonceHeader, "wrong-nonce");
        var deleteResponse = await _fixture.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode); // idempotent

        // The attempt is still alive — a correct-nonce poll answers pending.
        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("pending", status.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Delete_with_missing_install_pair_keeps_the_attempt()
    {
        var (state, nonce) = await StartLoginAsync();

        // Correct nonce but no install headers — the possession gate must refuse to remove.
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/discord/login/{state}");
        AddToken(delete);
        delete.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        var deleteResponse = await _fixture.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode); // idempotent

        // The attempt survives — the next full-pair poll still answers pending.
        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("pending", status.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Delete_with_wrong_install_id_keeps_the_attempt()
    {
        var (state, nonce) = await StartLoginAsync();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/discord/login/{state}");
        AddToken(delete);
        delete.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        delete.Headers.Add(ShareConstants.InstallIdHeader, "some-other-install");
        delete.Headers.Add(ShareConstants.InstallSecretHeader, _installSecret);
        var deleteResponse = await _fixture.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode); // idempotent

        // The attempt survives — a regression that drops the install check from the DELETE
        // (the endpoint that removes pending attempts) would surface here as an early 404.
        using var status = JsonDocument.Parse(await GetStatusJsonAsync(state, nonce));
        Assert.Equal("pending", status.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Start_rejects_an_empty_install_id()
    {
        // Empty/whitespace id is refused like a missing one — fail closed in ResolveInstall.
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/login/start");
        AddToken(request);
        request.Headers.Add(ShareConstants.InstallIdHeader, string.Empty);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Start_requires_an_install_id()
    {
        // S2: without the per-install identity the login cannot start — a script holding
        // only the public upload token is refused.
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/login/start");
        AddToken(request);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Start_with_known_install_and_correct_secret_succeeds()
    {
        var (_, _) = await StartLoginAsync(); // onboards _installId, captures _installSecret

        // A second start from the SAME install (id + issued secret) must succeed — and not
        // issue yet another secret (the app already holds it).
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/login/start");
        AddToken(request);
        AddInstallHeaders(request);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(_installId, doc.RootElement.GetProperty("installId").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("installSecret").ValueKind);
    }

    [Fact]
    public async Task Start_with_known_install_and_wrong_secret_is_rejected()
    {
        var (_, _) = await StartLoginAsync(); // onboards _installId

        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/login/start");
        AddToken(request);
        request.Headers.Add(ShareConstants.InstallIdHeader, _installId);
        request.Headers.Add(ShareConstants.InstallSecretHeader, "wrong-secret");
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_requires_the_install_pair()
    {
        var (state, nonce) = await StartLoginAsync();

        // Token + nonce, but no install headers — the poll must still be refused.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/discord/login/status?state={state}");
        AddToken(request);
        request.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_rejects_a_wrong_install_id()
    {
        var (state, nonce) = await StartLoginAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/discord/login/status?state={state}");
        AddToken(request);
        request.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        request.Headers.Add(ShareConstants.InstallIdHeader, "some-other-install");
        request.Headers.Add(ShareConstants.InstallSecretHeader, _installSecret);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string NewCode() => Guid.NewGuid().ToString("N");

    // S2 per-test install identity: every test onboards a fresh install, so the possession
    // registry (shared per fixture/class) never collides across tests.
    private readonly string _installId = Guid.NewGuid().ToString("N");
    private string _installSecret = null!; // filled by StartLoginAsync from the onboarded response

    private async Task<(string State, string Nonce)> StartLoginAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/login/start");
        AddToken(request);
        // First contact: send only the id — the server onboards it and answers with the
        // issued possession secret, which the test persists for the status/delete calls.
        request.Headers.Add(ShareConstants.InstallIdHeader, _installId);
        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _installSecret = doc.RootElement.GetProperty("installSecret").GetString()!;
        return (doc.RootElement.GetProperty("state").GetString()!,
                doc.RootElement.GetProperty("nonce").GetString()!);
    }

    private void AddInstallHeaders(HttpRequestMessage request)
    {
        request.Headers.Add(ShareConstants.InstallIdHeader, _installId);
        request.Headers.Add(ShareConstants.InstallSecretHeader, _installSecret);
    }

    private async Task<HttpResponseMessage> GetStatusAsync(string state, string nonce)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/discord/login/status?state={state}");
        AddToken(request);
        AddInstallHeaders(request);
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
        request.Headers.Add(ShareConstants.TokenHeader, DiscordLoginFixture.Token);
}

/// <summary>One WebApplicationFactory per test class with a stubbed Discord API: the
/// ShareServer's token/users endpoints point at a local stub that answers with a fake
/// token + user, counts token requests (for the double-redirect guard test) and rejects
/// already-redeemed codes (for the parallel-callback race).</summary>
public sealed class DiscordLoginFixture : IDisposable
{
    public const string Token = "test-token";
    public const string ClientSecret = "test-secret";

    private readonly DiscordStub _discord;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _root;

    public DiscordLoginFixture()
    {
        _discord = new DiscordStub();
        _root = Path.Combine(Path.GetTempPath(), $"erc-discord-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Share__Token", Token);
        Environment.SetEnvironmentVariable("Share__RootPath", _root);
        Environment.SetEnvironmentVariable("Share__DiscordTokenEndpoint", $"{_discord.BaseUrl}/token");
        Environment.SetEnvironmentVariable("Share__DiscordUsersEndpoint", $"{_discord.BaseUrl}/users");
        Environment.SetEnvironmentVariable("Share__DiscordClientSecret", ClientSecret);
        Environment.SetEnvironmentVariable("Share__DiscordClientId", "discord-client-123");
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public HttpClient Client { get; }

    public DiscordStub Discord => _discord;

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _discord.Dispose();
        Environment.SetEnvironmentVariable("Share__Token", null);
        Environment.SetEnvironmentVariable("Share__RootPath", null);
        Environment.SetEnvironmentVariable("Share__DiscordTokenEndpoint", null);
        Environment.SetEnvironmentVariable("Share__DiscordUsersEndpoint", null);
        Environment.SetEnvironmentVariable("Share__DiscordClientSecret", null);
        Environment.SetEnvironmentVariable("Share__DiscordClientId", null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Minimal stand-in for the Discord token + users API: answers with a fake token
/// and user, counts how often the token endpoint was hit, and redeems each authorization
/// code exactly once (a re-used code gets Discord's "invalid_grant" error).</summary>
public sealed class DiscordStub : IDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte> _redeemedCodes = new();
    private int _tokenRequests;

    public DiscordStub()
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
            // Discord requires the client secret in the exchange — reject the request if
            // the server did not send it, so the tests prove the secret is forwarded.
            var form = await ctx.Request.ReadFormAsync();
            if (form["client_secret"] != DiscordLoginFixture.ClientSecret)
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: 400);
            }

            // PKCE is part of the exchange now — a code sent without the verifier is
            // rejected, so the tests prove the stored verifier is forwarded (M1). Only its
            // presence is checked (the challenge was verified out-of-band against the
            // authorize URL in the Core tests / at Discord).
            if (string.IsNullOrEmpty(form["code_verifier"].ToString()))
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            // A code can be exchanged exactly once — a re-used one is Discord's "invalid_grant".
            if (!_redeemedCodes.TryAdd(form["code"].ToString() ?? string.Empty, 0))
            {
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            }

            return Results.Json(new
            {
                access_token = "fake-token",
                refresh_token = "fake-refresh",
                expires_in = 3600,
                scope = "identify guilds guilds.members.read",
            });
        });
        _app.MapGet("/users", () => Results.Json(new
        {
            id = "123",
            username = "playerone",
            global_name = "Player One",
        }));
        _app.StartAsync().GetAwaiter().GetResult();
        BaseUrl = $"http://127.0.0.1:{port}";
    }

    public string BaseUrl { get; }

    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();
}
