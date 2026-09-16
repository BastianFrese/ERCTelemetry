using System.Net.Http;
using System.Text.Json;

namespace ERCTelemetry.Core.TwitchChat;

/// <summary>An OAuth token from the Twitch token endpoint.</summary>
public sealed record TwitchToken(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    IReadOnlyList<string> Scopes);

/// <summary>The Twitch user an access token belongs to (from the Helix /users endpoint).</summary>
public sealed record TwitchUser(string Id, string Login, string DisplayName);

/// <summary>Twitch OAuth helpers: builds the authorize URL the streamer opens in the
/// browser and exchanges the returned code for a token. The token exchange and user
/// lookup are thin HTTP wrappers — the URL building is the testable core.</summary>
public static class TwitchOAuth
{
    public const string AuthorizeEndpoint = "https://id.twitch.tv/oauth2/authorize";
    public const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";
    public const string UsersEndpoint = "https://api.twitch.tv/helix/users";

    /// <summary>Twitch app client ID (dev.twitch.tv). Public — it only identifies the app
    /// to Twitch. The app must have <see cref="RedirectUri"/> registered as its redirect
    /// URI; the client secret lives on the ShareServer (config), never in the app.</summary>
    public const string ClientId = "8cekqatgkipstivsw63q2b44hati81";

    /// <summary>Public HTTPS redirect URI registered for the Twitch app (dev.twitch.tv).
    /// Twitch requires HTTPS; the ShareServer at this address completes the OAuth flow
    /// (exchanges the code, stores the result) and the app polls for it.</summary>
    public const string RedirectUri = "https://telemetrie.erdi-erc.de/twitch/callback";

    /// <summary>Scopes the chat bot needs: read chat messages and send replies.</summary>
    public static readonly string[] ChatScopes = ["chat:read", "chat:edit"];

    /// <summary>Builds the browser URL for Twitch's authorization-code flow. The
    /// <paramref name="state"/> is echoed back by Twitch in the callback; the server
    /// rejects callbacks whose state does not match the current attempt, so a stale code
    /// from an old browser tab (already redeemed) can never be mistaken for a fresh one.
    /// Twitch does not support PKCE — the client secret is sent at exchange time.</summary>
    public static string BuildAuthorizeUrl(
        string clientId, string redirectUri, IReadOnlyList<string> scopes, string state)
    {
        var query = new List<string>
        {
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            "response_type=code",
            $"scope={Uri.EscapeDataString(string.Join(' ', scopes))}",
            $"state={Uri.EscapeDataString(state)}",
        };

        return $"{AuthorizeEndpoint}?{string.Join('&', query)}";
    }

    /// <summary>Exchanges the authorization code for a token. Twitch requires the client
    /// secret here (it does not support PKCE), so the caller — the ShareServer — supplies
    /// it from its config. Throws on a non-success response (bad code, revoked app, …)
    /// with the server's message in the exception. <paramref name="tokenEndpoint"/> and
    /// <paramref name="handler"/> exist for tests (stub endpoint / canned response).</summary>
    public static async Task<TwitchToken> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri,
        string? tokenEndpoint = null, HttpMessageHandler? handler = null,
        CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        });

        var response = await http.PostAsync(tokenEndpoint ?? TokenEndpoint, form, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Twitch token exchange failed: {response.StatusCode} {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new TwitchToken(
            root.GetProperty("access_token").GetString() ?? string.Empty,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 0,
            root.TryGetProperty("scope", out var sc)
                ? sc.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                : []);
    }

    /// <summary>Resolves the user behind an access token (needed for the IRC NICK).
    /// <paramref name="usersEndpoint"/> and <paramref name="handler"/> exist for tests.</summary>
    public static async Task<TwitchUser> GetUserAsync(
        string accessToken, string clientId, string? usersEndpoint = null,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, usersEndpoint ?? UsersEndpoint);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        request.Headers.Add("Client-Id", clientId);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Twitch user lookup failed: {response.StatusCode} {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Twitch user lookup returned no user.");
        }

        var user = data[0];
        return new TwitchUser(
            user.GetProperty("id").GetString() ?? string.Empty,
            user.GetProperty("login").GetString() ?? string.Empty,
            user.GetProperty("display_name").GetString() ?? string.Empty);
    }
}
