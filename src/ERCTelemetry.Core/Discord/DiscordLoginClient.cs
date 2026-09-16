using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.Core.Discord;

/// <summary>Result of <see cref="DiscordLoginClient.StartAsync"/>: the server-side login
/// attempt's state (echoed back by Discord in the callback), the nonce the app must send
/// (X-Login-Nonce) on every status poll and delete, and the browser URL the app opens so
/// the user can authorize.</summary>
public sealed record DiscordLoginStart(string State, string Nonce, string AuthorizeUrl);

/// <summary>Polled result of a server-side Discord login attempt. <see cref="Status"/> is
/// <c>"pending"</c> while the user has not finished authorizing, <c>"success"</c> with the
/// token + user, <c>"error"</c> with a message, or <c>"notfound"</c> when the server no
/// longer knows the state (expired or never started).</summary>
public sealed record DiscordLoginStatus(
    string Status,
    string? Token,
    string? RefreshToken,
    DiscordUser? User,
    string? Error);

/// <summary>App-side client for the server-side Discord login handshake. The ShareServer
/// owns the OAuth flow (code exchange with the client secret, user lookup) and stores the
/// result per state; this client starts the attempt, polls for the result and deletes it
/// when done. No localhost callback port involved. Headless-testable via an injected
/// <see cref="HttpMessageHandler"/>.</summary>
public sealed class DiscordLoginClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string?> _baseUrl;
    private readonly Func<string?>? _token;
    private readonly ShareTokenResolver? _tokenResolver;

    /// <summary>Feeds the client the settings base URL + share token (null/empty →
    /// built-in default / not configured).</summary>
    public DiscordLoginClient(Func<string?> baseUrl, Func<string?> token, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = baseUrl;
        _token = token;
    }

    /// <summary>Feeds the client the settings base URL + an auto-resolving token so
    /// Enduser need no manual setup (see <see cref="ShareTokenResolver"/>).</summary>
    public DiscordLoginClient(Func<string?> baseUrl, ShareTokenResolver tokenResolver, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = baseUrl;
        _tokenResolver = tokenResolver;
    }

    /// <summary>Starts a login attempt on the server: it generates the state and answers
    /// with the authorize URL the app opens in the browser.</summary>
    public async Task<DiscordLoginStart> StartAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ResolveBaseUrl()}/discord/login/start");
        await AddTokenAsync(request, ct).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode is false)
        {
            throw new InvalidOperationException(
                $"Discord-Login konnte nicht gestartet werden (Server {response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
        var url = root.TryGetProperty("authorizeUrl", out var u) ? u.GetString() : null;
        var nonce = root.TryGetProperty("nonce", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(nonce))
        {
            throw new InvalidOperationException("Server lieferte keine gültige Login-URL.");
        }

        return new DiscordLoginStart(state, nonce, url);
    }

    /// <summary>Polls the server for the login result. A 404 (state unknown — expired,
    /// never started or already delivered once) maps to <see cref="DiscordLoginStatus.Status"/>
    /// <c>"notfound"</c>. The nonce from <see cref="StartAsync"/> binds the call to the app
    /// session that started the login.</summary>
    public async Task<DiscordLoginStatus> GetStatusAsync(string state, string nonce, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{ResolveBaseUrl()}/discord/login/status?state={Uri.EscapeDataString(state)}");
        await AddTokenAsync(request, ct).ConfigureAwait(false);
        request.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new DiscordLoginStatus("notfound", null, null, null, null);
        }

        if (response.IsSuccessStatusCode is false)
        {
            throw new InvalidOperationException(
                $"Discord-Login-Status nicht abrufbar (Server {response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
        var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
        var refresh = root.TryGetProperty("refreshToken", out var r) ? r.GetString() : null;
        var error = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        DiscordUser? user = null;
        if (root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            user = new DiscordUser(
                u.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                u.TryGetProperty("username", out var un) ? un.GetString() ?? string.Empty : string.Empty,
                u.TryGetProperty("globalName", out var gn) ? gn.GetString() : null);
        }

        return new DiscordLoginStatus(status ?? "error", token, refresh, user, error);
    }

    /// <summary>Removes the login attempt from the server (the token is single-use; the
    /// app deletes it once it has the result). Best-effort — a failure is ignored.</summary>
    public async Task DeleteAsync(string state, string nonce, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Delete, $"{ResolveBaseUrl()}/discord/login/{Uri.EscapeDataString(state)}");
            await AddTokenAsync(request, ct).ConfigureAwait(false);
            request.Headers.Add(ShareConstants.LoginNonceHeader, nonce);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Best-effort cleanup — the server's expiry removes leftovers anyway.
        }
    }

    private async Task AddTokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = _tokenResolver is not null
            ? await _tokenResolver.GetTokenAsync(ct).ConfigureAwait(false)
            : _token?.Invoke();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Kein Share-Token konfiguriert — in den Einstellungen unter „Session teilen“ eintragen.");
        }

        request.Headers.Add(ShareConstants.TokenHeader, token);
    }

    /// <summary>Settings URL when present, else the built-in default; always without a
    /// trailing slash so appended paths stay well-formed.</summary>
    private string ResolveBaseUrl()
    {
        var fromSettings = _baseUrl();
        var baseUrl = string.IsNullOrWhiteSpace(fromSettings) ? ShareConstants.DefaultBaseUrl : fromSettings!;
        return baseUrl.TrimEnd('/');
    }

    /// <summary>Releases the internal HttpClient. The App hosts create one client per login
    /// attempt — disposing it prevents a socket-handler leak across repeated logins.</summary>
    public void Dispose() => _http.Dispose();
}
