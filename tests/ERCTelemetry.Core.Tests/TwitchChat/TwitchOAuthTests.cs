using System.Net;
using ERCTelemetry.Core.Tests.Llm;
using ERCTelemetry.Core.TwitchChat;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class TwitchOAuthTests
{
    [Fact]
    public void Authorize_url_contains_client_id_redirect_scopes_and_state()
    {
        var url = TwitchOAuth.BuildAuthorizeUrl(
            "abc123", "http://localhost:8091/callback", TwitchOAuth.ChatScopes, "state123");

        Assert.StartsWith(TwitchOAuth.AuthorizeEndpoint + "?", url);
        Assert.Contains("client_id=abc123", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A8091%2Fcallback", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=chat%3Aread%20chat%3Aedit", url);
        Assert.Contains("state=state123", url);
    }

    [Fact]
    public void Authorize_url_escapes_special_characters()
    {
        var url = TwitchOAuth.BuildAuthorizeUrl(
            "a b&c", "http://localhost/x?y=1", ["chat:read"], "state a&b");

        Assert.Contains("client_id=a%20b%26c", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%2Fx%3Fy%3D1", url);
        Assert.Contains("state=state%20a%26b", url);
    }

    [Fact]
    public async Task ExchangeCodeAsync_posts_to_custom_endpoint_and_parses_token()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"tok","refresh_token":"ref","expires_in":3600,"scope":["chat:read","chat:edit"]}"""));
        const string endpoint = "http://stub/token";

        var token = await TwitchOAuth.ExchangeCodeAsync(
            "client", "secret123", "code123", "https://telemetrie.erdi-erc.de/twitch/callback",
            tokenEndpoint: endpoint, handler: handler);

        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal(endpoint, handler.LastRequest?.RequestUri?.ToString());
        Assert.Contains("client_id=client", handler.LastRequestBody);
        Assert.Contains("client_secret=secret123", handler.LastRequestBody);
        Assert.Contains("code=code123", handler.LastRequestBody);
        Assert.Equal("tok", token.AccessToken);
        Assert.Equal("ref", token.RefreshToken);
        Assert.Equal(3600, token.ExpiresIn);
        Assert.Equal(["chat:read", "chat:edit"], token.Scopes);
    }

    [Fact]
    public async Task ExchangeCodeAsync_throws_on_non_success()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.BadRequest));
        const string endpoint = "http://stub/token";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TwitchOAuth.ExchangeCodeAsync("client", "secret", "code", "https://x/callback",
                tokenEndpoint: endpoint, handler: handler));

        Assert.Contains("Twitch token exchange failed", ex.Message);
    }

    [Fact]
    public async Task GetUserAsync_gets_custom_endpoint_and_parses_user()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"data":[{"id":"42","login":"playerone","display_name":"Player One"}]}"""));
        const string endpoint = "http://stub/users";

        var user = await TwitchOAuth.GetUserAsync("tok", "client", usersEndpoint: endpoint, handler: handler);

        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal(endpoint, handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer tok", handler.LastRequest?.Headers.Authorization?.ToString());
        Assert.Equal("client", handler.LastRequest?.Headers.GetValues("Client-Id").Single());
        Assert.Equal("42", user.Id);
        Assert.Equal("playerone", user.Login);
        Assert.Equal("Player One", user.DisplayName);
    }
}
