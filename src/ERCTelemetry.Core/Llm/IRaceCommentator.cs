namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 live commentator: turns a batch of recent Layer-1 commentary lines
/// plus session context into one natural German commentary line. Implementations: LLM
/// (Ollama) or a template. Returns null when no comment is warranted or the provider
/// failed — callers keep the Layer-1 line then.</summary>
public interface IRaceCommentator
{
    Task<string?> CommentateAsync(CommentaryContext context, CancellationToken ct);
}
