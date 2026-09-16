using System.Net;
using ERCTelemetry.Core.Discord;
using ERCTelemetry.Core.Tests.Llm;
using Xunit;

namespace ERCTelemetry.Core.Tests.Discord;

public sealed class DiscordOAuthTests
{
    [Fact]
    public void Authorize_url_contains_client_id_redirect_scopes_and_state()
    {
        var url = DiscordOAuth.BuildAuthorizeUrl(
            "abc123", "https://telemetrie.erdi-erc.de/discord/callback", DiscordOAuth.Scopes, "state123");

        Assert.StartsWith(DiscordOAuth.AuthorizeEndpoint + "?", url);
        Assert.Contains("client_id=abc123", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Ftelemetrie.erdi-erc.de%2Fdiscord%2Fcallback", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=identify%20guilds%20guilds.members.read", url);
        Assert.Contains("state=state123", url);
    }

    [Fact]
    public void Authorize_url_escapes_special_characters()
    {
        var url = DiscordOAuth.BuildAuthorizeUrl(
            "a b&c", "http://localhost/x?y=1", ["identify"], "state a&b");

        Assert.Contains("client_id=a%20b%26c", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%2Fx%3Fy%3D1", url);
        Assert.Contains("state=state%20a%26b", url);
    }

    [Fact]
    public async Task ExchangeCodeAsync_posts_to_custom_endpoint_and_parses_token()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"tok","refresh_token":"ref","expires_in":3600,"scope":"identify guilds"}"""));
        const string endpoint = "http://stub/token";

        var token = await DiscordOAuth.ExchangeCodeAsync(
            "client", "secret123", "code123", "https://telemetrie.erdi-erc.de/discord/callback",
            tokenEndpoint: endpoint, handler: handler);

        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal(endpoint, handler.LastRequest?.RequestUri?.ToString());
        Assert.Contains("client_id=client", handler.LastRequestBody);
        Assert.Contains("client_secret=secret123", handler.LastRequestBody);
        Assert.Contains("code=code123", handler.LastRequestBody);
        Assert.Contains("grant_type=authorization_code", handler.LastRequestBody);
        Assert.Equal("tok", token.AccessToken);
        Assert.Equal("ref", token.RefreshToken);
        Assert.Equal(3600, token.ExpiresIn);
        Assert.Equal(["identify", "guilds"], token.Scopes);
    }

    [Fact]
    public async Task ExchangeCodeAsync_throws_on_non_success()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.BadRequest));
        const string endpoint = "http://stub/token";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DiscordOAuth.ExchangeCodeAsync("client", "secret", "code", "https://x/callback",
                tokenEndpoint: endpoint, handler: handler));

        Assert.Contains("Discord token exchange failed", ex.Message);
    }

    [Fact]
    public void NewCodeVerifier_is_43_chars_of_base64url_and_unique()
    {
        var verifier = DiscordOAuth.NewCodeVerifier();

        Assert.Equal(43, verifier.Length);
        Assert.All(verifier, c => Assert.True(
            c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'));
        // A reused verifier would defeat PKCE — two calls must differ.
        Assert.NotEqual(verifier, DiscordOAuth.NewCodeVerifier());
    }

    [Fact]
    public void CodeChallenge_matches_the_rfc_7636_test_vector()
    {
        // RFC 7636 Section B — the canonical verifier → S256 challenge pair.
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            DiscordOAuth.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Authorize_url_adds_pkce_parameters_only_when_a_challenge_is_given()
    {
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        var url = DiscordOAuth.BuildAuthorizeUrl(
            "client", "https://x/callback", ["identify"], "state", codeChallenge: challenge);

        Assert.Contains($"code_challenge={challenge}", url);
        Assert.Contains("code_challenge_method=S256", url);

        // Without a challenge the URL stays PKCE-free (the pre-PKCE shape).
        Assert.DoesNotContain(
            "code_challenge",
            DiscordOAuth.BuildAuthorizeUrl("client", "https://x/callback", ["identify"], "state"));
    }

    [Fact]
    public async Task ExchangeCodeAsync_forwards_the_code_verifier_when_given()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"tok","refresh_token":"ref","expires_in":3600,"scope":"identify"}"""));
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        await DiscordOAuth.ExchangeCodeAsync(
            "client", "secret", "code", "https://x/callback",
            codeVerifier: verifier, tokenEndpoint: "http://stub/token", handler: handler);

        Assert.Contains($"code_verifier={verifier}", handler.LastRequestBody);
    }

    [Fact]
    public async Task GetUserAsync_gets_custom_endpoint_and_parses_user()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"id":"42","username":"playerone","global_name":"Player One"}"""));
        const string endpoint = "http://stub/users";

        var user = await DiscordOAuth.GetUserAsync("tok", usersEndpoint: endpoint, handler: handler);

        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal(endpoint, handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer tok", handler.LastRequest?.Headers.Authorization?.ToString());
        Assert.Equal("42", user.Id);
        Assert.Equal("playerone", user.Username);
        Assert.Equal("Player One", user.GlobalName);
    }

    [Fact]
    public async Task GetUserAsync_handles_missing_global_name()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"id":"42","username":"playerone"}"""));
        const string endpoint = "http://stub/users";

        var user = await DiscordOAuth.GetUserAsync("tok", usersEndpoint: endpoint, handler: handler);

        Assert.Equal("playerone", user.Username);
        Assert.Null(user.GlobalName);
    }
}
