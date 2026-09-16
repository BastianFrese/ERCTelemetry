using System.Net.Http;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Llm;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.App.Composition;

/// <summary>Owns the optional LLM layer: builds an <see cref="OllamaClient"/> from the
/// current settings (rebuilt when the key/base URL change) and exposes the Layer-2
/// providers plus a connection test. The model is LOCKED to a single cheap Ollama cloud
/// model (<see cref="LlmConstants.FixedModel"/>) so no caller — and no user — can ever
/// select an expensive one, and an API key is mandatory: without a key nothing loads,
/// nothing is tested and the layer is never considered configured. Every call falls back
/// to null on failure — Layer 1 keeps working without a key.</summary>
public sealed class LlmService
{
    private readonly AppSettingsService _settings;
    private readonly SocketsHttpHandler _handler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    private OllamaClient? _client;
    private string? _clientKey;
    private string? _clientBaseUrl;

    public LlmService(AppSettingsService settings) => _settings = settings;

    /// <summary>True when the LLM layer is switched on AND has an API key — only then do
    /// the Layer-2 calls run. The model is fixed, so the key is the only requirement next
    /// to the master switch; without a key everything stays on Layer 1.</summary>
    public bool IsConfigured => _settings.Current is { LlmEnabled: true } s &&
        !string.IsNullOrWhiteSpace(s.LlmApiKey);

    /// <summary>Human-readable state for the Settings tab (deactivated / missing key /
    /// ready with the fixed model name).</summary>
    public string Status
    {
        get
        {
            var s = _settings.Current;
            if (s is null || !s.LlmEnabled)
            {
                return "deaktiviert (Layer 1 aktiv)";
            }

            if (string.IsNullOrWhiteSpace(s.LlmApiKey))
            {
                return "API-Key fehlt";
            }

            return $"bereit ({LlmConstants.FixedModel})";
        }
    }

    /// <summary>The client for the saved settings, rebuilt when the key or base URL
    /// change so a settings save applies without an app restart. The model is always the
    /// fixed <see cref="LlmConstants.FixedModel"/> — a stored or hand-edited model name
    /// is ignored.</summary>
    private OllamaClient Client
    {
        get
        {
            var s = _settings.Current ?? new AppSettings();
            if (_client is null ||
                _clientKey != s.LlmApiKey ||
                _clientBaseUrl != s.LlmBaseUrl)
            {
                _client = new OllamaClient(
                    string.IsNullOrWhiteSpace(s.LlmBaseUrl) ? "https://ollama.com" : s.LlmBaseUrl,
                    s.LlmApiKey,
                    LlmConstants.FixedModel,
                    _handler);
                _clientKey = s.LlmApiKey;
                _clientBaseUrl = s.LlmBaseUrl;
            }

            return _client;
        }
    }

    /// <summary>Tests the connection with the given (possibly unsaved) settings — the
    /// Settings tab tests before the user hits Save. Refuses without an API key: a
    /// connection can never be "OK" when no key was entered.</summary>
    public async Task<string?> TestAsync(string apiKey, string baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null; // API key is mandatory — no key, no connection test
        }

        var client = new OllamaClient(
            string.IsNullOrWhiteSpace(baseUrl) ? "https://ollama.com" : baseUrl,
            apiKey,
            LlmConstants.FixedModel,
            _handler);
        return await client.CompleteAsync(
            "Du bist ein Test. Antworte nur mit: OK", "Verbindungstest", ct).ConfigureAwait(false);
    }

    public async Task<string?> CommentateAsync(CommentaryContext context, CancellationToken ct) =>
        await new LlmRaceCommentator(Client).CommentateAsync(context, ct).ConfigureAwait(false);

    public async Task<string?> CoachAsync(CoachReport report, CancellationToken ct) =>
        await new LlmRaceCoach(Client).CoachAsync(report, ct).ConfigureAwait(false);

    public async Task<string?> SummarizeAsync(RaceSummary summary, CancellationToken ct) =>
        await new LlmRaceSummarizer(Client).SummarizeAsync(summary, ct).ConfigureAwait(false);

    public async Task<string?> LiveAnalyzeAsync(LiveAnalysisDigest digest, LiveAnalysisTriggerReason reason, CancellationToken ct) =>
        await new LlmLiveAnalyst(Client).AnalyzeAsync(digest, reason, ct).ConfigureAwait(false);

    public async Task<string?> RivalAnalyzeAsync(RivalDigest digest, CancellationToken ct) =>
        await new LlmRivalAnalyst(Client).AnalyzeAsync(digest, ct).ConfigureAwait(false);
}
