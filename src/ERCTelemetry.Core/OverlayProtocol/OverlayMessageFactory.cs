using System.Text.Json;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Tracks;

namespace ERCTelemetry.Core.OverlayProtocol;

/// <summary>Serializes immutable session snapshots into the overlay wire messages
/// (camelCase JSON, enums as member names, times raw in ms so pages format themselves).
/// Pure functions — all throttling/fan-out lives in the App-side hub.</summary>
public static class OverlayMessageFactory
{
    public static string CreateHello(string? scheme = null) =>
        Serialize(new OverlayHello(OverlayProtocolConstants.HelloType, OverlayProtocolConstants.Version, scheme));

    public static string CreateState(TelemetrySnapshot snapshot, string? scheme = null) =>
        Serialize(new OverlayState(
            OverlayProtocolConstants.StateType,
            OverlaySessionOf(snapshot),
            snapshot.Standings.Select(StandingOf).ToArray(),
            snapshot.FinalResults.Select(FinalResultOf).ToArray(),
            (snapshot.Tyres ?? Array.Empty<TyreStatus>()).Select(t => new OverlayTyre(
                t.CarIndex, t.WearPercent, t.DamagePercent)).ToArray(),
            scheme,
            snapshot.Strategy is null
                ? null
                : new OverlayStrategy(
                    snapshot.Strategy.TyreLapsLeft,
                    snapshot.Strategy.SuggestedPitLap,
                    snapshot.Strategy.TargetLapMs),
            H2hOf(snapshot.H2h),
            snapshot.Fuel is null
                ? null
                : new OverlayFuel(
                    snapshot.Fuel.LapsToGo,
                    snapshot.Fuel.FuelLapsRemaining,
                    snapshot.Fuel.ShortfallLaps,
                    snapshot.Fuel.LitresToFuel)));

    /// <summary>Head-to-head duel tallies for the wire; omitted when no duel is being scored.</summary>
    private static OverlayH2hTally[]? H2hOf(IReadOnlyList<H2hTally>? tallies) =>
        tallies is null || tallies.Count == 0
            ? null
            : tallies.Select(t => new OverlayH2hTally(
                t.CarIndex, t.OpponentIndex, t.LapsWon, t.PositionWins,
                t.OpponentLapsWon, t.OpponentPositionWins)).ToArray();

    /// <summary>Block-visibility configuration for overlay pages, straight from the
    /// settings — a pure function of the argument, no snapshot needed.</summary>
    public static string CreateConfig(IReadOnlyDictionary<string, bool> blocks) =>
        Serialize(new OverlayConfig(OverlayProtocolConstants.ConfigType, blocks));

    /// <summary>Minimap frame — cars with no Motion data yet (all-zero slot) are skipped;
    /// the player is marked so the page can highlight it. Null when nothing to send.</summary>
    public static string? CreateMap(TelemetrySnapshot snapshot)
    {
        if (snapshot.Positions is not { } positions)
        {
            return null;
        }

        var list = new List<OverlayCarPosition>(TelemetryConstants.MaxCars);
        for (byte i = 0; i < positions.Count; i++)
        {
            // Zero slots are unreported cars (empty slots arrive zeroed from the game).
            if (positions.X[i] == 0 && positions.Z[i] == 0)
            {
                continue;
            }

            list.Add(new OverlayCarPosition(
                i,
                i == snapshot.Meta?.PlayerCarIndex,
                positions.X[i],
                positions.Z[i]));
        }

        return list.Count == 0
            ? null
            : Serialize(new OverlayMap(OverlayProtocolConstants.MapType, list));
    }

    public static string CreatePlayer(TelemetrySnapshot snapshot, byte? selectedCarIndex = null)
    {
        var (player, rival) = ResolveFrames(snapshot, selectedCarIndex);
        return Serialize(new OverlayPlayer(OverlayProtocolConstants.PlayerType, player, rival));
    }

    /// <summary>Immediate race-control style message; session lifecycle and lap completions
    /// become <c>session-started</c>/<c>session-ended</c>/<c>lap-completed</c> posters.</summary>
    /// <summary>Synthetic events (no store-assigned sequence) get numbers starting above the
    /// event store's range so clients can order intermixed store + synthetic events by seq.</summary>
    private static long _syntheticSequence = 1_000_000;

    private static long NextSyntheticSequence() => Interlocked.Increment(ref _syntheticSequence);

    public static string CreateEvent(StoreEvent storeEvent)
    {
        return storeEvent switch
        {
            RaceControl rc => Serialize(new OverlayEventMessage(
                OverlayProtocolConstants.EventType,
                rc.Event.Type,
                rc.Event.Text,
                rc.Event.CarIndex,
                rc.Event.Utc,
                rc.Event.Sequence)),
            SessionStarted s => Serialize(new OverlayEventMessage(
                OverlayProtocolConstants.EventType,
                "session-started",
                SessionStartedText(s.Meta),
                null,
                DateTimeOffset.UtcNow,
                NextSyntheticSequence())),
            SessionEnded s => Serialize(new OverlayEventMessage(
                OverlayProtocolConstants.EventType,
                "session-ended",
                s.Reason,
                null,
                DateTimeOffset.UtcNow,
                NextSyntheticSequence())),
            LapCompleted l => Serialize(new OverlayEventMessage(
                OverlayProtocolConstants.EventType,
                "lap-completed",
                $"{l.DriverName} · lap {l.LapNumber} · {FormatLapTime(l.LapTimeMs)}",
                l.CarIndex,
                DateTimeOffset.UtcNow,
                NextSyntheticSequence())),
            _ => Serialize(new OverlayEventMessage(
                OverlayProtocolConstants.EventType, "unknown", "Unmapped event", null,
                DateTimeOffset.UtcNow, NextSyntheticSequence())),
        };
    }

    /// <summary>One AI-commentator line → immediate commentary message. Sequence numbers
    /// come from the synthetic range so pages can order commentary vs. store events.</summary>
    public static string CreateCommentary(string text) =>
        Serialize(new OverlayCommentary(OverlayProtocolConstants.CommentaryType, text, NextSyntheticSequence()));

    /// <summary>A client's select override picks which drive frame is shown in the player
    /// slot; only the player and the global rival carry 60 Hz telemetry, so any other index
    /// means "selected car has no frame yet". A select without a frame is not an error.</summary>
    private static (OverlayPlayerFrame? Player, OverlayPlayerFrame? Rival) ResolveFrames(
        TelemetrySnapshot snapshot, byte? selectedCarIndex)
    {
        var player = snapshot.Player is null ? null : PlayerFrameOf(snapshot.Player);
        var rival = snapshot.Rival is null ? null : PlayerFrameOf(snapshot.Rival);

        if (selectedCarIndex is not { } selected)
        {
            return (player, rival);
        }

        if (player is not null && player.CarIndex == selected)
        {
            return (player, rival);
        }

        if (rival is not null && rival.CarIndex == selected)
        {
            // The selected driver takes the player slot; the original player becomes the
            // comparison car so player-card pages keep their player/rival structure.
            return (rival, player);
        }

        return (null, rival);
    }

    private static OverlaySession? OverlaySessionOf(TelemetrySnapshot snapshot)
    {
        if (snapshot.Meta is not { } meta)
        {
            return null;
        }

        return new OverlaySession(
            meta.SessionUid,
            meta.SessionType.ToString(),
            meta.Track.ToString(),
            meta.TotalLaps,
            meta.Weather.ToString(),
            meta.GameMode.ToString(),
            meta.IsNetworkGame,
            meta.PlayerCarIndex,
            meta.AirTemperature,
            meta.TrackTemperature,
            meta.SessionTimeLeft,
            (meta.Forecast ?? Array.Empty<ForecastSample>()).Select(f => new OverlayForecastSample(
                f.TimeOffsetMinutes,
                f.Weather.ToString(),
                f.RainPercent,
                f.AirTemperature,
                f.TrackTemperature)).ToArray(),
            meta.PitWindowIdealLap,
            meta.PitWindowLatestLap,
            (meta.MarshalZones ?? Array.Empty<MarshalZoneStatus>()).Select(z => new OverlayMarshalZone(
                z.ZoneStart, z.Flag)).ToArray());
    }

    private static OverlayStanding StandingOf(StandingsRow row) => new(
        row.Position,
        row.CarIndex,
        row.Name,
        row.Team.Display(),
        row.RaceNumber,
        row.CurrentLapNum,
        row.LastLapTimeMs,
        row.BestLapTimeMs,
        row.GapToLeaderMs,
        row.GapToCarInFrontMs,
        row.PitStatus.ToString(),
        row.Penalties,
        row.ResultStatus.ToString(),
        row.TyreCompound.ToString(),
        row.TyreAgeLaps,
        row.IsPlayer,
        row.Sector1TimeMs,
        row.Sector2TimeMs,
        row.Sector3TimeMs,
        row.GridPosition,
        row.NumPitStops,
        row.CurrentLapTimeMs,
        row.LapValidity,
        row.FuelUsedLastLap,
        row.S1Status,
        row.S2Status,
        row.S3Status,
        row.BestSector1Ms,
        row.BestSector2Ms,
        row.BestSector3Ms,
        row.LapDeltaMs,
        row.PredictedLapMs);

    private static OverlayFinalResult FinalResultOf(FinalResultRow row) => new(
        row.Position,
        row.CarIndex,
        row.Name,
        row.Team.Display(),
        row.RaceNumber,
        row.NumLaps,
        row.GridPosition,
        row.Points,
        row.ResultStatus.ToString(),
        row.BestLapTimeMs,
        row.TotalRaceTimeSeconds,
        row.PenaltiesTime,
        row.NumPenalties);

    private static OverlayPlayerFrame PlayerFrameOf(PlayerFrame frame) => new(
        frame.CarIndex,
        frame.Name,
        frame.Speed,
        frame.Gear,
        frame.Throttle,
        frame.Brake,
        frame.EngineRpm,
        frame.ErsStoreEnergy,
        Math.Clamp(frame.ErsStoreEnergy / TelemetryConstants.MaxErsJoules * 100f, 0f, 100f),
        frame.ErsDeployMode.ToString(),
        frame.FuelInTank,
        frame.FuelRemainingLaps,
        frame.TyreCompound.ToString(),
        frame.TyreAgeLaps,
        frame.DrsAllowed,
        frame.IsDrsOn,
        frame.Steer,
        frame.FrontBrakeBias,
        frame.GLat,
        frame.GLong,
        frame.Wheels?.Pressure,
        frame.Wheels?.SurfaceTemp,
        frame.Wheels?.BrakeTemp);

    private static string SessionStartedText(SessionMeta? meta)
    {
        if (meta is null)
        {
            return "Session started";
        }

        var laps = meta.TotalLaps > 0 ? $"{meta.TotalLaps} laps" : FormatClock(meta.SessionTimeLeft);
        return $"{meta.SessionType} at {meta.Track} · {laps}";
    }

    /// <summary>F1-style lap time: 1:23.456. Empty string for a missing (0) time.</summary>
    public static string FormatLapTime(uint ms) => ms == 0
        ? string.Empty
        : $"{ms / 60000}:{ms % 60000 / 1000:00}.{ms % 1000:000}";

    private static string FormatClock(ushort seconds) =>
        $"{seconds / 60:0}:{seconds % 60:00}";

    /// <summary>Circuit outline for the minimap pages: transforms the embedded layout
    /// polyline into game-world metres via the fitted similarity (the browser only draws).
    /// Start/finish included when known; null start coordinates are omitted on the wire.</summary>
    public static string CreateTrackLayout(OverlayTrackLayout layout) =>
        Serialize(layout);

    /// <summary>Builds the wire record for one fitted layout: every layout point mapped
    /// through the fit, in draw order. Null when any input is missing.</summary>
    public static OverlayTrackLayout? BuildTrackLayout(
        string trackEnumName,
        ulong sessionUid,
        TrackLayout layout,
        TrackFitResult fit,
        (float X, float Z)? startFinish)
    {
        var points = new List<float[]>(layout.PointCount);
        for (var i = 0; i < layout.PointCount; i++)
        {
            var (wx, wz) = fit.ToWorld(layout.X[i], layout.Y[i]);
            points.Add([wx, wz]);
        }

        return new OverlayTrackLayout(
            OverlayProtocolConstants.TrackLayoutType,
            trackEnumName,
            sessionUid,
            points,
            startFinish?.X,
            startFinish?.Z);
    }

    private static string Serialize<T>(T message) =>
        JsonSerializer.Serialize(message, OverlayJson.Options);
}