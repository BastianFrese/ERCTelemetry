using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Share;

/// <summary>Fetches the track setups the logged-in Discord user may view from the website
/// (<c>GET /api/setups</c>, Bearer auth with the Discord access token from the app login).
/// Never throws — a missing token, unreachable server or invalid response maps to null.</summary>
public sealed class ErcSetupsClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Func<string?> _apiUrl;

    /// <summary>Feeds the client the settings API base URL live, so a settings save applies
    /// without a restart.</summary>
    public ErcSetupsClient(Func<string?> apiUrl)
    {
        _apiUrl = apiUrl;
    }

    /// <summary>Web defaults serialize/deserialize in camelCase — the exact property names
    /// the website's JSON uses.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Fetches the setups for the given Discord access token, optionally filtered
    /// by track and/or game year. Null on any failure (no token, unreachable, 401, invalid
    /// response).</summary>
    public async Task<ErcSetupsResponse?> GetSetupsAsync(
        string? accessToken, string? track = null, string? gameYear = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(track))
            {
                query.Add($"track={Uri.EscapeDataString(track.Trim())}");
            }
            if (!string.IsNullOrWhiteSpace(gameYear))
            {
                query.Add($"gameYear={Uri.EscapeDataString(gameYear.Trim())}");
            }
            var queryString = query.Count > 0 ? $"?{string.Join('&', query)}" : string.Empty;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ResolveSetupsUrl()}{queryString}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode is false)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<ErcSetupsResponse>(Json, ct);
            return result;
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

    /// <summary>The setups API hangs off the API root (<c>/api/setups</c>), not the telemetry
    /// base (<c>/api/telemetry</c>). Derives the root from the settings URL by trimming a
    /// trailing <c>/telemetry</c> segment; falls back to the built-in default.</summary>
    private string ResolveSetupsUrl()
    {
        var fromSettings = _apiUrl();
        var baseUrl = string.IsNullOrWhiteSpace(fromSettings) ? ErcConstants.DefaultApiBaseUrl : fromSettings!;
        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/telemetry", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/telemetry".Length];
        }

        return $"{baseUrl}/setups";
    }
}
