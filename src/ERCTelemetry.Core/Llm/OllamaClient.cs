using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ERCTelemetry.Core.Llm;

/// <summary>Thin HTTP client for Ollama's chat API (cloud at https://ollama.com or a local
/// server at http://localhost:11434). One request, no streaming: sends a system + user
/// message and returns the assistant text. Returns null on ANY failure (network, auth,
/// malformed response) so callers fall back to Layer 1 — the LLM is optional, never
/// mandatory. Headless-testable via an injected <see cref="HttpMessageHandler"/>.</summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _model;

    public OllamaClient(string baseUrl, string? apiKey, string model, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Sends one chat completion and returns the assistant text, or null when the
    /// request failed (non-2xx, malformed body, network error) or the answer was empty.</summary>
    public async Task<string?> CompleteAsync(string system, string user, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
                stream = false,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                var text = content.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate, don't swallow the cancellation.
            throw;
        }
        catch (Exception)
        {
            // Any other failure (network error, the 30-s HttpClient timeout surfacing as
            // TaskCanceledException with the token NOT cancelled, auth, malformed body).
            return null;
        }
    }

    /// <summary>Lists the models the account/server can run (GET /api/tags), or null on
    /// failure. The Settings tab uses this after a successful connection test to fill the
    /// model dropdown (e.g. gemma, deepseek) instead of asking the user to type a name.
    /// Prefers the <c>model</c> field (the <c>name</c> field is deprecated upstream).</summary>
    public async Task<IReadOnlyList<string>?> GetModelsAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tags");
            if (!string.IsNullOrEmpty(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("models", out var models))
            {
                return null;
            }

            var names = new List<string>();
            foreach (var entry in models.EnumerateArray())
            {
                var name = entry.TryGetProperty("model", out var model)
                    ? model.GetString()
                    : entry.TryGetProperty("name", out var legacy) ? legacy.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names.Count > 0 ? names : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate, don't swallow the cancellation.
            throw;
        }
        catch (Exception)
        {
            // Any other failure (network error, the 30-s HttpClient timeout surfacing as
            // TaskCanceledException with the token NOT cancelled, auth, malformed body).
            return null;
        }
    }
}
