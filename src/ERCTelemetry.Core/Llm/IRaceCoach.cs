using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 AI coach: rewrites the structured Layer-1 <see cref="CoachReport"/>
/// findings into natural German advice. Implementations: LLM (Ollama) or a template.
/// Returns null when the provider failed — the Layer-1 findings stay visible then.</summary>
public interface IRaceCoach
{
    Task<string?> CoachAsync(CoachReport report, CancellationToken ct);
}
