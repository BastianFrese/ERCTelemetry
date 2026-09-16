using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Turns the structured <see cref="LiveAnalysisDigest"/> plus the trigger reason
/// into natural German advice. Two implementations: <see cref="LiveAnalysisTemplate"/>
/// (Layer 1, deterministic, free) and <see cref="LlmLiveAnalyst"/> (Layer 2, LLM behind
/// the API key). Returns null when the provider failed — the caller stays silent then.</summary>
public interface ILiveAnalyst
{
    Task<string?> AnalyzeAsync(LiveAnalysisDigest digest, LiveAnalysisTriggerReason reason, CancellationToken ct);
}
