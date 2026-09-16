using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 race summarizer backed by an <see cref="OllamaClient"/>: rewrites the
/// deterministic Layer-1 paragraphs into a natural German story. Null on failure — the
/// Layer-1 paragraphs stay visible.</summary>
public sealed class LlmRaceSummarizer : IRaceSummarizer
{
    private readonly OllamaClient _client;

    public LlmRaceSummarizer(OllamaClient client) => _client = client;

    public async Task<string?> SummarizeAsync(RaceSummary summary, CancellationToken ct)
    {
        var system =
            "Du bist ein deutscher F1-Rennreporter. Schreibe die folgende Rennzusammenfassung " +
            "in 3–5 natürliche, abwechslungsreiche deutsche Sätze um. Behalte alle Fakten, " +
            "erzähle eine kleine Geschichte, keine Anführungszeichen.";
        var user = string.Join("\n", summary.Paragraphs);
        return await _client.CompleteAsync(system, user, ct).ConfigureAwait(false);
    }
}
