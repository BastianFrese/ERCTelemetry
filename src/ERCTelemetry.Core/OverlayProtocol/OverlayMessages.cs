using System.Text.Json;
using System.Text.Json.Serialization;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.OverlayProtocol;

/// <summary>Wire identifiers of the overlay WebSocket protocol (server ⇄ browser client).</summary>
public static class OverlayProtocolConstants
{
    /// <summary>Bump on a breaking wire change so stale clients can detect a mismatch.</summary>
    public const int Version = 1;

    public const string HelloType = "hello";
    public const string PlayerType = "player";
    public const string StateType = "state";
    public const string EventType = "event";
    public const string SelectType = "select";
    public const string PingType = "ping";
    public const string MapType = "map";
    public const string TrackLayoutType = "tracklayout";
    public const string ConfigType = "config";
    public const string CommentaryType = "commentary";
}

/// <summary>Shared JSON settings for every overlay message. Enums are written as their
/// member names ("RedBullRacing", "F1C3", "Finished") so clients need no numeric tables;
/// out-of-range values fall back to the raw number.</summary>
public static class OverlayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>First message after a socket opens; confirms the protocol version.
/// <see cref="Scheme"/> is the active color scheme (null/"classic" = default look,
/// "de" = black-red-gold) — pages apply it as a body class.</summary>
public sealed record OverlayHello(
    [property: JsonPropertyName("t")] string Type,
    int Protocol,
    [property: JsonPropertyName("scheme")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scheme = null);

/// <summary>One weather-forecast entry (from the Session packet, refreshed in-game
/// every few seconds). TimeOffsetMinutes counts ahead of now; RainPercent 0–100.</summary>
public sealed record OverlayForecastSample(
    int TimeOffsetMinutes,
    string Weather,
    byte RainPercent,
    sbyte AirTemperature,
    sbyte TrackTemperature);

/// <summary>Slow-rate snapshot of the session itself.</summary>
public sealed record OverlaySession(
    ulong SessionUid,
    string SessionType,
    string Track,
    byte TotalLaps,
    string Weather,
    string GameMode,
    bool IsNetworkGame,
    byte PlayerCarIndex,
    sbyte AirTemperature,
    sbyte TrackTemperature,
    ushort SessionTimeLeft,
    IReadOnlyList<OverlayForecastSample>? Forecast = null,
    ushort PitWindowIdealLap = 0,
    ushort PitWindowLatestLap = 0,
    IReadOnlyList<OverlayMarshalZone>? MarshalZones = null);

/// <summary>One marshal zone (start fraction through the lap + the raw FiaFlag int:
/// Unknown −1, None 0, Green 1, Blue 2, Yellow 3) for flag-tracking overlays.</summary>
public sealed record OverlayMarshalZone(float ZoneStart, int Flag);

/// <summary>One leaderboard row (numbers raw in ms so pages format them themselves).</summary>
public sealed record OverlayStanding(
    byte Position,
    byte CarIndex,
    string Name,
    string Team,
    ushort RaceNumber,
    byte CurrentLapNum,
    uint LastLapTimeMs,
    uint BestLapTimeMs,
    int GapToLeaderMs,
    int GapToCarInFrontMs,
    string PitStatus,
    byte Penalties,
    string ResultStatus,
    string TyreCompound,
    byte TyreAgeLaps,
    bool IsPlayer,
    ushort Sector1TimeMs = 0,
    ushort Sector2TimeMs = 0,
    ushort Sector3TimeMs = 0,
    byte GridPosition = 255,
    byte PitStops = 0,
    uint CurrentLapTimeMs = 0,
    byte LapValidity = 0,        // 1 = current lap invalidated (corner cut)
    float FuelUsedLastLap = 0,   // litres burnt on the last completed lap
    SectorMark S1Status = SectorMark.None,
    SectorMark S2Status = SectorMark.None,
    SectorMark S3Status = SectorMark.None,
    uint BestSector1TimeMs = 0,
    uint BestSector2TimeMs = 0,
    uint BestSector3TimeMs = 0,
    int LapDeltaMs = 0,           // live lap delta vs. own best sectors (0 = unknown, in S1)
    uint PredictedLapTimeMs = 0); // best lap + live delta (0 = unknown)

/// <summary>End-of-session classification for one car.</summary>
public sealed record OverlayFinalResult(
    byte Position,
    byte CarIndex,
    string Name,
    string Team,
    ushort RaceNumber,
    byte NumLaps,
    ushort GridPosition,
    float Points,
    string ResultStatus,
    uint BestLapTimeMs,
    double TotalRaceTimeSeconds,
    byte PenaltiesTime,
    byte NumPenalties);

/// <summary>Fast-rate driving frame for a player/rival car. <see cref="ErsPercent"/> is
/// <see cref="ErsStoreEnergy"/> normalized against the 4 MJ store (0–100, clamped).
/// Trailing additions (setup/training): steering, brake balance, G-forces and the
/// per-wheel arrays (order FL/FR/RL/RR; null before the first CarTelemetry packet).</summary>
public sealed record OverlayPlayerFrame(
    byte CarIndex,
    string Name,
    ushort Speed,
    sbyte Gear,
    float Throttle,
    float Brake,
    ushort EngineRpm,
    float ErsStoreEnergy,
    float ErsPercent,
    string ErsDeployMode,
    float FuelInTank,
    float FuelRemainingLaps,
    string TyreCompound,
    byte TyreAgeLaps,
    bool DrsAllowed,
    bool DrsOn,
    float Steer = 0,
    byte FrontBrakeBias = 0,
    float GLat = 0,
    float GLong = 0,
    float[]? Pressure = null,
    float[]? SurfaceTemp = null,
    float[]? BrakeTemp = null);

/// <summary>~30 Hz drive frames. Only the player and the global rival are included; a
/// client's <c>select</c> picks which of the two lands in <see cref="Player"/>.</summary>
public sealed record OverlayPlayer(
    [property: JsonPropertyName("t")] string Type,
    OverlayPlayerFrame? Player,
    OverlayPlayerFrame? Rival);

/// <summary>5 Hz standings/session/points state, only sent when something in it changed.
/// <see cref="Scheme"/> carries the active color scheme ("de" = black-red-gold) so pages
/// still switch when the setting changes mid-stream.</summary>
public sealed record OverlayState(
    [property: JsonPropertyName("t")] string Type,
    OverlaySession? Session,
    IReadOnlyList<OverlayStanding> Standings,
    IReadOnlyList<OverlayFinalResult> Results,
    IReadOnlyList<OverlayTyre>? Tyres = null,
    [property: JsonPropertyName("scheme")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scheme = null,
    OverlayStrategy? Strategy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OverlayH2hTally>? H2h = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OverlayFuel? Fuel = null);

/// <summary>Lap-completion tally of the head-to-head duel between two cars (one entry
/// per pair): how many laps CarIndex won against exactly this opponent (faster lap) and
/// how many times it gained the better race position at a shared lap completion —
/// OpponentLapsWon/OpponentPositionWins carry the reverse direction of the same duel.</summary>
public sealed record OverlayH2hTally(
    [property: JsonPropertyName("carIndex")] byte CarIndex,
    [property: JsonPropertyName("opponentIndex")] byte OpponentIndex,
    [property: JsonPropertyName("lapsWon")] int LapsWon,
    [property: JsonPropertyName("positionWins")] int PositionWins,
    [property: JsonPropertyName("opponentLapsWon")] int OpponentLapsWon,
    [property: JsonPropertyName("opponentPositionWins")] int OpponentPositionWins);

/// <summary>One-time block-visibility configuration pushed to overlay pages. Keys are the
/// stable block ids of <c>OverlayBlocks</c>, values are the on/off choices from settings.</summary>
public sealed record OverlayConfig(
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("blocks")] IReadOnlyDictionary<string, bool> Blocks);

/// <summary>Player-car strategy advice (from <see cref="ERCTelemetry.Core.Analysis.StrategyAdvice"/>) —
/// tyre laps left from the wear trend, suggested pit lap, fuel-corrected target lap.</summary>
public sealed record OverlayStrategy(
    int TyreLapsLeft,
    ushort SuggestedPitLap,
    uint TargetLapMs);

/// <summary>Fuel-to-finish advice (from <see cref="ERCTelemetry.Core.Analysis.FuelAdvice"/>).
/// ShortfallLaps &gt; 0 = the tank will not cover the finish; negative = spare laps.</summary>
public sealed record OverlayFuel(
    int LapsToGo,
    float FuelLapsRemaining,
    int? ShortfallLaps,
    float LitresToFuel);

/// <summary>Tyre health per car for the heatmap overlay (worst wheel, 0–100).</summary>
public sealed record OverlayTyre(
    byte CarIndex,
    float WearPercent,
    float DamagePercent);

/// <summary>One dot of the ~10 Hz minimap stream. X/Z are world metres; positions carry
/// the latest Motion-packet values (unseen cars are not listed).</summary>
public sealed record OverlayCarPosition(
    byte CarIndex,
    bool IsPlayer,
    float X,
    float Z);

/// <summary>~10 Hz minimap positions for the map.html page.</summary>
public sealed record OverlayMap(
    [property: JsonPropertyName("t")] string Type,
    IReadOnlyList<OverlayCarPosition> Positions);

/// <summary>Circuit outline for the minimap pages: the embedded layout polyline already
/// transformed into game-world metres (Core-side fit — the browser does no math). Sent
/// once when the fit locks and again on every client connect while it is locked; the
/// <c>map</c> message keeps flowing as before, so outline and dots share one frame.
/// Additive message — the protocol version stays 1.</summary>
public sealed record OverlayTrackLayout(
    [property: JsonPropertyName("t")] string Type,
    string Track,
    ulong SessionUid,
    IReadOnlyList<float[]> Points,
    [property: JsonPropertyName("startX")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? StartX = null,
    [property: JsonPropertyName("startZ")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? StartZ = null);

/// <summary>Race-control style occurrence for immediate delivery.</summary>
public sealed record OverlayEventMessage(
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("type")] string EventType,
    string Text,
    byte? CarIndex,
    DateTimeOffset Utc,
    [property: JsonPropertyName("seq")] long Sequence);

/// <summary>One AI-commentator line for immediate delivery to the commentator overlay.
/// <see cref="Sequence"/> lets the page order intermixed commentary + event lines.</summary>
public sealed record OverlayCommentary(
    [property: JsonPropertyName("t")] string Type,
    string Text,
    [property: JsonPropertyName("seq")] long Sequence);

/// <summary>Union of everything a client can send us. <see cref="CarIndex"/> is set only
/// for <c>select</c>; a select without a carIndex clears that client's override.</summary>
public sealed record OverlayClientMessage(string Type, byte? CarIndex);

/// <summary>Tolerant parser for client frames: returns null for anything malformed or an
/// unknown shape — a bad client must never take the server down.</summary>
public static class OverlayClientParser
{
    public static OverlayClientMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("t", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var type = typeEl.GetString()!;
            byte? carIndex = null;
            if (type == OverlayProtocolConstants.SelectType &&
                doc.RootElement.TryGetProperty("carIndex", out var carEl) &&
                carEl.ValueKind == JsonValueKind.Number &&
                carEl.TryGetByte(out var value))
            {
                carIndex = value;
            }

            return new OverlayClientMessage(type, carIndex);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}