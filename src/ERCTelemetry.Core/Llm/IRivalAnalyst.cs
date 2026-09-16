using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Formulates a German sentence (or two) about a notable rival action. Layer 1 is
/// the deterministic <see cref="RivalTemplate"/> (free, no key); Layer 2 is the
/// <see cref="LlmRivalAnalyst"/> (API key), which returns null on failure so the caller
/// falls back to the template.</summary>
public interface IRivalAnalyst
{
    Task<string?> AnalyzeAsync(RivalDigest digest, CancellationToken ct);
}
