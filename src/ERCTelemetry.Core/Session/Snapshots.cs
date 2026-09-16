using F1Game.UDP.Enums;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Session;

/// <summary>Identity and core facts of the current game session. SessionUid changes ⇒ new session.
/// Trailing race-day facts: safety-car state, the game's suggested pit window (laps), and the
/// marshal zones with their current flag (null until the first Session packet with zones).</summary>
public sealed record SessionMeta(
    ulong SessionUid,
    SessionType SessionType,
    Track Track,
    byte TotalLaps,
    ushort TrackLength,
    bool IsNetworkGame,
    byte PlayerCarIndex,
    GameMode GameMode,
    Weather Weather,
    sbyte TrackTemperature,
    sbyte AirTemperature,
    ushort SessionTimeLeft,
    IReadOnlyList<ForecastSample>? Forecast = null,
    int SafetyCarStatus = 0,
    ushort PitWindowIdealLap = 0,
    ushort PitWindowLatestLap = 0,
    IReadOnlyList<MarshalZoneStatus>? MarshalZones = null,
    ushort TimeOfDay = 0,                // local time of day, minutes since midnight
    FormulaType Formula = default,
    ushort PitSpeedLimit = 0,
    float Sector2LapDistanceStart = 0,   // metres into the lap where S2/S3 begin
    float Sector3LapDistanceStart = 0,
    int NumDrsZones = 0,
    IReadOnlyList<DrsZoneInfo>? DrsZones = null,
    RuleSet RuleSet = default,
    ushort SessionDuration = 0);         // seconds (0 = timed-out/unknown)

/// <summary>One DRS zone on the track (from the Session packet): start/end as the 0..1
/// fraction through the lap.</summary>
public sealed record DrsZoneInfo(float ZoneStart, float ZoneEnd);

/// <summary>One marshal zone along the lap. <see cref="ZoneStart"/> is the 0..1 fraction
/// through the lap where the zone begins; <see cref="Flag"/> mirrors F1Game.UDP's
/// <c>FiaFlag</c> (Unknown −1, None 0, Green 1, Blue 2, Yellow 3) as raw int.</summary>
public sealed record MarshalZoneStatus(float ZoneStart, int Flag);

/// <summary>Body damage detail of one car (from the CarDamage packet), 0–100 % each,
/// plus electrical faults. Tells the driver which side to protect and when repairs pay off.</summary>
public sealed record CarDamageStatus(
    byte CarIndex,
    byte FrontLeftWing,
    byte FrontRightWing,
    byte RearWing,
    byte Floor,
    byte Diffuser,
    bool DrsFault,
    byte Sidepod = 0,
    byte FrontBrakeDamage = 0,       // per-wheel brake damage 0–100 (packet: FL/FR/RL/RR)
    byte RearBrakeDamage = 0,
    byte GearBoxDamage = 0,
    byte EngineDamage = 0,
    bool ErsFault = false,
    bool GearBoxFault = false,
    bool EngineFault = false,
    bool EngineBlown = false,
    bool EngineSeized = false,
    float EngineIceWear = 0,         // internal-combustion wear 0..1
    float EngineMguhWear = 0,
    float EngineEsWear = 0,
    float EngineCeWear = 0);

/// <summary>The player's car setup as of one CarSetups packet — everything the game
/// exposes about the car's mechanical/aero configuration. Tyre pressures in PSI
/// (order FL/FR/RL/RR), camber in degrees (negative = more negative camber).</summary>
public sealed record CarSetupSnapshot(
    byte FrontWing,
    byte RearWing,
    byte OnThrottle,
    byte OffThrottle,
    float FrontCamber,
    float RearCamber,
    float FrontToe,
    float RearToe,
    byte FrontSuspension,
    byte RearSuspension,
    byte FrontAntiRollBar,
    byte RearAntiRollBar,
    byte FrontSuspensionHeight,
    byte RearSuspensionHeight,
    byte BrakePressure,
    byte BrakeBias,
    byte EngineBraking,
    float Ballast,
    float FuelLoad,
    float[] TyresPressure,          // PSI, 4 entries FL/FR/RL/RR
    byte NextFrontWingValue)
{
    /// <summary>Value equality including the TyresPressure array (record equality would
    /// compare the array by reference, so identical re-sends would re-emit forever).</summary>
    public bool Equals(CarSetupSnapshot? other) =>
        other is not null &&
        FrontWing == other.FrontWing && RearWing == other.RearWing &&
        OnThrottle == other.OnThrottle && OffThrottle == other.OffThrottle &&
        FrontCamber == other.FrontCamber && RearCamber == other.RearCamber &&
        FrontToe == other.FrontToe && RearToe == other.RearToe &&
        FrontSuspension == other.FrontSuspension && RearSuspension == other.RearSuspension &&
        FrontAntiRollBar == other.FrontAntiRollBar && RearAntiRollBar == other.RearAntiRollBar &&
        FrontSuspensionHeight == other.FrontSuspensionHeight &&
        RearSuspensionHeight == other.RearSuspensionHeight &&
        BrakePressure == other.BrakePressure && BrakeBias == other.BrakeBias &&
        EngineBraking == other.EngineBraking && Ballast == other.Ballast &&
        FuelLoad == other.FuelLoad &&
        TyresPressure.AsSpan().SequenceEqual(other.TyresPressure) &&
        NextFrontWingValue == other.NextFrontWingValue;

    public override int GetHashCode() => HashCode.Combine(
        FrontWing, RearWing, OnThrottle, OffThrottle, FrontCamber, RearCamber,
        FrontToe, HashCode.Combine(RearToe, BrakeBias, FuelLoad, NextFrontWingValue));
}

/// <summary>Per-lap aggregate of the player's high-rate motion data (MotionEx + Motion
/// g-forces), built by <see cref="MotionSummaryRecorder"/>. One record per completed lap;
/// arrays of 4, order FL/FR/RL/RR.</summary>
public sealed record LapMotionSummaryData(
    float[] MaxWheelSlipRatio,      // per wheel, lap maximum
    float MinFrontAeroHeight,       // metres
    float MinRearAeroHeight,
    float MaxLateralG,
    float MaxLongitudinalG);

/// <summary>One weather-forecast sample (from the Session packet, refreshed in-game
/// every few seconds). RainPercent 0–100, TimeOffsetMinutes counts ahead of now.</summary>
public sealed record ForecastSample(
    int TimeOffsetMinutes,
    Weather Weather,
    byte RainPercent,
    sbyte TrackTemperature,
    sbyte AirTemperature);

/// <summary>World X/Z positions (metres) of up to 22 cars from the latest Motion packet,
/// index-aligned with car index, plus each car's normalized world forward direction
/// (for the radar's relative geometry). Zero entries before the first packet.</summary>
public sealed record MotionFrame(
    byte Count,
    float[] X,
    float[] Z,
    float[] ForwardX,
    float[] ForwardZ);

/// <summary>One driver in the current session (from the Participants packet).
/// <see cref="GameName"/> carries the original game-provided name when
/// <see cref="Name"/> was resolved through a user display-name override.</summary>
public sealed record DriverEntry(
    byte CarIndex,
    string Name,
    Team Team,
    ushort RaceNumber,
    bool IsAiControlled,
    bool IsPlayer,
    bool IsTelemetryPublic,
    string? GameName = null);

/// <summary>Position/timing state of one car (from the LapData packet).</summary>
public sealed record CarTiming(
    byte CarIndex,
    byte Position,
    byte CurrentLapNum,
    float LapDistance,
    uint CurrentLapTimeMs,
    uint LastLapTimeMs,
    ushort Sector1TimeMs,
    ushort Sector2TimeMs,
    int GapToLeaderMs,
    int GapToCarInFrontMs,
    PitStatus PitStatus,
    byte NumPitStops,
    byte Penalties,
    DriverStatus DriverStatus,
    ResultStatus ResultStatus,
    byte GridPosition = 0,
    byte LapValidity = 0,          // 1 = current lap INVALID (corner cut) — HUD badge
    float FuelUsedLastLap = 0,     // fuel burnt on the most recently completed lap (litres)
    float ErsUsedLastLapJ = 0,     // net ERS energy of the last lap (J, signed: harvest refills)
    ushort BestSector1Ms = 0,      // driver's own best sector times this session (0 = none seen)
    ushort BestSector2Ms = 0,
    ushort BestSector3Ms = 0,
    byte Sector = 0);              // current sector the car is in (0=S1, 1=S2, 2=S3) — live-delta feed

/// <summary>Green = personal best for that driver, purple = session best across all cars,
/// none = unremarkable — the classic F1 broadcast sector coloring.</summary>
public enum SectorMark
{
    None = 0,
    Green = 1,
    Purple = 2,
}

/// <summary>Consumables/tyres of one car (from the CarStatus packet).</summary>

/// <summary>Consumables/tyres of one car (from the CarStatus packet).</summary>
public sealed record CarCondition(
    byte CarIndex,
    ActualCompound TyreCompound,
    byte TyreAgeLaps,
    float FuelInTank,
    float FuelRemainingLaps,
    float ErsStoreEnergy, // joules (up to ~4 MJ)
    ErsDeployMode ErsDeployMode,
    bool DrsAllowed);

/// <summary>Live driving data of one car (from the CarTelemetry packet, ~60Hz).</summary>
public sealed record CarTelemetry(
    byte CarIndex,
    ushort Speed,
    sbyte Gear,
    float Throttle,
    float Brake,
    ushort EngineRpm,
    bool IsDrsOn);

/// <summary>One row of the ordered leaderboard shown in UI and overlays. Sector 1/2 come
/// straight from the LapData packet (times of the most recently completed sectors);
/// sector 3 is derived by <see cref="StandingsCalculator"/> — the packets carry no S3 time.
/// 0 means "not available right now".</summary>
public sealed record StandingsRow(
    byte Position,
    byte CarIndex,
    string Name,
    Team Team,
    ushort RaceNumber,
    byte CurrentLapNum,
    uint LastLapTimeMs,
    uint BestLapTimeMs,
    int GapToLeaderMs,
    int GapToCarInFrontMs,
    PitStatus PitStatus,
    byte Penalties,
    ResultStatus ResultStatus,
    ActualCompound TyreCompound,
    byte TyreAgeLaps,
    bool IsPlayer,
    ushort Sector1TimeMs = 0,
    ushort Sector2TimeMs = 0,
    ushort Sector3TimeMs = 0,
    byte GridPosition = 255,      // 255 = unknown (packet default); commentary pages derive Δ-grid
    byte NumPitStops = 0,
    uint CurrentLapTimeMs = 0,
    byte LapValidity = 0,
    float FuelUsedLastLap = 0,
    float ErsUsedLastLapJ = 0,    // net ERS energy of the last lap (J, signed)
    SectorMark S1Status = SectorMark.None,
    SectorMark S2Status = SectorMark.None,
    SectorMark S3Status = SectorMark.None,
    ushort BestSector1Ms = 0,     // driver's own best sectors this session — live-delta feed
    ushort BestSector2Ms = 0,
    ushort BestSector3Ms = 0,
    int LapDeltaMs = 0,           // live lap delta vs. own best sectors (0 = unknown/not in S2+S3)
    uint PredictedLapMs = 0);     // best lap + live delta — where this lap is heading (0 = unknown)

/// <summary>The combined fast-rate frame for one driver (player or rival).</summary>
public sealed record PlayerFrame(
    byte CarIndex,
    string Name,
    ushort Speed,
    sbyte Gear,
    float Throttle,
    float Brake,
    ushort EngineRpm,
    float ErsStoreEnergy, // joules (up to ~4 MJ)
    ErsDeployMode ErsDeployMode,
    float FuelInTank,
    float FuelRemainingLaps,
    ActualCompound TyreCompound,
    byte TyreAgeLaps,
    bool DrsAllowed,
    bool IsDrsOn,
    float Steer = 0,               // −1 (left) .. +1 (right)
    byte FrontBrakeBias = 0,       // % braking force on the front axle
    float GLat = 0,                // lateral G (signed)
    float GLong = 0,               // longitudinal G (signed)
    WheelState? Wheels = null)     // per-wheel pressures/temps (null before first telemetry)
{
    /// <summary>ERS store as 0–100 % of the 4 MJ maximum (clamped) — UI percent readout.</summary>
    public float ErsPercent => Math.Clamp(ErsStoreEnergy / TelemetryConstants.MaxErsJoules * 100f, 0f, 100f);
}

/// <summary>Per-wheel snapshot (arrays of 4, order FL/FR/RL/RR) from the CarTelemetry packet.
/// Built only for the player/rival frames — 60 Hz raw data never reaches consumers.</summary>
public sealed record WheelState(
    float[] Pressure,        // PSI
    float[] SurfaceTemp,     // °C
    float[] BrakeTemp);      // °C

/// <summary>One tyre set from the tyre bank (TyreSets packet). Wear 0–100 %,</summary>
/// <remarks>FittedIndex on the packet identifies the fitted set; mirrored as <c>IsFitted</c>.</remarks>
public sealed record TyreSetEntry(
    byte Index,
    ActualCompound Compound,
    VisualCompound Visual,
    byte Wear,
    bool IsAvailable,
    bool IsFitted,
    ushort LifeSpan,
    ushort UsableLife);

/// <summary>Tyre health of one car (from the CarDamage packet). Wear/damage are the WORST
/// of the four wheels, 0–100 %. Null entries mean "no CarDamage packet yet" — the overlay
/// heatmap only ranks cars that have reported.</summary>
public sealed record TyreStatus(
    byte CarIndex,
    float WearPercent,
    float DamagePercent);

/// <summary>Definitive end-of-session result row (FinalClassification packet) with the
/// game's tyre-stint summary (max 8 stints; empty arrays when absent).</summary>
public sealed record FinalResultRow(
    byte Position,
    byte CarIndex,
    string Name,
    Team Team,
    ushort RaceNumber,
    byte NumLaps,
    ushort GridPosition,
    float Points,
    ResultStatus ResultStatus,
    uint BestLapTimeMs,
    double TotalRaceTimeSeconds,
    byte PenaltiesTime,
    byte NumPenalties,
    byte NumPitStops = 0,
    string ResultReason = "",
    byte NumTyreStints = 0,
    ActualCompound[] StintsActual = null!,      // NumTyreStints entries (null! = absent)
    VisualCompound[] StintsVisual = null!,
    byte[] StintsEndLaps = null!);

/// <summary>One rendered race-control event (fastest lap, penalty, retirement, ...).
/// Sequence is assigned by the single writer when the event is appended, so consumers
/// can dedup/detect-new cheaply (wall-clock ticks are not unique). LapNumber is the lap
/// the event happened on — penalties carry the game-reported lap, everything else is
/// stamped by the store from the involved car's current lap; 0 = unknown. LapDistance is
/// the involved car's distance-around-lap (metres) when the event fired — stamped by the
/// store for penalties/warnings so the History can show "Kurve/Sektor"; -1 = unknown.</summary>
public sealed record RaceEventEntry(
    DateTimeOffset Utc,
    string Type,
    byte? CarIndex,
    string Text,
    long Sequence = 0,
    int LapNumber = 0,
    byte? SecondCarIndex = null,   // collision: the other car involved
    int DetailValue = 0,           // collision: impact severity (0..999)
    float LapDistance = -1f);      // metres into the lap at event time (-1 = unknown)

/// <summary>Immutable projection of the whole session state. Rebuilt by the aggregator
/// after each drain cycle; consumed by WPF UI, overlay server and persistence.</summary>
public sealed record TelemetrySnapshot(
    SessionMeta? Meta,
    IReadOnlyList<DriverEntry> Drivers,
    IReadOnlyList<StandingsRow> Standings,
    IReadOnlyList<FinalResultRow> FinalResults,
    IReadOnlyList<RaceEventEntry> RecentEvents,
    PlayerFrame? Player,
    PlayerFrame? Rival,
    long FrameVersion,
    DateTimeOffset BuiltAtUtc,
    IReadOnlyList<TyreStatus>? Tyres = null,
    MotionFrame? Positions = null,
    IReadOnlyList<CarDamageStatus>? Damages = null,
    IReadOnlyList<TyreSetEntry>? TyreSets = null,
    StrategyAdvice? Strategy = null,
    IReadOnlyList<H2hTally>? H2h = null,
    FuelAdvice? Fuel = null);

/// <summary>Head-to-head duel tally between two cars (CarIndex vs OpponentIndex, one
/// entry per pair): LapsWon is CarIndex's laps won on the faster lap time against exactly
/// this opponent (OpponentLapsWon the reverse), PositionWins the laps where CarIndex
/// first reached the better race position at a shared lap completion.</summary>
public sealed record H2hTally(
    byte CarIndex,
    byte OpponentIndex,
    int LapsWon,
    int PositionWins,
    int OpponentLapsWon,
    int OpponentPositionWins);