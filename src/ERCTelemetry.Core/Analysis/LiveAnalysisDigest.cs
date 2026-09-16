using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Lap-time trend of the player's recent laps.</summary>
public enum LapTrend { Slower, Faster, Stable }

/// <summary>Structured state digest the live analysis speaks from: lap-time trend, tyre
/// wear and fuel projection. Built by <see cref="LiveAnalysisDigestBuilder"/> from
/// periodic snapshots; <see cref="LiveAnalysisTrigger"/> decides when it is worth
/// speaking. Layer 1 — deterministic, free, works without an API key.</summary>
public sealed record LiveAnalysisDigest(
    byte Lap,
    byte TotalLaps,
    byte Position,
    int GapToLeaderMs,
    IReadOnlyList<uint> LastLapTimesMs,   // rolling window (last 5 completed laps)
    uint BestLapTimeMs,
    LapTrend Trend,                        // Slower / Faster / Stable
    float TrendDeltaMsPerLap,              // + = slower per lap
    ActualCompound TyreCompound,
    byte TyreAgeLaps,
    float TyreWearPercent,                 // worst wheel, 0–100
    float FuelInTank,                       // litres on board
    float FuelRemainingLaps,               // game-reported laps of fuel
    float FuelUsedLastLap,                 // litres burnt on the last completed lap
    float ProjectedFuelAtEnd);             // tank − burn × laps to go (negative = shortfall)
