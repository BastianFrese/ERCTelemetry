using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Header facts of one stored session as shown at the top of the race report
/// (in-app tab and HTML export). Times in ms, temperatures in °C, TimeOfDay in minutes.</summary>
public sealed record ReportHeader(
    long SessionId,
    string SessionType,
    string Track,
    int TotalLaps,
    int TrackLength,
    string GameMode,
    string Weather,
    bool IsNetworkGame,
    int TrackTemp,
    int AirTemp,
    int TimeOfDay,
    string Formula,
    int PitSpeedLimit,
    int NumDrsZones,
    string RuleSet,
    int SessionDuration,
    string StartedUtc,
    string? EndReason,
    int NumDrivers,
    int NumLapsStored);

/// <summary>Best/average/σ of one sector across a driver's laps. BestMs 0 = unknown.</summary>
public sealed record SectorStats(uint BestMs, double AverageMs, double SigmaMs);

/// <summary>Full race-report picture of one driver. Position 0 = no classification row;
/// DamageEndState null = no damage change was ever logged for the car.</summary>
public sealed record PerDriverSummary(
    byte CarIndex,
    string Name,
    Team Team,
    ushort RaceNumber,
    byte Position,
    float Points,
    string ResultStatus,
    string ResultReason,
    ushort GridPosition,
    byte NumPitStops,
    int RacingLaps,
    uint BestLapMs,
    double AverageLapMs,
    double ConsistencySigmaMs,
    SectorStats S1,
    SectorStats S2,
    SectorStats S3,
    int LapsValid,
    int LapsLed,
    int PositionsGained,
    int PenaltiesSeconds,
    double FuelUsedLitres,
    double ErsUsedMegajoules,
    double TotalRaceTimeSeconds,
    IReadOnlyList<StintSummary> Stints,
    CarDamageStatus? DamageEndState);

/// <summary>One lap of a driver for the lap-time chart and the duel lap-join:
/// sector times 0 = unknown, IsValid -1/0/1 as stored in laps.</summary>
public sealed record LapPoint(
    int LapNumber,
    uint LapTimeMs,
    uint S1Ms,
    uint S2Ms,
    uint S3Ms,
    int IsValid,
    string Tyre,
    byte Position);

/// <summary>One driver's lap-time chart series.</summary>
public sealed record LapPointSeries(byte CarIndex, string Name, IReadOnlyList<LapPoint> Points);

/// <summary>One stored lap-position chunk (lap_positions table): Rows[lapOffset][carSlot]
/// holds the 1..22 position of that car on that lap (0 = unknown). StartingLap is 0-based
/// as sent by the game's LapPositions packet.</summary>
public sealed record PositionChunk(int StartingLap, int NumLaps, IReadOnlyList<IReadOnlyList<byte>> Rows);

/// <summary>One driver's stored speed trace of a lap in the report's trace section.</summary>
public sealed record TracedLap(byte CarIndex, string Name, LapTrace Trace);

/// <summary>Unified timeline entry: race-control events and overtakes in one
/// chronological feed. LapNumber 0 = not lap-anchored.</summary>
public sealed record TimelineEntry(
    DateTimeOffset Utc,
    string Type,
    byte? CarIndex,
    byte? SecondCarIndex,
    string Text,
    int LapNumber,
    int DetailValue);

/// <summary>One duel pair: two laps joined by lap number.</summary>
public sealed record DuelLapPair(int LapNumber, uint LapTimeA, uint LapTimeB);

/// <summary>Head-to-head sector wins per sector (ties count neither side).</summary>
public sealed record SectorWinTally(int A1, int B1, int A2, int B2, int A3, int B3);

/// <summary>Two drivers' stints joined by stint index (null side = no matching stint).</summary>
public sealed record StintPair(int StintNumber, StintSummary? A, StintSummary? B);

/// <summary>Full head-to-head comparison of two drivers. BestDeltaMs = A − B
/// (negative = A faster); LapPairs contain only laps both drivers completed.</summary>
public sealed record DuelReport(
    PerDriverSummary A,
    PerDriverSummary B,
    int LapWinsA,
    int LapWinsB,
    IReadOnlyList<DuelLapPair> LapPairs,
    SectorWinTally SectorWins,
    double BestDeltaMs,
    double AverageDeltaMs,
    double SigmaDeltaMs,
    IReadOnlyList<StintPair> Stints,
    IReadOnlyList<TraceTip> TraceTips,
    IReadOnlyList<CornerLoss> CornerLosses);

/// <summary>The immutable race report: the single data source for the in-app report tab
/// (Phase 4) and the HTML export (Phase 5). Built by <see cref="RaceReportBuilder"/> from
/// the telemetry database.</summary>
public sealed record RaceReport(
    ReportHeader? Header,
    IReadOnlyList<PerDriverSummary> Classification,
    PerDriverSummary? Player,
    IReadOnlyList<ConsistencyRow> Consistency,
    IReadOnlyList<LapPointSeries> LapSeries,
    IReadOnlyList<PositionChunk> FieldPositions,
    DuelReport? Duel,
    IReadOnlyList<TelemetryDb.DamageLogRow> DamageLog,
    IReadOnlyList<TimelineEntry> Timeline,
    CarSetupSnapshot? Setup,
    IReadOnlyList<TracedLap> Traces);