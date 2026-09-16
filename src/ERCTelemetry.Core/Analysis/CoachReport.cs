namespace ERCTelemetry.Core.Analysis;

/// <summary>One structured coaching finding: where the player lost time against the
/// rival and what to do about it. <see cref="CornerNumber"/> 0 = not corner-anchored
/// (a general trace tip). <see cref="LossSeconds"/> is the time lost in that corner
/// window; <see cref="AtPercent"/> anchors it on the track (0–100).</summary>
public sealed record CoachFinding(
    byte CornerNumber,
    float LossSeconds,
    float AtPercent,
    string Advice);

/// <summary>The Layer-1 AI-Coach report: a structured gap report built from the
/// <see cref="DuelReport"/>'s trace tips and corner losses, each mapped to a German
/// coaching tip. Deterministic, offline, headless-testable. Layer 2 (LLM) can later
/// rephrase these findings naturally — the structure stays the same.</summary>
public sealed record CoachReport(
    string RivalName,
    double TotalLossSeconds,
    IReadOnlyList<CoachFinding> Findings);
