using F1Game.UDP.Enums;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Session;

/// <summary>Something the aggregator decides happened while applying a packet.
/// Consumers (persistence, overlays) react to these; the UI works off snapshots.</summary>
public abstract record StoreEvent;

/// <summary>A new session started (SessionUid changed).</summary>
public sealed record SessionStarted(SessionMeta Meta) : StoreEvent;

/// <summary>The session ended — by FinalClassification, session change or shutdown.</summary>
public sealed record SessionEnded(ulong SessionUid, string Reason, IReadOnlyList<FinalResultRow> Results)
    : StoreEvent;

/// <summary>A driver completed a lap (not fired again on replay/flashback). Tyre and
/// position snapshot the car state at detection time so persistence can record them
/// without correlating later state. The optional members carry the full LapData facts
/// of the completed lap (0/defaults = not seen yet).</summary>
public sealed record LapCompleted(
    byte CarIndex,
    string DriverName,
    byte LapNumber,
    uint LapTimeMs,
    ushort Sector1TimeMs,
    ushort Sector2TimeMs,
    ActualCompound TyreCompound = (ActualCompound)0,
    byte TyreAgeLaps = 0,
    byte Position = 0,
    float ErsUsedJoules = 0,
    float FuelUsedLitres = 0,
    PitStatus PitStatus = PitStatus.None,
    byte NumPitStops = 0,
    byte PenaltiesSeconds = 0,
    DriverStatus DriverStatus = DriverStatus.InGarage,
    ResultStatus ResultStatus = ResultStatus.Inactive,
    byte GridPosition = 255,             // 255 = unknown (packet default)
    byte ActiveAeroMode = 255,           // 255 = unknown (no CarTelemetry2 seen)
    bool OvertakeUsed = false,
    bool WrongWay = false) : StoreEvent;

/// <summary>The full Participants/LobbyInfo driver roster (with team/number/AI flags).
/// Fired when the roster's details changed; persistence upserts the drivers table.</summary>
public sealed record DriversRegistered(IReadOnlyList<DriverEntry> Drivers) : StoreEvent;

/// <summary>The player's just-completed lap has a usable speed-over-distance trace
/// (sampled by the trace recorder) — persistence stores it for later lap comparison.</summary>
public sealed record LapTraced(byte CarIndex, byte LapNumber, LapTrace Trace) : StoreEvent;

/// <summary>A race-control style entry was appended to the event feed (personal best,
/// overtake on the player, fastest lap, ...) — persistence stores it in the events table.</summary>
public sealed record RaceControl(RaceEventEntry Event) : StoreEvent;

/// <summary>An adjacent position swap between two racing cars was detected. Persistence
/// records it for the session's highlights timeline; <see cref="Utc"/> is stamped at
/// detection time.</summary>
public sealed record Overtake(
    byte CarIndex,
    string DriverName,
    byte PassedCarIndex,
    string PassedDriverName,
    byte NewPosition,
    byte LapNumber) : StoreEvent
{
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One completed lap from the SessionHistory packet (the game's own per-lap
/// history, arriving ~2 Hz per car). Sector times already include their minutes parts;
/// ValidFlags mirrors F1Game.UDP's LapValid bitfield (bit0 = lap, bit1..3 = S1..S3).</summary>
public sealed record LapHistoryRow(
    int LapNumber,
    uint LapTimeMs,
    uint Sector1Ms,
    uint Sector2Ms,
    uint Sector3Ms,
    ushort ValidFlags);

/// <summary>One tyre stint from the SessionHistory packet. EndLap 255 = ongoing stint.</summary>
public sealed record StoredTyreStint(
    byte StintIndex,
    ActualCompound Actual,
    VisualCompound Visual,
    byte EndLap);

/// <summary>Incremental per-car lap history from the SessionHistory packet: only the laps
/// not yet handed out are in NewLaps. Stints are sent complete whenever the stint count
/// changed. BestLap/BestSector lap numbers are 1-based as reported by the game.</summary>
public sealed record LapHistoryUpdated(
    byte CarIndex,
    int TotalLaps,
    IReadOnlyList<LapHistoryRow> NewLaps,
    IReadOnlyList<StoredTyreStint> Stints,
    byte BestLapLapNum,
    byte BestS1LapNum,
    byte BestS2LapNum,
    byte BestS3LapNum) : StoreEvent;

/// <summary>The player's car setup (CarSetups packet, deduplicated via record equality).
/// Multiplayer only reports the player's own setup — rivals' slots arrive empty.</summary>
public sealed record SetupCaptured(byte CarIndex, CarSetupSnapshot Setup) : StoreEvent;

/// <summary>One chunk of the lap-by-lap position history (LapPositions packet, up to 50
/// laps × 24 car slots). StartingLap is 0-based as reported by the game; the chunk is
/// re-emitted only when the packet's StartingLap advances.</summary>
public sealed record LapPositionsChunk(
    byte StartingLap,
    byte NumLaps,
    IReadOnlyList<byte[]> PositionsPerLap) : StoreEvent; // Array24 per lap (lap*24+car indexing in DB)

/// <summary>The CarDamage packet's full detail of one car changed (wear rounded to whole
/// percent). Fired at most once per actual change — not per packet. TyreWorst/DamageWorst
/// are the 0–100 % per-car maxima; LapNumber the car's current lap (0 = unknown).</summary>
public sealed record CarDamageChanged(
    byte CarIndex,
    CarDamageStatus Damage,
    byte TyreWorst = 0,
    byte DamageWorst = 0,
    int LapNumber = 0) : StoreEvent;

/// <summary>Per-lap aggregate of the player's high-rate motion data (MotionEx + Motion
/// g-forces): max slip ratio per wheel, min aero heights, max |g|. Emitted right after
/// the matching LapCompleted.</summary>
public sealed record LapMotionSummary(
    byte CarIndex,
    byte LapNumber,
    LapMotionSummaryData Data) : StoreEvent;