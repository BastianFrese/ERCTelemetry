using System.Text.Json;

namespace ERCTelemetry.Core.Share;

/// <summary>Resolves the share-server upload token so Enduser can share a session
/// without configuring anything. Precedence: an explicit settings token always wins
/// (operator/manual rotation), otherwise the current token is fetched from the server's
/// public GET /api/config endpoint and cached for a short TTL, falling back to
/// <see cref="ShareConstants.DefaultToken"/> when the server is unreachable or answers
/// without a token. Returns null only when every source is empty — the clients then
/// report the „Kein Share-Token“ error as before. The cache only guards the fetched
/// token (a concurrent duplicate fetch is harmless — both callers get the same value).</summary>
public sealed class ShareTokenResolver
{
    private const int FetchTimeoutSeconds = 10;

    private readonly Func<string?> _baseUrl;
    private readonly Func<string?> _settingsToken;
    private readonly HttpClient _http;
    private readonly TimeSpan _cacheTtl;
    private readonly string _defaultToken;

    private readonly object _gate = new();
    private string? _cachedToken;
    private DateTimeOffset _cachedAt;

    /// <summary>Feeds the resolver the settings base URL + token (null/empty → built-in
    /// default / auto-fetch). The injected <paramref name="handler"/> lets headless tests
    /// stub the /api/config call; production uses a real client with a short timeout so a
    /// dead server fails fast into the fallback token instead of blocking the share for
    /// HttpClient's default 100 s.</summary>
    public ShareTokenResolver(
        Func<string?> baseUrl,
        Func<string?> settingsToken,
        HttpMessageHandler? handler = null,
        TimeSpan? cacheTtl = null,
        string defaultToken = ShareConstants.DefaultToken)
    {
        _baseUrl = baseUrl;
        _settingsToken = settingsToken;
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds),
        };
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
        _defaultToken = defaultToken;
    }

    /// <summary>Resolved token to send as <see cref="ShareConstants.TokenHeader"/>: the
    /// settings override when present, else the cached / freshly fetched server token,
    /// else the built-in default. Null when every source is empty.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var fromSettings = _settingsToken();
        if (!string.IsNullOrWhiteSpace(fromSettings))
        {
            return fromSettings.Trim();
        }

        lock (_gate)
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow - _cachedAt < _cacheTtl)
            {
                return _cachedToken;
            }
        }

        var fetched = await TryFetchAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fetched))
        {
            lock (_gate)
            {
                // Same-value refreshes are fine (and needed after a TTL expiry so the cache
                // keeps serving). But a concurrent caller may have stored a fresher token
                // since — a rotation can land between the two fetches — so never overwrite
                // it with an older value. In that rare window whichever caller stored first
                // wins and the stale value self-heals on the next expiry.
                var fresh = _cachedToken is not null && DateTimeOffset.UtcNow - _cachedAt < _cacheTtl;
                if (!fresh || string.Equals(_cachedToken, fetched, StringComparison.Ordinal))
                {
                    _cachedToken = fetched;
                    _cachedAt = DateTimeOffset.UtcNow;
                }
            }

            return fetched;
        }

        // The refetch failed (server down, timeout, malformed answer) — the last-known-good
        // token, even if its TTL expired, is likelier to still be valid than the static
        // default, so keep serving it through a transient outage instead of degrading.
        lock (_gate)
        {
            if (_cachedToken is not null)
            {
                return _cachedToken;
            }
        }

        return string.IsNullOrWhiteSpace(_defaultToken) ? null : _defaultToken;
    }

    private async Task<string?> TryFetchAsync(CancellationToken ct)
    {
        try
        {
            var fromSettings = _baseUrl();
            var url = string.IsNullOrWhiteSpace(fromSettings) ? ShareConstants.DefaultBaseUrl : fromSettings!;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/api/config");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode is false)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("shareToken", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) &&
            !ct.IsCancellationRequested)
        {
            // TaskCanceledException covers both the 10 s HttpClient timeout (intended
            // fallback) and — depending on the handler — a caller cancellation. The latter
            // must propagate, not silently fall back to the default token.
            return null;
        }
    }
}
