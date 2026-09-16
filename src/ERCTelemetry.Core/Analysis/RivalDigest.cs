using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Notable action of the tracked rival that is worth announcing.</summary>
public enum RivalEvent { PitStop, TyreChange, FastLap }

/// <summary>What happened to the rival the player is racing against. Carries the state the
/// Layer-1 template / Layer-2 LLM need to phrase one or two German sentences.</summary>
public sealed record RivalDigest(
    string RivalName,
    RivalEvent Event,
    byte Lap,
    ActualCompound Compound,
    byte TyreAgeLaps,
    uint LastLapTimeMs,
    uint BestLapTimeMs,
    int GapToPlayerMs,
    float TrendDeltaMsPerLap);   // + = slower per lap (0 = not enough laps yet)
