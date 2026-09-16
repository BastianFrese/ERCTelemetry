using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Share;

/// <summary>Sends a finished race result to the ERC admin-review inbox
/// (<c>POST /api/telemetry/race</c>) and lists the leagues the driver may send for
/// (<c>GET /api/telemetry/leagues</c>). Never throws — all network failures map to a
/// failed <see cref="SendResult"/> / an empty league list.</summary>
public sealed class ErcRaceSender
{
    /// <summary>Result of <see cref="SendAsync"/>: <see cref="PendingId"/> is the ERC
    /// draft id on success (the admin reviews it before it becomes a final race result).</summary>
    public sealed record SendResult(bool Success, int? PendingId, string? Error);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Func<string?> _apiUrl;
    private readonly Func<string?> _apiKey;

    /// <summary>Feeds the service the settings API base URL + personal key live, so a
    /// settings save applies without a restart.</summary>
    public ErcRaceSender(Func<string?> apiUrl, Func<string?> apiKey)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
    }

    /// <summary>Web defaults serialize the payload in camelCase — the exact property
    /// names the ERC-side parser reads.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Settings URL when present, else the built-in default; always without a
    /// trailing slash so appended paths stay well-formed.</summary>
    private string ResolveApiUrl()
    {
        var fromSettings = _apiUrl();
        var baseUrl = string.IsNullOrWhiteSpace(fromSettings) ? ErcConstants.DefaultApiBaseUrl : fromSettings!;
        return baseUrl.TrimEnd('/');
    }

    /// <summary>Leagues the key owner is an active member of; empty on any failure
    /// (no key, unreachable, invalid response).</summary>
    public async Task<IReadOnlyList<ErcLeague>> GetLeaguesAsync(CancellationToken ct = default)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ResolveApiUrl()}/leagues");
            request.Headers.Add(ErcConstants.ApiKeyHeader, key);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode is false)
            {
                return [];
            }

            var leagues = await response.Content.ReadFromJsonAsync<List<ErcLeague>>(Json, ct);
            return leagues ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Owner of the configured API key, from <c>GET /api/telemetry/me</c>; null on
    /// any failure (no key, unreachable, invalid response). The app compares the returned
    /// <see cref="ErcKeyOwner.DiscordId"/> with the logged-in Discord user to verify the key
    /// belongs to the person using it.</summary>
    public async Task<ErcKeyOwner?> GetOwnerAsync(CancellationToken ct = default)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ResolveApiUrl()}/me");
            request.Headers.Add(ErcConstants.ApiKeyHeader, key);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode is false)
            {
                return null;
            }

            var owner = await response.Content.ReadFromJsonAsync<ErcKeyOwner>(Json, ct);
            return owner;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Sends the result; on success the ERC side created a pending draft for its
    /// admin review inbox. Idempotent server-side: an identical re-send returns the same
    /// id.</summary>
    public async Task<SendResult> SendAsync(ErcRaceResultPayload payload, CancellationToken ct = default)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new SendResult(false, null,
                "Kein ERC-API-Key konfiguriert — in den Einstellungen unter „ERC-Ergebnis“ eintragen.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ResolveApiUrl()}/race")
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Add(ErcConstants.ApiKeyHeader, key);
            using var response = await _http.SendAsync(request, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            int? pendingId = null;
            string? error = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    // The id must be a JSON number ("id": 42). A string/odd shape is treated
                    // as "no id" → the status-error path below, never a wrong number.
                    if (doc.RootElement.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.Number
                        && id.TryGetInt32(out var idValue))
                    {
                        pendingId = idValue;
                    }

                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        error = err.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON body below is reported as a plain server-status error.
                }
            }

            if (response.IsSuccessStatusCode && pendingId is not null)
            {
                return new SendResult(true, pendingId, null);
            }

            return new SendResult(false, null,
                error ?? $"Server antwortete {response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SendResult(false, null, $"Senden fehlgeschlagen: {ex.Message}");
        }
    }
}
