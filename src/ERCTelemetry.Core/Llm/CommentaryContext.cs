namespace ERCTelemetry.Core.Llm;

/// <summary>Snapshot of the live session handed to the Layer-2 commentator: the recent
/// Layer-1 commentary lines plus the context they were produced in. The LLM uses this to
/// write one varied German line instead of repeating the deterministic templates.</summary>
public sealed record CommentaryContext(
    string Track,
    string SessionType,
    int Lap,
    int TotalLaps,
    string Weather,
    int PlayerPosition,
    IReadOnlyList<string> RecentLines);
