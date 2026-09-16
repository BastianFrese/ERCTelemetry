using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 race summarizer: rewrites the deterministic Layer-1
/// <see cref="RaceSummary"/> paragraphs into a natural German story. Implementations:
/// LLM (Ollama) or a template. Returns null when the provider failed — the Layer-1
/// paragraphs stay visible then.</summary>
public interface IRaceSummarizer
{
    Task<string?> SummarizeAsync(RaceSummary summary, CancellationToken ct);
}
