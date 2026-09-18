using System.Net;
using ERCTelemetry.Core.Share;
using ERCTelemetry.Core.Tests.Llm;
using ERCTelemetry.Core.TwitchChat;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class TwitchLoginClientTests
{
    private const string BaseUrl = "https://telemetrie.erdi-erc.de";
    private const string Token = "test-token";

    private static TwitchLoginClient Client(FakeHttpHandler handler) =>
        new(() => BaseUrl, () => Token, handler, () => "install-123", () => "secret-456");

    [Fact]
    public async Task StartAsync_posts_to_login_start_with_token_header()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"state":"abc123","nonce":"n123","authorizeUrl":"https://id.twitch.tv/oauth2/authorize?client_id=x"}"""));
        var client = Client(handler);

        var start = await client.StartAsync();

        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal($"{BaseUrl}/twitch/login/start", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(Token, handler.LastRequest?.Headers.GetValues(ShareConstants.TokenHeader).Single());
        Assert.Equal("abc123", start.State);
        Assert.Equal("n123", start.Nonce);
        Assert.Equal("https://id.twitch.tv/oauth2/authorize?client_id=x", start.AuthorizeUrl);
    }

    [Fact]
    public async Task StartAsync_throws_when_the_server_sends_no_nonce()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"state":"abc123","authorizeUrl":"https://id.twitch.tv/oauth2/authorize?client_id=x"}"""));
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());

        Assert.Contains("keine gültige Login-URL", ex.Message);
    }

    [Fact]
    public async Task StartAsync_throws_without_token()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));
        var client = new TwitchLoginClient(() => BaseUrl, () => null, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());

        Assert.Contains("Kein Share-Token", ex.Message);
    }

    [Fact]
    public async Task StartAsync_throws_on_server_error()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.InternalServerError));
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());

        Assert.Contains("konnte nicht gestartet werden", ex.Message);
    }

    [Fact]
    public async Task StartAsync_forbidden_carries_the_status_for_the_host_self_heal()
    {
        // The S2 host's binding self-heal matches on this exact text: a 403 from /start
        // means the server still holds a secret for our install id that the app no longer
        // has, so the host drops the dead identity and retries (see TwitchChatHost).
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.Forbidden));
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());

        Assert.Contains("Forbidden", ex.Message);
    }

    [Fact]
    public async Task GetStatusAsync_parses_pending()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"status":"pending"}"""));
        var client = Client(handler);

        var status = await client.GetStatusAsync("abc123", "n123");

        Assert.Equal("pending", status.Status);
        Assert.Null(status.Token);
        Assert.Null(status.User);
    }

    [Fact]
    public async Task GetStatusAsync_parses_success()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"status":"success","token":"tok","refreshToken":"ref","user":{"id":"1","login":"playerone","displayName":"Player One"}}"""));
        var client = Client(handler);

        var status = await client.GetStatusAsync("abc123", "n123");

        Assert.Equal("success", status.Status);
        Assert.Equal("tok", status.Token);
        Assert.Equal("ref", status.RefreshToken);
        Assert.NotNull(status.User);
        Assert.Equal("playerone", status.User!.Login);
        Assert.Equal("Player One", status.User.DisplayName);
    }

    [Fact]
    public async Task GetStatusAsync_parses_error()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"status":"error","message":"Invalid code"}"""));
        var client = Client(handler);

        var status = await client.GetStatusAsync("abc123", "n123");

        Assert.Equal("error", status.Status);
        Assert.Equal("Invalid code", status.Error);
    }

    [Fact]
    public async Task GetStatusAsync_returns_notfound_on_404()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.NotFound));
        var client = Client(handler);

        var status = await client.GetStatusAsync("abc123", "n123");

        Assert.Equal("notfound", status.Status);
    }

    [Fact]
    public async Task GetStatusAsync_sends_the_login_nonce_header()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"status":"pending"}"""));
        var client = Client(handler);

        await client.GetStatusAsync("abc123", "n123");

        Assert.Equal("n123", handler.LastRequest?.Headers.GetValues(ShareConstants.LoginNonceHeader).Single());
        Assert.Contains("state=abc123", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(Token, handler.LastRequest?.Headers.GetValues(ShareConstants.TokenHeader).Single());
    }

    [Fact]
    public async Task DeleteAsync_sends_delete_for_state_with_nonce_header()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = Client(handler);

        await client.DeleteAsync("abc123", "n123");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest?.Method);
        Assert.Equal($"{BaseUrl}/twitch/login/abc123", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(Token, handler.LastRequest?.Headers.GetValues(ShareConstants.TokenHeader).Single());
        Assert.Equal("n123", handler.LastRequest?.Headers.GetValues(ShareConstants.LoginNonceHeader).Single());
    }

    [Fact]
    public async Task DeleteAsync_ignores_server_errors()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.InternalServerError));
        var client = Client(handler);

        // Best-effort cleanup must not throw.
        await client.DeleteAsync("abc123", "n123");
    }

    [Fact]
    public async Task StartAsync_uses_the_auto_resolved_token_when_no_settings_token_is_set()
    {
        var configHandler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"shareToken":"auto-token"}"""));
        var resolver = new ShareTokenResolver(() => BaseUrl, () => null, configHandler);
        var loginHandler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"state":"abc123","nonce":"n123","authorizeUrl":"https://id.twitch.tv/oauth2/authorize?client_id=x"}"""));
        var client = new TwitchLoginClient(() => BaseUrl, resolver, loginHandler, () => "install-123", () => "secret-456");

        var start = await client.StartAsync();

        Assert.Equal("auto-token", loginHandler.LastRequest?.Headers.GetValues(ShareConstants.TokenHeader).Single());
        Assert.Equal("abc123", start.State);
        Assert.Equal("n123", start.Nonce);
    }

    [Fact]
    public async Task StartAsync_sends_the_install_headers_and_parses_the_response()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"state":"abc123","nonce":"n123","authorizeUrl":"https://id.twitch.tv/oauth2/authorize?client_id=x","installId":"install-123","installSecret":"secret-456"}"""));
        var client = Client(handler);

        var start = await client.StartAsync();

        // Both possession headers go out on the start…
        Assert.Equal("install-123", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallIdHeader).Single());
        Assert.Equal("secret-456", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallSecretHeader).Single());
        // …and the server's onboarding answer (id + issued secret to persist) is surfaced.
        Assert.Equal("install-123", start.InstallId);
        Assert.Equal("secret-456", start.InstallSecret);
    }

    [Fact]
    public async Task StartAsync_sends_only_the_id_before_the_secret_is_issued()
    {
        // First login of a brand-new installation: the app has an id but no possession secret
        // yet — the client must send the id alone and the server onboards it.
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"state":"abc123","nonce":"n123","authorizeUrl":"https://id.twitch.tv/oauth2/authorize?client_id=x","installId":"install-123","installSecret":"secret-456"}"""));
        var client = new TwitchLoginClient(() => BaseUrl, () => Token, handler, () => "install-123");

        var start = await client.StartAsync();

        Assert.Equal("install-123", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallIdHeader).Single());
        Assert.False(handler.LastRequest?.Headers.Contains(ShareConstants.InstallSecretHeader));
        // The freshly issued secret comes back in the start response for the app to persist.
        Assert.Equal("secret-456", start.InstallSecret);
    }

    [Fact]
    public async Task StartAsync_throws_without_an_install_id()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));
        var client = new TwitchLoginClient(() => BaseUrl, () => Token, handler); // no installId Func

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());

        Assert.Contains("Keine Install-Id", ex.Message);
    }

    [Fact]
    public async Task GetStatusAsync_sends_the_install_headers()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"status":"pending"}"""));
        var client = Client(handler);

        await client.GetStatusAsync("abc123", "n123");

        Assert.Equal("install-123", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallIdHeader).Single());
        Assert.Equal("secret-456", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallSecretHeader).Single());
    }

    [Fact]
    public async Task DeleteAsync_sends_the_install_headers()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = Client(handler);

        await client.DeleteAsync("abc123", "n123");

        Assert.Equal("install-123", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallIdHeader).Single());
        Assert.Equal("secret-456", handler.LastRequest?.Headers.GetValues(ShareConstants.InstallSecretHeader).Single());
    }
}
