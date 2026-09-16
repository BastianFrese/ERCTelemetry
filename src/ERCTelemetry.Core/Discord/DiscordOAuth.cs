using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ERCTelemetry.Core.Discord;

/// <summary>An OAuth token from the Discord token endpoint.</summary>
public sealed record DiscordToken(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    IReadOnlyList<string> Scopes);

/// <summary>The Discord user an access token belongs to (from /users/@me).</summary>
public sealed record DiscordUser(string Id, string Username, string? GlobalName);

/// <summary>Discord OAuth helpers: builds the authorize URL the user opens in the
/// browser and exchanges the returned code for a token. The token exchange and user
/// lookup are thin HTTP wrappers — the URL building is the testable core. The client
/// id/secret are supplied by the caller (the ShareServer config), never hardcoded.</summary>
public static class DiscordOAuth
{
    public const string AuthorizeEndpoint = "https://discord.com/api/v10/oauth2/authorize";
    public const string TokenEndpoint = "https://discord.com/api/v10/oauth2/token";
    public const string UsersEndpoint = "https://discord.com/api/v10/users/@me";

    /// <summary>Public HTTPS redirect URI registered for the Discord app
    /// (discord.com/developers). The ShareServer at this address completes the OAuth
    /// flow (exchanges the code, stores the result) and the app polls for it. The
    /// ShareServer config can override it (tests / a different host).</summary>
    public const string RedirectUri = "https://telemetrie.erdi-erc.de/discord/callback";

    /// <summary>Scopes the app needs: <c>identify</c> (who the user is) plus
    /// <c>guilds</c> and <c>guilds.members.read</c> — the Erdi-ERC website resolves
    /// setup access via the guild member endpoint, which requires those scopes.</summary>
    public static readonly string[] Scopes = ["identify", "guilds", "guilds.members.read"];

    /// <summary>Builds the browser URL for Discord's authorization-code flow. The
    /// <paramref name="state"/> is echoed back by Discord in the callback; the server
    /// rejects callbacks whose state does not match the current attempt, so a stale code
    /// from an old browser tab (already redeemed) can never be mistaken for a fresh one.
    /// When <paramref name="codeChallenge"/> is given (the base64url S256 challenge of a
    /// freshly generated code verifier), PKCE (RFC 7636) is added — Discord then binds the
    /// authorization code to the verifier, so a code intercepted between the callback and
    /// the token exchange cannot be redeemed by someone holding neither the verifier nor the
    /// client secret.</summary>
    public static string BuildAuthorizeUrl(
        string clientId, string redirectUri, IReadOnlyList<string> scopes, string state,
        string? codeChallenge = null)
    {
        var query = new List<string>
        {
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            "response_type=code",
            $"scope={Uri.EscapeDataString(string.Join(' ', scopes))}",
            $"state={Uri.EscapeDataString(state)}",
        };

        if (codeChallenge is not null)
        {
            query.Add($"code_challenge={Uri.EscapeDataString(codeChallenge)}");
            query.Add("code_challenge_method=S256");
        }

        return $"{AuthorizeEndpoint}?{string.Join('&', query)}";
    }

    /// <summary>Exchanges the authorization code for a token. Discord requires the client
    /// secret here, so the caller — the ShareServer — supplies it from its config. When
    /// <paramref name="codeVerifier"/> is given (the verifier stored when the login
    /// started), it is sent alongside so Discord can verify the PKCE challenge. Throws
    /// on a non-success response (bad code, revoked app, …) with the server's message in
    /// the exception. <paramref name="tokenEndpoint"/> and <paramref name="handler"/>
    /// exist for tests (stub endpoint / canned response).</summary>
    public static async Task<DiscordToken> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri,
        string? codeVerifier = null, string? tokenEndpoint = null,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        if (codeVerifier is not null)
        {
            form["code_verifier"] = codeVerifier;
        }

        var response = await http.PostAsync(tokenEndpoint ?? TokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Discord token exchange failed: {response.StatusCode} {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new DiscordToken(
            root.GetProperty("access_token").GetString() ?? string.Empty,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 0,
            root.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String
                ? sc.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : []);
    }

    /// <summary>Generates a fresh PKCE code verifier as mandated by RFC 7636: 43-127 chars
    /// from the unreserved alphabet — 32 random bytes base64url-encoded gives exactly 43.
    /// One per login start; the server keeps it on the attempt and sends it with the code
    /// exchange.</summary>
    public static string NewCodeVerifier() => ToBase64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 code challenge of a <paramref name="verifier"/>: base64url of its
    /// SHA-256 hash, as Discord expects when <c>code_challenge_method=S256</c>.</summary>
    public static string CodeChallenge(string verifier) =>
        ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    /// <summary>Base64url without padding — the RFC 7636 alphabet (A-Za-z0-9-_).</summary>
    private static string ToBase64Url(byte[] bytes) => Convert
        .ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    /// <summary>Resolves the user behind an access token (needed to show who logged in
    /// and to cross-check the API key owner). <paramref name="usersEndpoint"/> and
    /// <paramref name="handler"/> exist for tests.</summary>
    public static async Task<DiscordUser> GetUserAsync(
        string accessToken, string? usersEndpoint = null,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, usersEndpoint ?? UsersEndpoint);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Discord user lookup failed: {response.StatusCode} {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new DiscordUser(
            root.GetProperty("id").GetString() ?? string.Empty,
            root.TryGetProperty("username", out var u) ? u.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("global_name", out var g) ? g.GetString() : null);
    }
}
