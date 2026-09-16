using F1Game.UDP.Enums;
using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Packets;
using ERCTelemetry.Core;

namespace ERCTelemetry.Core.Session;

/// <summary>Single-writer aggregator over the telemetry stream. Apply() is called by one
/// consumer task for every packet; BuildSnapshot() projects the state into an immutable
/// <see cref="TelemetrySnapshot"/> for all consumers.</summary>
public sealed class SessionStateStore
{
    private const int RecentEventsInSnapshot = 40;

    private readonly DriverEntry?[] _drivers = new DriverEntry?[TelemetryConstants.MaxCars];
    private readonly CarTiming?[] _timings = new CarTiming?[TelemetryConstants.MaxCars];
    private readonly CarCondition?[] _conditions = new CarCondition?[TelemetryConstants.MaxCars];
    private readonly CarTelemetry?[] _telemetry = new CarTelemetry?[TelemetryConstants.MaxCars];
    private readonly TyreStatus?[] _tyreHealth = new TyreStatus?[TelemetryConstants.MaxCars];
    private readonly uint[] _bestLapMs = new uint[TelemetryConstants.MaxCars];
    private readonly byte[] _lastLapNum = new byte[TelemetryConstants.MaxCars];
    private readonly bool[] _lapAnnounced = new bool[TelemetryConstants.MaxCars];
    private readonly float[] _fuelUsedLastLap = new float[TelemetryConstants.MaxCars];

    private readonly EventFeed _events = new();
    private SessionMeta? _meta;
    private IReadOnlyList<FinalResultRow> _finalResults = Array.Empty<FinalResultRow>();
    private long _frameVersion;
    private bool _finalized;
    private DateTimeOffset _lastPacketUtc = DateTimeOffset.MinValue;
    private byte? _rivalOverride;
    private readonly float[] _posX = new float[TelemetryConstants.MaxCars];
    private readonly float[] _posZ = new float[TelemetryConstants.MaxCars];
    private readonly float[] _fwdX = new float[TelemetryConstants.MaxCars];
    private readonly float[] _fwdZ = new float[TelemetryConstants.MaxCars];
    private byte _posCount;
    private IReadOnlyList<ForecastSample> _forecast = Array.Empty<ForecastSample>();
    private readonly CarDamageStatus?[] _damages = new CarDamageStatus?[TelemetryConstants.MaxCars];
    private TyreSetEntry[]? _tyreSets;
    private IReadOnlyList<MarshalZoneStatus> _marshalZones = Array.Empty<MarshalZoneStatus>();
    private readonly float[] _fuelAtLapStart = new float[TelemetryConstants.MaxCars];
    private readonly float[] _ersAtLapStart = new float[TelemetryConstants.MaxCars];
    private readonly float[] _ersUsedLastLap = new float[TelemetryConstants.MaxCars];
    private readonly bool[] _ersSeedValid = new bool[TelemetryConstants.MaxCars];
    private readonly byte[] _cornerCuttingWarnings = new byte[TelemetryConstants.MaxCars];
    private readonly byte[] _totalWarnings = new byte[TelemetryConstants.MaxCars];
    private readonly bool[] _warningSeedValid = new bool[TelemetryConstants.MaxCars];
    private readonly float[] _steer = new float[TelemetryConstants.MaxCars];
    private readonly float[] _gLat = new float[TelemetryConstants.MaxCars];
    private readonly float[] _gLong = new float[TelemetryConstants.MaxCars];
    private readonly byte[] _brakeBias = new byte[TelemetryConstants.MaxCars];
    private readonly float[,] _pressure = new float[TelemetryConstants.MaxCars, 4];
    private readonly float[,] _surfaceTemp = new float[TelemetryConstants.MaxCars, 4];
    private readonly float[,] _brakeTemp = new float[TelemetryConstants.MaxCars, 4];
    private readonly ushort[] _bestSectorMs = new ushort[TelemetryConstants.MaxCars * 3];
    private readonly ushort[] _sessionBestSectorMs = new ushort[3];
    private readonly Dictionary<int, (int LapsA, int PosA, int LapsB, int PosB)> _h2hPairs = new();
    private readonly Dictionary<int, (uint LapTimeMs, byte Position)>?[] _h2hLaps =
        new Dictionary<int, (uint, byte)>?[TelemetryConstants.MaxCars];
    private int _h2hVersion;
    private (int Version, IReadOnlyList<H2hTally> Tallies)? _h2hBuilt;
    private readonly LapTraceRecorder _traceRecorder = new();
    private readonly ActualCompound?[] _wearStintCompound = new ActualCompound?[TelemetryConstants.MaxCars];
    private readonly float[] _wearBaselineWear = new float[TelemetryConstants.MaxCars];
    private readonly byte[] _wearBaselineLap = new byte[TelemetryConstants.MaxCars];
    private IReadOnlyDictionary<string, string>? _nameOverrides;
    private readonly MotionSummaryRecorder _motionRecorder = new();
    private readonly byte[] _historyLapsEmitted = new byte[TelemetryConstants.MaxCars];
    private readonly byte[] _historyStintCount = new byte[TelemetryConstants.MaxCars];
    private CarSetupSnapshot? _lastSetup;
    private sbyte _lapPositionsStartLap = -1;
    private readonly byte[] _activeAeroMode = new byte[TelemetryConstants.MaxCars];
    private readonly bool[] _overtakeUsedThisLap = new bool[TelemetryConstants.MaxCars];
    private readonly bool[] _wrongWayThisLap = new bool[TelemetryConstants.MaxCars];
    private readonly int[] _damageFingerprint = new int[TelemetryConstants.MaxCars];
    private readonly bool[] _damageSeen = new bool[TelemetryConstants.MaxCars];

    public SessionStateStore()
    {
        // 255 = "no CarTelemetry2 packet seen" (0 is a valid aero mode).
        Array.Fill(_activeAeroMode, (byte)255);
    }

    /// <summary>All-time best lap of the current track (seeded from the DB by the App
    /// layer at session start). 0 = unknown. The store announces the player beating it
    /// as a regular race-control event and adopts the new value.</summary>
    public uint AllTimeBestLapMs;

    /// <summary>Explicit rival selection (e.g. the user's teammate). Null = auto (teammate).
    /// Safe to set from the UI thread: only the byte? is written here; the next
    /// BuildSnapshot on the aggregator picks it up.</summary>
    public byte? RivalOverride
    {
        get => _rivalOverride;
        set => _rivalOverride = value is >= TelemetryConstants.MaxCars ? null : value;
    }

    /// <summary>Display-name overrides keyed by the game-provided participant name
    /// (null/empty = no overrides). Applies immediately to already-known drivers so the
    /// UI shows the new names without waiting for the next Participants packet; clearing
    /// restores the original names. Safe to set from the UI thread like
    /// <see cref="RivalOverride"/>.</summary>
    public void SetNameOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        _nameOverrides = overrides;
        for (byte i = 0; i < TelemetryConstants.MaxCars; i++)
        {
            if (_drivers[i] is { } driver)
            {
                // Rebuild from the game name first so clearing restores the original.
                var reset = driver.GameName is { Length: > 0 } gameName
                    ? driver with { Name = gameName }
                    : driver;
                _drivers[i] = ResolveDriverName(reset);
            }
        }
    }

    public SessionMeta? Meta => _meta;

    /// <summary>Applies one packet and returns the events it caused. Must be called from
    /// a single consumer only.</summary>
    public IReadOnlyList<StoreEvent> Apply(UnionPacket packet)
    {
        _lastPacketUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<StoreEvent>? events = null;
        switch (packet.PacketType)
        {
            case PacketType.Session when packet.TryGetSessionDataPacket(out var session):
                events = ApplySession(session);
                break;
            case PacketType.Participants when packet.TryGetParticipantsDataPacket(out var participants):
                ApplyParticipants(participants);
                break;
            case PacketType.LobbyInfo when packet.TryGetLobbyInfoDataPacket(out var lobby):
                ApplyLobbyInfo(lobby);
                break;
            case PacketType.LapData when packet.TryGetLapDataPacket(out var lapData):
                events = ApplyLapData(lapData);
                break;
            case PacketType.Motion when packet.TryGetMotionDataPacket(out var motion):
                ApplyMotion(motion);
                break;
            case PacketType.CarStatus when packet.TryGetCarStatusDataPacket(out var status):
                ApplyCarStatus(status);
                break;
            case PacketType.CarDamage when packet.TryGetCarDamageDataPacket(out var damage):
                events = ApplyCarDamage(damage);
                break;
            case PacketType.CarTelemetry when packet.TryGetCarTelemetryDataPacket(out var telemetry):
                ApplyCarTelemetry(telemetry);
                break;
            case PacketType.TyreSets when packet.TryGetTyreSetsDataPacket(out var tyreSets):
                ApplyTyreSets(tyreSets);
                break;
            case PacketType.Event when packet.TryGetEventDataPacket(out var eventData):
                events = ApplyEvent(eventData);
                break;
            case PacketType.FinalClassification when packet.TryGetFinalClassificationDataPacket(out var final):
                events = ApplyFinalClassification(final);
                break;
            case PacketType.SessionHistory when packet.TryGetSessionHistoryDataPacket(out var history):
                events = ApplySessionHistory(history);
                break;
            case PacketType.CarSetups when packet.TryGetCarSetupDataPacket(out var setups):
                events = ApplyCarSetups(setups);
                break;
            case PacketType.LapPositions when packet.TryGetLapPositionsDataPacket(out var lapPositions):
                events = ApplyLapPositions(lapPositions);
                break;
            case PacketType.CarTelemetry2 when packet.TryGetCarTelemetry2DataPacket(out var telemetry2):
                ApplyCarTelemetry2(telemetry2);
                break;
            case PacketType.MotionEx when packet.TryGetMotionExDataPacket(out var motionEx):
                ApplyMotionEx(motionEx);
                break;
        }

        return events ?? Array.Empty<StoreEvent>();
    }

    /// <summary>Returns a SessionEnded event when the session has been idle (no packets
    /// received) for the given timeout — the game closed or the user left the session
    /// without a FinalClassification. Called by the aggregator when the packet stream
    /// goes quiet. Null when there is no open session or it is already finalized.</summary>
    public StoreEvent? CheckIdle(DateTimeOffset now, TimeSpan idleTimeout)
    {
        if (_meta is null || _finalized || now - _lastPacketUtc < idleTimeout)
        {
            return null;
        }

        _finalized = true;
        return new SessionEnded(_meta.SessionUid, "Idle", _finalResults);
    }

    private List<StoreEvent> ApplySession(SessionDataPacket packet)
    {
        var events = new List<StoreEvent>();
        var uid = packet.Header.SessionUID;

        if (_meta is not null && _meta.SessionUid != uid)
        {
            events.Add(new SessionEnded(_meta.SessionUid, "SessionChange", _finalResults));
            ResetState();
        }

        if (_meta is null || _meta.SessionUid != uid)
        {
            _forecast = BuildForecast(packet);
            _marshalZones = BuildMarshalZones(packet);
            _meta = new SessionMeta(
                uid,
                packet.SessionType,
                packet.Track,
                packet.TotalLaps,
                packet.TrackLength,
                packet.IsNetworkGame,
                packet.Header.PlayerCarIndex,
                packet.GameMode,
                packet.Weather,
                packet.TrackTemperature,
                packet.AirTemperature,
                packet.SessionTimeLeft,
                _forecast,
                (int)packet.SafetyCarStatus,
                packet.PitStopWindowIdealLap,
                packet.PitStopWindowLatestLap,
                _marshalZones,
                (ushort)packet.TimeOfDay,
                packet.Formula,
                packet.PitSpeedLimit,
                packet.Sector2LapDistanceStart,
                packet.Sector3LapDistanceStart,
                packet.NumDrsZones,
                BuildDrsZones(packet),
                packet.RuleSet,
                packet.SessionDuration);
            _finalized = false;
            events.Add(new SessionStarted(_meta));
        }
        else
        {
            _forecast = BuildForecast(packet);
            _marshalZones = BuildMarshalZones(packet);
            _meta = _meta with
            {
                SessionTimeLeft = packet.SessionTimeLeft,
                Weather = packet.Weather,
                PlayerCarIndex = packet.Header.PlayerCarIndex,
                Forecast = _forecast,
                SafetyCarStatus = (int)packet.SafetyCarStatus,
                PitWindowIdealLap = packet.PitStopWindowIdealLap,
                PitWindowLatestLap = packet.PitStopWindowLatestLap,
                MarshalZones = _marshalZones,
                TimeOfDay = (ushort)packet.TimeOfDay,
                Formula = packet.Formula,
                PitSpeedLimit = packet.PitSpeedLimit,
                Sector2LapDistanceStart = packet.Sector2LapDistanceStart,
                Sector3LapDistanceStart = packet.Sector3LapDistanceStart,
                NumDrsZones = packet.NumDrsZones,
                DrsZones = packet.NumDrsZones > 0 ? BuildDrsZones(packet) : null,
                RuleSet = packet.RuleSet,
                SessionDuration = packet.SessionDuration,
            };

            // A session finalized by the idle timeout (pause / lobby) can resume with the
            // same UID — re-open it so laps and results keep flowing. The re-emitted
            // SessionStarted makes the persistence pump re-open the DB row.
            if (_finalized)
            {
                _finalized = false;
                _finalResults = Array.Empty<FinalResultRow>();
                events.Add(new SessionStarted(_meta));
            }
        }

        // Participants can arrive before the first Session packet (online lobbies),
        // so IsPlayer flags are recomputed whenever the player index may have changed.
        RefreshPlayerFlags(_meta.PlayerCarIndex);
        return events;
    }

    private void RefreshPlayerFlags(byte playerCarIndex)
    {
        for (byte i = 0; i < TelemetryConstants.MaxCars; i++)
        {
            if (_drivers[i] is { } driver && driver.IsPlayer != (i == playerCarIndex))
            {
                _drivers[i] = driver with { IsPlayer = i == playerCarIndex };
            }
        }
    }

    private IReadOnlyList<StoreEvent>? ApplyParticipants(ParticipantsDataPacket packet)
    {
        List<StoreEvent>? events = null;
        var rosterChanged = false;
        var participants = packet.Participants.AsSpan();
        var active = Math.Min(packet.NumActiveCars, participants.Length);
        for (byte i = 0; i < active; i++)
        {
            var p = participants[i];
            var entry = ResolveDriverName(new DriverEntry(
                i, p.Name, p.Team, p.RaceNumber, p.IsAiControlled,
                i == _meta?.PlayerCarIndex, p.IsTelemetryPublic, p.Name));
            if (!Equals(entry, _drivers[i]))
            {
                _drivers[i] = entry;
                rosterChanged = true;
            }
        }

        for (var i = active; i < TelemetryConstants.MaxCars; i++)
        {
            if (_drivers[i] is not null)
            {
                _drivers[i] = null;
                rosterChanged = true;
            }
        }

        if (rosterChanged)
        {
            events = new List<StoreEvent> { new DriversRegistered(SnapshotDrivers()) };
        }

        return events;
    }

    /// <summary>Fresh roster list for <see cref="DriversRegistered"/> events: the events
    /// list reaches consumers as-is, so a new list avoids shared-mutable-list hazards.</summary>
    private List<DriverEntry> SnapshotDrivers()
    {
        var list = new List<DriverEntry>(TelemetryConstants.MaxCars);
        for (byte i = 0; i < TelemetryConstants.MaxCars; i++)
        {
            if (_drivers[i] is { } d) list.Add(d);
        }
        return list;
    }

    private IReadOnlyList<StoreEvent>? ApplyLobbyInfo(LobbyInfoDataPacket packet)
    {
        List<StoreEvent>? events = null;
        var rosterChanged = false;
        // Online lobbies can fill Participants late; use lobby names as a fallback.
        var players = packet.LobbyPlayers.AsSpan();
        var active = Math.Min(packet.NumPlayers, players.Length);
        for (byte i = 0; i < active; i++)
        {
            if (_drivers[i] is null)
            {
                var p = players[i];
                _drivers[i] = ResolveDriverName(new DriverEntry(i, p.Name, p.Team, p.CarNumber, p.IsAiControlled,
                    i == _meta?.PlayerCarIndex, true, p.Name));
                rosterChanged = true;
            }
        }

        if (rosterChanged)
        {
            events = new List<StoreEvent> { new DriversRegistered(SnapshotDrivers()) };
        }

        return events;
    }

    /// <summary>Per-car lap history from the SessionHistory packet (~2 Hz per car): emits
    /// only the laps persistence has not seen yet, skips the in-progress partial row, and
    /// re-sends the stint list whenever its count changed. Sector times combine the
    /// packet's minutes part (the ms fields only carry the sub-minute remainder).</summary>
    private IReadOnlyList<StoreEvent>? ApplySessionHistory(SessionHistoryDataPacket packet)
    {
        var carIndex = packet.CarIndex;
        if (carIndex >= TelemetryConstants.MaxCars)
        {
            return null;
        }

        var laps = packet.LapHistoryData.AsSpan();
        var emitted = _historyLapsEmitted[carIndex];

        // The trailing entry can be the lap currently in progress (LapTimeInMS == 0) —
        // skip it and keep the counter where it is, so the completed row is emitted later.
        var usable = Math.Min(packet.NumLaps, laps.Length);
        while (usable > emitted && laps[usable - 1].LapTimeInMS == 0)
        {
            usable--;
        }

        var stintCount = packet.NumTyreStints;
        var stintsChanged = stintCount != _historyStintCount[carIndex];

        if (usable <= emitted && !stintsChanged)
        {
            return null;
        }

        var newLaps = new List<LapHistoryRow>(Math.Max(0, usable - emitted));
        for (var i = emitted; i < usable; i++)
        {
            var l = laps[i];
            newLaps.Add(new LapHistoryRow(
                i + 1,
                l.LapTimeInMS,
                (uint)l.Sector1TimeMinutes * 60000u + l.Sector1TimeInMS,
                (uint)l.Sector2TimeMinutes * 60000u + l.Sector2TimeInMS,
                (uint)l.Sector3TimeMinutes * 60000u + l.Sector3TimeInMS,
                (ushort)l.LapValidBitFlags));
        }

        _historyLapsEmitted[carIndex] = (byte)usable;
        _historyStintCount[carIndex] = stintCount;

        List<StoreEvent> events = new(1);
        events.Add(new LapHistoryUpdated(
            carIndex, usable, newLaps,
            BuildStints(packet, stintCount),
            packet.BestLapTimeLapNum, packet.BestSector1LapNum,
            packet.BestSector2LapNum, packet.BestSector3LapNum));
        return events;
    }

    /// <summary>Complete stint list (up to 8) — EndLap 255 means the stint is ongoing.</summary>
    private static StoredTyreStint[] BuildStints(SessionHistoryDataPacket packet, byte stintCount)
    {
        var stints = packet.TyreStintsHistoryData.AsSpan();
        var n = Math.Min((int)stintCount, stints.Length);
        var result = new StoredTyreStint[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = new StoredTyreStint(
                (byte)i, stints[i].TyreActualCompound, stints[i].TyreVisualCompound, stints[i].EndLap);
        }

        return result;
    }

    /// <summary>Car setups: only the player's slot is meaningful (multiplayer reports
    /// rival setups as empty) — deduplicated so persistence gets one event per actual
    /// setup change, not per packet.</summary>
    private IReadOnlyList<StoreEvent>? ApplyCarSetups(CarSetupDataPacket packet)
    {
        var player = packet.Header.PlayerCarIndex;
        var cars = packet.CarSetups.AsSpan();
        if (player >= cars.Length)
        {
            return null;
        }

        var s = cars[player];
        var snapshot = new CarSetupSnapshot(
            s.FrontWing, s.RearWing, s.OnThrottle, s.OffThrottle,
            s.FrontCamber, s.RearCamber, s.FrontToe, s.RearToe,
            s.FrontSuspension, s.RearSuspension, s.FrontAntiRollBar, s.RearAntiRollBar,
            s.FrontSuspensionHeight, s.RearSuspensionHeight, s.BrakePressure, s.BrakeBias,
            s.EngineBraking, s.Ballast, s.FuelLoad,
            new[]
            {
                s.TyresPressure.FrontLeft, s.TyresPressure.FrontRight,
                s.TyresPressure.RearLeft, s.TyresPressure.RearRight,
            },
            (byte)Math.Round(packet.NextFrontWingValue));
        if (snapshot.Equals(_lastSetup))
        {
            return null;
        }

        _lastSetup = snapshot;
        return new StoreEvent[] { new SetupCaptured(player, snapshot) };
    }

    /// <summary>Lap-by-lap position history (up to 50 laps × 24 car slots, row-major
    /// lap*24+car in persistence). The packet re-sends its current window constantly;
    /// a chunk is only emitted when the window's StartingLap advances.</summary>
    private IReadOnlyList<StoreEvent>? ApplyLapPositions(LapPositionsDataPacket packet)
    {
        if (packet.StartingLap == _lapPositionsStartLap)
        {
            return null;
        }

        _lapPositionsStartLap = (sbyte)packet.StartingLap;
        var laps = packet.PositionsPerLapForVehicle.AsSpan();
        var count = Math.Min((int)packet.NumberOfLaps, laps.Length);
        var perLap = new byte[count][];
        for (var lap = 0; lap < count; lap++)
        {
            var row = laps[lap].AsSpan();
            var cars = new byte[Math.Min(row.Length, TelemetryConstants.MaxCars)];
            for (var car = 0; car < cars.Length; car++)
            {
                cars[car] = row[car];
            }

            perLap[lap] = cars;
        }

        return new StoreEvent[] { new LapPositionsChunk(packet.StartingLap, (byte)count, perLap) };
    }

    /// <summary>2026-regulation car flags (CarTelemetry2 packet): active aero mode and
    /// per-lap overtake/wrong-way behaviour, folded into the next LapCompleted for the car.</summary>
    private void ApplyCarTelemetry2(CarTelemetry2DataPacket packet)
    {
        var cars = packet.CarTelemetry2Data.AsSpan();
        var carCount = Math.Min(cars.Length, TelemetryConstants.MaxCars);
        for (byte i = 0; i < carCount; i++)
        {
            var t = cars[i];
            if (t.ActiveAeroAvailable)
            {
                _activeAeroMode[i] = t.ActiveAeroMode;
            }

            if (t.OvertakeActive)
            {
                _overtakeUsedThisLap[i] = true;
            }

            if (t.IsDrivingWrongWay)
            {
                _wrongWayThisLap[i] = true;
            }
        }
    }

    /// <summary>High-rate player-only motion data (MotionEx): folded into the per-lap
    /// motion summary window. g-forces come from the Motion packet (updated first in each
    /// frame cycle, so they are current).</summary>
    private void ApplyMotionEx(MotionExDataPacket packet)
    {
        var player = _meta?.PlayerCarIndex ?? 0;
        if (player >= TelemetryConstants.MaxCars)
        {
            return; // game sends 255 when no player is active (lobby/spectating)
        }

        _motionRecorder.Sample(
            packet.WheelSlipRatio.FrontLeft, packet.WheelSlipRatio.FrontRight,
            packet.WheelSlipRatio.RearLeft, packet.WheelSlipRatio.RearRight,
            packet.FrontAeroHeight, packet.RearAeroHeight,
            _gLat[player], _gLong[player],
            _timings[player]?.CurrentLapNum ?? 0);
    }

    /// <summary>DRS zones from the Session packet, capped at the reported count.</summary>
    private static List<DrsZoneInfo> BuildDrsZones(SessionDataPacket packet)
    {
        var zones = packet.DrsZones.AsSpan();
        var n = Math.Min((int)packet.NumDrsZones, zones.Length);
        var result = new List<DrsZoneInfo>(n);
        for (var i = 0; i < n; i++)
        {
            result.Add(new DrsZoneInfo(zones[i].ZoneStart, zones[i].ZoneEnd));
        }

        return result;
    }

    /// <summary>Motion packets arrive ~360 Hz; only X/Z world positions (metres) are kept
    /// for the minimap — pure writes into pre-allocated arrays, no allocation.</summary>
    private void ApplyMotion(MotionDataPacket packet)
    {
        var cars = packet.CarMotionData.AsSpan();
        var carCount = Math.Min(cars.Length, TelemetryConstants.MaxCars);
        for (byte i = 0; i < carCount; i++)
        {
            _posX[i] = cars[i].WorldPositionX;
            _posZ[i] = cars[i].WorldPositionZ;
            _fwdX[i] = cars[i].WorldForwardDirX;
            _fwdZ[i] = cars[i].WorldForwardDirZ;
            _gLat[i] = cars[i].GForceLateral / 1000f; // quantised int16: ÷1000 → g
            _gLong[i] = cars[i].GForceLongitudinal / 1000f;
        }

        _posCount = (byte)carCount;
    }

    private static readonly ForecastSample[] EmptyForecast = Array.Empty<ForecastSample>();

    /// <summary>Maps the Session packet's forecast samples. The samples repeat with every
    /// Session packet (~every 1–2 s); to keep them out of overlay change detection the
    /// SAME list reference is returned while the content is unchanged, and a fresh array
    /// only when a value changed (record equality compares arrays by reference).</summary>
    private IReadOnlyList<ForecastSample> BuildForecast(SessionDataPacket packet)
    {
        var count = Math.Min(packet.NumWeatherForecastSamples, packet.WeatherForecastSamples.AsSpan().Length);
        if (count == 0)
        {
            return EmptyForecast;
        }

        var samples = new ForecastSample[count];
        for (var i = 0; i < count; i++)
        {
            var s = packet.WeatherForecastSamples.AsSpan()[i];
            samples[i] = new ForecastSample(s.TimeOffset, s.Weather, s.RainPercentage, s.TrackTemperature, s.AirTemperature);
        }

        if (_forecast.Count == count)
        {
            var equal = true;
            for (var i = 0; i < count && equal; i++)
            {
                equal = _forecast[i] == samples[i];
            }

            if (equal)
            {
                return _forecast;
            }
        }

        return samples;
    }

    private static readonly MarshalZoneStatus[] EmptyMarshalZones = Array.Empty<MarshalZoneStatus>();

    /// <summary>Maps the Session packet's marshal zones with their current flag. The zones
    /// repeat with every Session packet; the SAME list reference is returned while the
    /// content is unchanged (keeps them out of overlay change detection), like BuildForecast.</summary>
    private IReadOnlyList<MarshalZoneStatus> BuildMarshalZones(SessionDataPacket packet)
    {
        var count = Math.Min(packet.NumMarshalZones, packet.MarshalZones.AsSpan().Length);
        if (count == 0)
        {
            return EmptyMarshalZones;
        }

        MarshalZoneStatus[] zones = new MarshalZoneStatus[count];
        for (var i = 0; i < count; i++)
        {
            var z = packet.MarshalZones.AsSpan()[i];
            zones[i] = new MarshalZoneStatus(z.ZoneStart, (int)z.ZoneFlag);
        }

        if (_marshalZones.Count == count)
        {
            var equal = true;
            for (var i = 0; i < count && equal; i++)
            {
                equal = _marshalZones[i] == zones[i];
            }

            if (equal)
            {
                return _marshalZones;
            }
        }

        return zones;
    }

    /// <summary>Applies a user display-name override (matched on the game-provided name,
    /// case-insensitive) while keeping the original in <see cref="DriverEntry.GameName"/>.</summary>
    private DriverEntry ResolveDriverName(DriverEntry driver)
    {
        if (_nameOverrides is not null &&
            driver.GameName is { } gameName &&
            gameName.Length > 0 &&
            _nameOverrides.TryGetValue(gameName, out var displayName) &&
            displayName.Length > 0)
        {
            return driver with { Name = displayName };
        }

        return driver;
    }

    private IReadOnlyList<StoreEvent> ApplyLapData(LapDataPacket packet)
    {
        List<StoreEvent>? events = null;
        var laps = packet.LapData.AsSpan();
        var carCount = Math.Min(laps.Length, TelemetryConstants.MaxCars); // arrays are Array24 (24 slots)

        // Position snapshot for overtake (adjacent swap) detection after this packet.
        Span<byte> positionsBefore = stackalloc byte[TelemetryConstants.MaxCars];
        for (var i = 0; i < carCount; i++)
        {
            positionsBefore[i] = _timings[i]?.Position ?? 0;
        }
        for (byte i = 0; i < carCount; i++)
        {
            var lap = laps[i];

            // Unused car slots (online lobbies) arrive all-zero; keep them out of standings.
            if (lap.CarPosition == 0 && lap.CurrentLapNum == 0 &&
                lap.LastLapTimeInMS == 0 && lap.CurrentLapTimeInMS == 0)
            {
                _timings[i] = null;
                _warningSeedValid[i] = false; // re-seed if this car rejoins later
                continue;
            }

            // Sector bests are tracked from the same values that are displayed (S3 is the
            // derived Last − S1 − S2 everywhere), so the green/purple markers always match
            // what the reader sees in the grid.
            var derivedS3 = StandingsCalculator.DeriveSector3(
                lap.LastLapTimeInMS, lap.Sector1TimeInMS, lap.Sector2TimeInMS);
            var best1 = StandingsCalculator.TrackBest(ref _bestSectorMs[i * 3], lap.Sector1TimeInMS);
            var best2 = StandingsCalculator.TrackBest(ref _bestSectorMs[i * 3 + 1], lap.Sector2TimeInMS);
            var best3 = StandingsCalculator.TrackBest(ref _bestSectorMs[i * 3 + 2], derivedS3);
            StandingsCalculator.TrackBest(ref _sessionBestSectorMs[0], lap.Sector1TimeInMS);
            StandingsCalculator.TrackBest(ref _sessionBestSectorMs[1], lap.Sector2TimeInMS);
            StandingsCalculator.TrackBest(ref _sessionBestSectorMs[2], derivedS3);

            _timings[i] = new CarTiming(
                i, lap.CarPosition, lap.CurrentLapNum, lap.LapDistance,
                lap.CurrentLapTimeInMS, lap.LastLapTimeInMS,
                lap.Sector1TimeInMS, lap.Sector2TimeInMS,
                lap.DeltaToRaceLeaderInMS, lap.DeltaToCarInFrontInMS,
                lap.PitStatus, lap.NumPitStops, lap.Penalties,
                lap.DriverStatus, lap.ResultStatus, lap.GridPosition,
                (byte)(lap.IsCurrentLapInvalid ? 1 : 0), _fuelUsedLastLap[i],
                _ersUsedLastLap[i], best1, best2, best3, (byte)lap.Sector);

            // Warning counters (league requirement: per-lap/per-corner review of
            // Verwarnungen and Strafen). The game only exposes cumulative counters, so
            // an increase is a fresh warning event while a decrease is a flashback
            // rewind — never an event. Seeded once per (re)join so a mid-session join
            // does not replay the whole pre-join history as fresh warnings.
            if (_warningSeedValid[i])
            {
                if (lap.CornerCuttingWarnings > _cornerCuttingWarnings[i])
                {
                    events ??= new List<StoreEvent>();
                    var cutEntry = _events.Add(new RaceEventEntry(
                        DateTimeOffset.UtcNow, "CornerCuttingWarning", i,
                        $"{_drivers[i]?.Name ?? $"Car {i + 1}"}: Abkürz-Verwarnung",
                        0, lap.CurrentLapNum, LapDistance: lap.LapDistance));
                    events.Add(new RaceControl(cutEntry));
                }

                if (lap.TotalWarnings > _totalWarnings[i])
                {
                    events ??= new List<StoreEvent>();
                    var warningEntry = _events.Add(new RaceEventEntry(
                        DateTimeOffset.UtcNow, "Warning", i,
                        $"{_drivers[i]?.Name ?? $"Car {i + 1}"}: Verwarnung",
                        0, lap.CurrentLapNum, LapDistance: lap.LapDistance));
                    events.Add(new RaceControl(warningEntry));
                }
            }

            _cornerCuttingWarnings[i] = lap.CornerCuttingWarnings;
            _totalWarnings[i] = lap.TotalWarnings;
            _warningSeedValid[i] = true;

            // Lap-number tracking. A flashback rewinds CurrentLapNum; we deliberately do
            // NOT lower _lastLapNum so the re-crossing of the same lap is recognized as a
            // replay (lap number never moves forward past _lastLapNum again) and the
            // completion is announced only once, with no best-lap pollution from replays.
            // Completion is evaluated BEFORE the counter advances + reseeds, so the fuel
            // reference of the lap that just finished survives the crossing packet.
            if (lap.CurrentLapNum < _lastLapNum[i])
            {
                _lapAnnounced[i] = true;
            }
            if (lap.CurrentLapNum > _lastLapNum[i])
            {
                _lapAnnounced[i] = false;
            }

            // Forward lap-number jump (a car joining mid-race reports e.g. lap 15 with a
            // carried-over LastLapTimeInMS on its first packet): mirror the rewind handling
            // — mark the lap as seen so the carried-over time is never announced, while the
            // advance + fuel reseed below still runs like a normal lap increment.
            if (lap.CurrentLapNum - _lastLapNum[i] > 1)
            {
                _lapAnnounced[i] = true;
            }

            // Lap completion: a lap time is available for a lap not yet announced.
            if (!_lapAnnounced[i] && lap.LastLapTimeInMS > 0)
            {
                _lapAnnounced[i] = true;

                // Consumption of the just-completed lap, frozen from the fuel reference
                // seeded at the previous lap increment (0 when no CarStatus was seen yet).
                var fuelNow = _conditions[i]?.FuelInTank ?? 0f;
                _fuelUsedLastLap[i] = _fuelAtLapStart[i] > fuelNow
                    ? _fuelAtLapStart[i] - fuelNow
                    : 0f;
                if (_timings[i] is { } timing)
                {
                    _timings[i] = timing with { FuelUsedLastLap = _fuelUsedLastLap[i] };
                }

                // Net ERS energy over the just-finished lap: deploy drains the store,
                // harvesting refills it — the signed difference is the lap's balance.
                var ersNow = _conditions[i]?.ErsStoreEnergy ?? 0f;
                _ersUsedLastLap[i] = _ersSeedValid[i] ? _ersAtLapStart[i] - ersNow : 0f;
                _ersSeedValid[i] = false;
                if (_timings[i] is { } timingErs)
                {
                    _timings[i] = timingErs with { ErsUsedLastLapJ = _ersUsedLastLap[i] };
                }

                events ??= new List<StoreEvent>();
                var completedLap = (byte)Math.Max(1, lap.CurrentLapNum - 1);
                events.Add(new LapCompleted(
                    i, _drivers[i]?.Name ?? $"Car {i + 1}",
                    (byte)Math.Max(1, lap.CurrentLapNum - 1),
                    lap.LastLapTimeInMS, lap.Sector1TimeInMS, lap.Sector2TimeInMS,
                    _conditions[i]?.TyreCompound ?? (ActualCompound)0,
                    _conditions[i]?.TyreAgeLaps ?? 0,
                    lap.CarPosition,
                    _ersUsedLastLap[i],
                    _fuelUsedLastLap[i],
                    lap.PitStatus,
                    lap.NumPitStops,
                    lap.Penalties,
                    lap.DriverStatus,
                    lap.ResultStatus,
                    lap.GridPosition,
                    _activeAeroMode[i],
                    _overtakeUsedThisLap[i],
                    _wrongWayThisLap[i]));

                // Per-lap motion aggregate (player only): the just-finished lap's window
                // leaves the recorder here, before any MotionEx packet of the new lap has
                // been applied — the window is still the lap that just finished.
                if (i == (_meta?.PlayerCarIndex ?? 0) && _motionRecorder.TakeSummary(completedLap) is { } summary)
                {
                    events?.Add(new LapMotionSummary(i, completedLap, summary));
                }

                if (_bestLapMs[i] == 0 || lap.LastLapTimeInMS < _bestLapMs[i])
                {
                    _bestLapMs[i] = lap.LastLapTimeInMS;
                }

                RecordH2hLap(i, Math.Max(1, lap.CurrentLapNum - 1), lap.LastLapTimeInMS, lap.CarPosition);

                // Speed-trace handout for the player: the completed lap's samples leave the
                // recorder once — the store's replay protection already prevents double
                // completions, so the trace is never emitted twice. Laps without usable
                // telemetry (e.g. never sampled) emit no event at all.
                if (i == (_meta?.PlayerCarIndex ?? 0) && _traceRecorder.TakeTrace(completedLap) is { } traceSamples)
                {
                    events?.Add(new LapTraced(
                        i, completedLap,
                        new LapTrace(
                            completedLap,
                            lap.LastLapTimeInMS,
                            (ushort)Math.Min(_meta?.TrackLength ?? 0, ushort.MaxValue),
                            traceSamples)));
                }

                // All-time personal best: the App layer seeded AllTimeBestLapMs from the DB
                // at session start. When the player beats it, announce it as a race-control
                // event and adopt the new value (no repeat fire within the session).
                if (i == (_meta?.PlayerCarIndex ?? 0) && AllTimeBestLapMs > 0 &&
                    lap.LastLapTimeInMS > 0 && lap.LastLapTimeInMS < AllTimeBestLapMs)
                {
                    AllTimeBestLapMs = lap.LastLapTimeInMS;
                    var lapText = $"{lap.LastLapTimeInMS / 60000}:" +
                                  $"{lap.LastLapTimeInMS % 60000 / 1000:D2}.{lap.LastLapTimeInMS % 1000:D3}";
                    // Route through the feed so the entry gets a sequence number —
                    // consumers dedupe on it (DashboardViewModel _lastEventSequence).
                    var pbEntry = _events.Add(new RaceEventEntry(
                        DateTimeOffset.UtcNow, "personal-best", i,
                        $"PERSONAL BEST {lapText} — new all-time track best!"));
                    events?.Add(new RaceControl(pbEntry));
                }
            }

            // Advance the lap counter and seed the fuel/ERS reference for the new lap.
            // The ERS seed is only valid once a CarStatus packet has reported a store
            // level — seeding 0 against a later real level would show a bogus negative.
            if (lap.CurrentLapNum > _lastLapNum[i])
            {
                _lastLapNum[i] = lap.CurrentLapNum;
                _fuelAtLapStart[i] = _conditions[i]?.FuelInTank ?? 0f;
                if (_conditions[i] is { } condition)
                {
                    _ersAtLapStart[i] = condition.ErsStoreEnergy;
                    _ersSeedValid[i] = true;
                }

                // Per-lap driver-behaviour flags: reset when the new lap begins so each
                // LapCompleted carries exactly its own lap's facts.
                _overtakeUsedThisLap[i] = false;
                _wrongWayThisLap[i] = false;
            }
        }

        return DetectOvertakes(events, positionsBefore[..carCount]) ??
               (IReadOnlyList<StoreEvent>)Array.Empty<StoreEvent>();
    }

    /// <summary>Fires one <see cref="Overtake"/> per adjacent swap detected between the
    /// previous and this packet's standings, gated on both cars actually racing (active
    /// result status, not in the pits — pit-lane passes are traffic, not overtakes). The
    /// player's own swaps also reach the HUD feed as race-control entries; AI-vs-AI swaps
    /// stay off the feed (the game reports those itself) but still hit the DB.</summary>
    private IReadOnlyList<StoreEvent>? DetectOvertakes(List<StoreEvent>? events, ReadOnlySpan<byte> before)
    {
        Span<byte> after = stackalloc byte[TelemetryConstants.MaxCars];
        for (var i = 0; i < TelemetryConstants.MaxCars; i++)
        {
            after[i] = _timings[i]?.Position ?? 0;
        }

        foreach (var swap in OvertakeDetector.FindSwaps(before, after))
        {
            if (_timings[swap.MoverIndex]?.ResultStatus != ResultStatus.Active ||
                _timings[swap.VictimIndex]?.ResultStatus != ResultStatus.Active)
            {
                continue; // retirement/pit cascades move positions without a pass
            }

            if (_timings[swap.MoverIndex]?.PitStatus != PitStatus.None ||
                _timings[swap.VictimIndex]?.PitStatus != PitStatus.None)
            {
                continue; // pit entry/exit crossings are traffic, not overtakes
            }

            var moverName = _drivers[swap.MoverIndex]?.Name ?? $"Car {swap.MoverIndex + 1}";
            var victimName = _drivers[swap.VictimIndex]?.Name ?? $"Car {swap.VictimIndex + 1}";
            var lapNumber = _timings[swap.MoverIndex]?.CurrentLapNum ?? 0;
            events ??= new List<StoreEvent>();
            events.Add(new Overtake(
                swap.MoverIndex, moverName, swap.VictimIndex, victimName,
                swap.NewPosition, lapNumber));

            if (swap.MoverIndex == (_meta?.PlayerCarIndex ?? 0) ||
                swap.VictimIndex == (_meta?.PlayerCarIndex ?? 0))
            {
                var text = swap.MoverIndex == (_meta?.PlayerCarIndex ?? 0)
                    ? $"OVERHAUL! P{swap.NewPosition} — du hast {victimName} überholt"
                    : $"{moverName} hat dich überholt (P{swap.NewPosition})";
                var entry = _events.Add(new RaceEventEntry(
                    DateTimeOffset.UtcNow, "Overtake", swap.MoverIndex, text, 0, lapNumber));
                events.Add(new RaceControl(entry));
            }
        }

        return events;
    }

    /// <summary>Records a completed lap for the head-to-head tally and, when another car
    /// has already completed the SAME lap, compares the pair exactly once (the second
    /// completer triggers). Ties and unknown positions (0/255) score nobody. Scores are
    /// kept per ordered pair — the overlay presents a two-driver duel, so a car beating
    /// the whole field must not inflate one shared counter.</summary>
    private void RecordH2hLap(byte carIndex, int lapNumber, uint lapTimeMs, byte position)
    {
        if (carIndex >= TelemetryConstants.MaxCars || lapNumber <= 0)
        {
            return;
        }

        var laps = _h2hLaps[carIndex] ??= new Dictionary<int, (uint, byte)>();
        if (!laps.TryAdd(lapNumber, (lapTimeMs, position)))
        {
            return; // replayed/flashback crossing — never score a lap twice
        }

        for (byte j = 0; j < TelemetryConstants.MaxCars; j++)
        {
            if (j == carIndex || _h2hLaps[j] is not { } other ||
                !other.TryGetValue(lapNumber, out var rival))
            {
                continue;
            }

            // One entry per unordered pair (A < B) so both directions share the slot.
            var a = Math.Min(carIndex, j);
            var b = Math.Max(carIndex, j);
            var pair = (LapsA: 0, PosA: 0, LapsB: 0, PosB: 0);
            if (_h2hPairs.TryGetValue(a * TelemetryConstants.MaxCars + b, out var existing))
            {
                pair = existing;
            }

            if (lapTimeMs < rival.LapTimeMs)
            {
                pair = carIndex == a
                    ? (pair.LapsA + 1, pair.PosA, pair.LapsB, pair.PosB)
                    : (pair.LapsA, pair.PosA, pair.LapsB + 1, pair.PosB);
            }
            else if (rival.LapTimeMs < lapTimeMs)
            {
                pair = carIndex == a
                    ? (pair.LapsA, pair.PosA, pair.LapsB + 1, pair.PosB)
                    : (pair.LapsA + 1, pair.PosA, pair.LapsB, pair.PosB);
            }

            // 0/255 = unknown position (e.g. pit exit, retired car) — no positional score.
            if (position is > 0 and < 255 && rival.Position is > 0 and < 255)
            {
                if (position < rival.Position)
                {
                    pair = carIndex == a
                        ? (pair.LapsA, pair.PosA + 1, pair.LapsB, pair.PosB)
                        : (pair.LapsA, pair.PosA, pair.LapsB, pair.PosB + 1);
                }
                else if (rival.Position < position)
                {
                    pair = carIndex == a
                        ? (pair.LapsA, pair.PosA, pair.LapsB, pair.PosB + 1)
                        : (pair.LapsA, pair.PosA + 1, pair.LapsB, pair.PosB);
                }
            }

            _h2hPairs[a * TelemetryConstants.MaxCars + b] = pair;
            _h2hVersion++;
        }
    }

    private void ApplyCarStatus(CarStatusDataPacket packet)
    {
        var statuses = packet.CarStatusData.AsSpan();
        var carCount = Math.Min(statuses.Length, TelemetryConstants.MaxCars);
        for (byte i = 0; i < carCount; i++)
        {
            var s = statuses[i];
            _conditions[i] = new CarCondition(
                i, s.ActualTyreCompound, s.TyresAgeLaps,
                s.FuelInTank, s.FuelRemainingLaps,
                s.ErsStoreEnergy, s.ErsDeployMode, s.DrsAllowed);
            _brakeBias[i] = s.FrontBrakeBias;
        }
    }

    /// <summary>Extracts the worst tyre wear/damage per car (four wheels, 0–100 %) for the
    /// overlay heatmap plus the full damage detail. The full detail reaches persistence
    /// only when its rounded fingerprint changes — packets arrive at ~20 Hz and sub-percent
    /// drift would flood the damage log; the first all-intact packet is not an event.</summary>
    private IReadOnlyList<StoreEvent>? ApplyCarDamage(CarDamageDataPacket packet)
    {
        List<StoreEvent>? events = null;
        var damages = packet.CarDamageData.AsSpan();
        var carCount = Math.Min(damages.Length, TelemetryConstants.MaxCars);
        for (byte i = 0; i < carCount; i++)
        {
            var d = damages[i];
            var worstWear = Math.Max(
                d.TyresWear.FrontLeft, Math.Max(d.TyresWear.FrontRight,
                Math.Max(d.TyresWear.RearLeft, d.TyresWear.RearRight)));
            var worstDamage = Math.Max(
                d.TyresDamage.FrontLeft, Math.Max(d.TyresDamage.FrontRight,
                Math.Max(d.TyresDamage.RearLeft, d.TyresDamage.RearRight)));

            _tyreHealth[i] = new TyreStatus(i, worstWear, worstDamage);

            var status = new CarDamageStatus(
                i, d.FrontLeftWingDamage, d.FrontRightWingDamage, d.RearWingDamage,
                d.FloorDamage, d.DiffuserDamage, d.DrsFault,
                d.SidepodDamage,
                (byte)Math.Max(d.BrakesDamage.FrontLeft, d.BrakesDamage.FrontRight),
                (byte)Math.Max(d.BrakesDamage.RearLeft, d.BrakesDamage.RearRight),
                d.GearBoxDamage, d.EngineDamage,
                d.ErsFault, false, false, d.EngineBlown, d.EngineSeized,
                d.EngineICEWear, d.EngineMGUHWear, d.EngineESWear, d.EngineCEWear);
            _damages[i] = status;
            UpdateWearTrend(i, worstWear);

            var fingerprint = DamageFingerprint(status);
            if (_damageSeen[i])
            {
                if (fingerprint != _damageFingerprint[i])
                {
                    _damageFingerprint[i] = fingerprint;
                    events ??= new List<StoreEvent>();
                    events.Add(new CarDamageChanged(i, status,
                        (byte)MathF.Round(worstWear), (byte)MathF.Round(worstDamage),
                        (int)(_timings[i]?.CurrentLapNum ?? 0)));
                }
            }
            else
            {
                _damageSeen[i] = true;
                _damageFingerprint[i] = fingerprint;
                if (fingerprint != ZeroDamageFingerprint)
                {
                    events ??= new List<StoreEvent>();
                    events.Add(new CarDamageChanged(i, status,
                        (byte)MathF.Round(worstWear), (byte)MathF.Round(worstDamage),
                        (int)(_timings[i]?.CurrentLapNum ?? 0)));
                }
            }
        }

        return events;
    }

    /// <summary>Fingerprint of the un-damaged car — the first packet with a fully intact
    /// car is treated as "no damage seen yet", not an event.</summary>
    private static int ZeroDamageFingerprint { get; } =
        DamageFingerprint(new CarDamageStatus(0, 0, 0, 0, 0, 0, false));

    /// <summary>Change key over the damage fields persistence stores, rounded to whole
    /// percent so sub-percent packet noise doesn't spam the damage log.</summary>
    private static int DamageFingerprint(CarDamageStatus d) =>
        HashCode.Combine(
            d.FrontLeftWing, d.FrontRightWing, d.RearWing, d.Floor, d.Diffuser, d.Sidepod,
            d.FrontBrakeDamage, d.RearBrakeDamage)
        ^ HashCode.Combine(
            d.GearBoxDamage, d.EngineDamage, d.DrsFault, d.ErsFault,
            d.EngineBlown, d.EngineSeized, (int)d.EngineIceWear, 0)
        ^ HashCode.Combine(
            (int)d.EngineMguhWear, (int)d.EngineEsWear, (int)d.EngineCeWear, 0, 0, 0, 0, 0)
        ^ HashCode.Combine(
            (int)d.EngineMguhWear, (int)d.EngineEsWear, (int)d.EngineCeWear, 0, 0, 0, 0, 0);

    /// <summary>Tracks the player's tyre-wear baseline per stint: whenever the compound
    /// changes (pit stop, new set) the baseline restarts, so the wear-per-lap trend stays
    /// stint-scoped. CarDamage arrives at a steady rate for all cars; the trend only
    /// becomes usable after the second lap in the stint.</summary>
    private void UpdateWearTrend(byte carIndex, float wear)
    {
        var compound = _conditions[carIndex]?.TyreCompound;
        if (compound != _wearStintCompound[carIndex])
        {
            _wearStintCompound[carIndex] = compound;
            _wearBaselineWear[carIndex] = wear;
            _wearBaselineLap[carIndex] = _lastLapNum[carIndex];
        }
    }

    /// <summary>Tyre bank of one car (the packet cycles through the cars at ~20 Hz —
    /// only the player's bank is captured, it is the one the race strategy cares about).</summary>
    private void ApplyTyreSets(TyreSetsDataPacket packet)
    {
        if (packet.CarIndex != _meta?.PlayerCarIndex)
        {
            return;
        }

        var sets = packet.TyreSetDatas.AsSpan();
        var entries = new TyreSetEntry[sets.Length];
        for (var i = 0; i < sets.Length; i++)
        {
            var s = sets[i];
            entries[i] = new TyreSetEntry(
                (byte)i,
                s.ActualTyreCompound,
                s.VisualTyreCompound,
                s.Wear,
                s.IsAvailable,
                i == packet.FittedIndex,
                s.LifeSpan,
                s.UsableLife);
        }

        _tyreSets = entries;
    }

    private void ApplyCarTelemetry(CarTelemetryDataPacket packet)
    {
        var cars = packet.CarTelemetryData.AsSpan();
        var carCount = Math.Min(cars.Length, TelemetryConstants.MaxCars);
        var player = packet.Header.PlayerCarIndex;
        for (byte i = 0; i < carCount; i++)
        {
            var t = cars[i];
            _telemetry[i] = new CarTelemetry(i, t.Speed, t.Gear, t.Throttle, t.Brake, t.EngineRPM, t.IsDrsOn);
            _steer[i] = t.Steer;
            _surfaceTemp[i, 0] = t.TyresSurfaceTemperature.FrontLeft;
            _surfaceTemp[i, 1] = t.TyresSurfaceTemperature.FrontRight;
            _surfaceTemp[i, 2] = t.TyresSurfaceTemperature.RearLeft;
            _surfaceTemp[i, 3] = t.TyresSurfaceTemperature.RearRight;

            // Per-wheel pressures and brake temps only for the player frame — rivals keep
            // the cheap aggregate path (the 60 Hz packet cycle touches all cars anyway).
            if (i == player)
            {
                _pressure[i, 0] = t.TyresPressure.FrontLeft;
                _pressure[i, 1] = t.TyresPressure.FrontRight;
                _pressure[i, 2] = t.TyresPressure.RearLeft;
                _pressure[i, 3] = t.TyresPressure.RearRight;
                _brakeTemp[i, 0] = t.BrakesTemperature.FrontLeft;
                _brakeTemp[i, 1] = t.BrakesTemperature.FrontRight;
                _brakeTemp[i, 2] = t.BrakesTemperature.RearLeft;
                _brakeTemp[i, 3] = t.BrakesTemperature.RearRight;
            }
        }

        SampleLapTrace(player);
    }

    /// <summary>Speed-trace sampling for the player: speed from this telemetry packet,
    /// lap number + distance from the latest LapData. Called per 60 Hz telemetry packet;
    /// the recorder takes ~every 20 m.</summary>
    private void SampleLapTrace(byte player)
    {
        if (player >= TelemetryConstants.MaxCars ||
            _timings[player] is not { } timing || _telemetry[player] is not { } telem)
        {
            return;
        }

        _traceRecorder.Sample(timing.CurrentLapNum, timing.LapDistance, telem.Speed,
            telem.Throttle, telem.Brake);
    }

    private IReadOnlyList<StoreEvent> ApplyEvent(EventDataPacket packet)
    {
        var mapped = RaceEventMapper.Map(packet.EventDetails, _drivers);
        if (mapped is null)
        {
            return Array.Empty<StoreEvent>();
        }

        // Lap the event happened on: penalties carry the game-reported lap; everything
        // else gets the involved car's current lap (the player's for car-less events
        // like safety car / race winner). 0 stays 0 when no timing was ever seen.
        if (mapped.LapNumber == 0)
        {
            var car = mapped.CarIndex ?? _meta?.PlayerCarIndex ?? 0;
            var lap = car < TelemetryConstants.MaxCars
                ? _timings[car]?.CurrentLapNum ?? 0
                : 0;
            if (lap > 0)
            {
                mapped = mapped with { LapNumber = lap };
            }
        }

        // Distance-around-lap stamp for the History's corner/sector attribution: the
        // involved car's LapDistance at event time. Car-less events (safety car, race
        // winner, ...) get no distance — they have no place on the lap. -1 stays -1.
        if (mapped.CarIndex is { } eventCar && eventCar < TelemetryConstants.MaxCars &&
            _timings[eventCar]?.LapDistance is { } distance && distance >= 0f)
        {
            mapped = mapped with { LapDistance = distance };
        }

        var entry = _events.Add(mapped); // stamps the sequence number
        return new StoreEvent[] { new RaceControl(entry) };
    }

    private IReadOnlyList<StoreEvent> ApplyFinalClassification(FinalClassificationDataPacket packet)
    {
        var rows = new List<FinalResultRow>();
        var data = packet.ClassificationData.AsSpan();
        var active = Math.Min(packet.NumCars, data.Length);
        for (byte i = 0; i < active; i++)
        {
            var c = data[i];
            var stintCount = Math.Min((int)c.NumTyreStints, c.TyreStintsActual.AsSpan().Length);
            var stintsActual = new ActualCompound[stintCount];
            var stintsVisual = new VisualCompound[stintCount];
            var stintsEndLaps = new byte[stintCount];
            for (var s = 0; s < stintCount; s++)
            {
                stintsActual[s] = c.TyreStintsActual.AsSpan()[s];
                stintsVisual[s] = c.TyreStintsVisual.AsSpan()[s];
                stintsEndLaps[s] = c.TyreStintsEndLaps.AsSpan()[s];
            }

            rows.Add(new FinalResultRow(
                c.Position, i, _drivers[i]?.Name ?? $"Car {i + 1}",
                _drivers[i]?.Team ?? default, _drivers[i]?.RaceNumber ?? 0,
                c.NumLaps, c.GridPosition, c.Points, c.ResultStatus,
                c.BestLapTimeInMS, c.TotalRaceTime, c.PenaltiesTime, c.NumPenalties,
                c.NumPitStops, c.ResultReason.ToString(), (byte)stintCount,
                stintsActual, stintsVisual, stintsEndLaps));
        }

        rows.Sort((a, b) => a.Position.CompareTo(b.Position));
        _finalResults = rows;

        if (_meta is null || _finalized)
        {
            return Array.Empty<StoreEvent>();
        }

        _finalized = true;
        return new List<StoreEvent>
        {
            new SessionEnded(_meta.SessionUid, "FinalClassification", rows),
        };
    }

    private void ResetState()
    {
        Array.Clear(_drivers);
        _traceRecorder.Reset();
        Array.Clear(_timings);
        Array.Clear(_conditions);
        Array.Clear(_telemetry);
        Array.Clear(_tyreHealth);
        Array.Clear(_bestLapMs);
        Array.Clear(_lastLapNum);
        Array.Clear(_lapAnnounced);
        _events.Clear();
        _finalResults = Array.Empty<FinalResultRow>();
        _finalized = false;
        _lastPacketUtc = DateTimeOffset.MinValue;
        // A stale rival index from the previous session must not survive into the next —
        // clear it along with everything else.
        _rivalOverride = null;
        _posCount = 0;
        _forecast = EmptyForecast;
        _marshalZones = EmptyMarshalZones;
        Array.Clear(_damages);
        Array.Clear(_fuelAtLapStart);
        Array.Clear(_fuelUsedLastLap);
        Array.Clear(_ersAtLapStart);
        Array.Clear(_ersUsedLastLap);
        Array.Clear(_ersSeedValid);
        Array.Clear(_cornerCuttingWarnings);
        Array.Clear(_totalWarnings);
        Array.Clear(_warningSeedValid);
        Array.Clear(_steer);
        Array.Clear(_gLat);
        Array.Clear(_gLong);
        Array.Clear(_brakeBias);
        Array.Clear(_pressure);
        Array.Clear(_surfaceTemp);
        Array.Clear(_brakeTemp);
        Array.Clear(_bestSectorMs);
        Array.Clear(_sessionBestSectorMs);
        Array.Clear(_wearStintCompound);
        Array.Clear(_wearBaselineWear);
        Array.Clear(_wearBaselineLap);
        Array.Clear(_historyLapsEmitted);
        Array.Clear(_historyStintCount);
        _lastSetup = null;
        _lapPositionsStartLap = -1;
        Array.Fill(_activeAeroMode, (byte)255);
        Array.Clear(_overtakeUsedThisLap);
        Array.Clear(_wrongWayThisLap);
        Array.Clear(_damageFingerprint);
        Array.Clear(_damageSeen);
        _motionRecorder.Reset();
        _h2hPairs.Clear();
        _h2hVersion++;
        _h2hBuilt = null;
        Array.Clear(_h2hLaps);
        _tyreSets = null;
        // _nameOverrides intentionally survive: they are keyed by driver name and
        // outlive individual sessions (like RivalDriverName in the settings).
    }

    /// <summary>Builds an immutable snapshot of the current state. Called after each drain
    /// cycle by the owning consumer.</summary>
    /// <summary>Strategy advice for the player car (null before first CarDamage/CarStatus).</summary>
    private StrategyAdvice? BuildStrategy()
    {
        var player = _meta?.PlayerCarIndex ?? 0;
        if (player >= TelemetryConstants.MaxCars)
        {
            return null; // no active player — no strategy advice
        }

        if (_conditions[player] is not { } cond || _tyreHealth[player] is not { } health)
        {
            return null;
        }

        var lapsIntoStint = _lastLapNum[player] - _wearBaselineLap[player];
        var wearPerLap = lapsIntoStint >= 1
            ? (health.WearPercent - _wearBaselineWear[player]) / lapsIntoStint
            : 0f;
        return StrategyAdvisor.Compute(
            _bestLapMs[player], cond.FuelInTank, _fuelUsedLastLap[player],
            health.WearPercent, wearPerLap, _lastLapNum[player]);
    }

    /// <summary>Fuel-to-finish advice for the player car (null outside lap-based races).
    /// Uses the game's FuelRemainingLaps plus the last lap's burn for the litres figure.</summary>
    private FuelAdvice? BuildFuelAdvice()
    {
        var player = _meta?.PlayerCarIndex ?? 0;
        if (player >= TelemetryConstants.MaxCars)
        {
            return null; // no active player — no fuel advice
        }

        if (_conditions[player] is not { } cond)
        {
            return null;
        }

        return FuelCalculator.Compute(
            _meta?.TotalLaps ?? 0,
            _timings[player]?.CurrentLapNum ?? 0,
            cond.FuelRemainingLaps,
            _fuelUsedLastLap[player]);
    }

    public TelemetrySnapshot BuildSnapshot()
    {
        _frameVersion++;
        return new TelemetrySnapshot(
            _meta,
            CopyDrivers(),
            StandingsCalculator.BuildStandings(_drivers, _timings, _conditions, _bestLapMs,
                sessionBestSectorMs: _sessionBestSectorMs),
            _finalResults,
            _events.Latest(RecentEventsInSnapshot),
            BuildPlayerFrame(_meta?.PlayerCarIndex ?? 0),
            BuildPlayerFrame(ResolveRivalIndex()),
            _frameVersion,
            DateTimeOffset.UtcNow,
            BuildTyres(),
            BuildPositions(),
            BuildDamages(),
            _tyreSets,
            Strategy: BuildStrategy(),
            H2h: BuildH2h(),
            Fuel: BuildFuelAdvice());
    }

    /// <summary>Head-to-head duel tallies, one entry per pair that completed a shared lap;
    /// null when no duel is being scored (keeps the field out of the overlay state entirely).
    /// The list is cached and only rebuilt when a tally actually changed — BuildSnapshot
    /// runs per drain cycle while tallies move only on lap completions.</summary>
    private IReadOnlyList<H2hTally>? BuildH2h()
    {
        if (_h2hBuilt is { } built && built.Version == _h2hVersion)
        {
            return built.Tallies.Count == 0 ? null : built.Tallies;
        }

        var tallies = new List<H2hTally>(_h2hPairs.Count);
        foreach (var (key, pair) in _h2hPairs)
        {
            if (pair.LapsA == 0 && pair.PosA == 0 && pair.LapsB == 0 && pair.PosB == 0)
            {
                continue; // shared lap with a tie — nothing to show
            }

            var a = (byte)(key / TelemetryConstants.MaxCars);
            var b = (byte)(key % TelemetryConstants.MaxCars);
            tallies.Add(new H2hTally(
                a, b, pair.LapsA, pair.PosA, pair.LapsB, pair.PosB));
        }

        tallies.Sort((x, y) => x.CarIndex != y.CarIndex
            ? x.CarIndex.CompareTo(y.CarIndex)
            : x.OpponentIndex.CompareTo(y.OpponentIndex));
        _h2hBuilt = (_h2hVersion, tallies);
        return tallies.Count == 0 ? null : tallies;
    }

    /// <summary>Only cars that have reported CarDamage are listed (null slots skipped).</summary>
    private List<CarDamageStatus> BuildDamages()
    {
        var damages = new List<CarDamageStatus>(TelemetryConstants.MaxCars);
        for (byte i = 0; i < TelemetryConstants.MaxCars; i++)
        {
            if (_damages[i] is { } damage)
            {
                damages.Add(damage);
            }
        }

        return damages;
    }

    /// <summary>Snapshot copy of the latest Motion-derived positions (null before the
    /// first Motion packet — consumers skip the minimap work entirely then).</summary>
    private MotionFrame? BuildPositions()
    {
        if (_posCount == 0)
        {
            return null;
        }

        return new MotionFrame(
            _posCount,
            (float[])_posX.Clone(),
            (float[])_posZ.Clone(),
            (float[])_fwdX.Clone(),
            (float[])_fwdZ.Clone());
    }

    /// <summary>Only cars that have reported CarDamage are listed (null slots skipped).</summary>
    private List<TyreStatus> BuildTyres()
    {
        var tyres = new List<TyreStatus>(TelemetryConstants.MaxCars);
        for (byte i = 0; i < TelemetryConstants.MaxCars; i++)
        {
            if (_tyreHealth[i] is { } tyre)
            {
                tyres.Add(tyre);
            }
        }

        return tyres;
    }

    private byte ResolveRivalIndex()
    {
        if (_rivalOverride is { } explicitIndex)
        {
            return explicitIndex;
        }

        // Default rival: the teammate (same team, different car).
        var playerIndex = _meta?.PlayerCarIndex ?? 0;
        if (playerIndex >= TelemetryConstants.MaxCars)
        {
            return playerIndex; // no active player — BuildPlayerFrame returns null
        }

        if (_drivers[playerIndex] is { } player)
        {
            for (byte i = 0; i < TelemetryConstants.MaxCars; i++)
            {
                if (i != playerIndex && _drivers[i]?.Team == player.Team)
                {
                    return i;
                }
            }
        }

        return playerIndex;
    }

    private PlayerFrame? BuildPlayerFrame(byte carIndex)
    {
        if (carIndex >= TelemetryConstants.MaxCars)
        {
            return null; // game sends 255 when no player is active
        }

        var telemetry = _telemetry[carIndex];
        var condition = _conditions[carIndex];
        var driver = _drivers[carIndex];
        if (telemetry is null && condition is null)
        {
            return null;
        }

        return new PlayerFrame(
            carIndex,
            driver?.Name ?? $"Car {carIndex + 1}",
            telemetry?.Speed ?? 0,
            telemetry?.Gear ?? 0,
            telemetry?.Throttle ?? 0,
            telemetry?.Brake ?? 0,
            telemetry?.EngineRpm ?? 0,
            condition?.ErsStoreEnergy ?? 0,
            condition?.ErsDeployMode ?? default,
            condition?.FuelInTank ?? 0,
            condition?.FuelRemainingLaps ?? 0,
            condition?.TyreCompound ?? default,
            condition?.TyreAgeLaps ?? 0,
            condition?.DrsAllowed ?? false,
            telemetry?.IsDrsOn ?? false,
            _steer[carIndex],
            _brakeBias[carIndex],
            _gLat[carIndex],
            _gLong[carIndex],
            BuildWheels(carIndex));
    }

    /// <summary>Per-wheel arrays for the player/rival frames (null before the first
    /// CarTelemetry packet — the 2D store arrays start zeroed).</summary>
    private WheelState? BuildWheels(byte carIndex) =>
        _telemetry[carIndex] is null
            ? null
            : new WheelState(
                new[] { _pressure[carIndex, 0], _pressure[carIndex, 1], _pressure[carIndex, 2], _pressure[carIndex, 3] },
                new[] { Convert.ToSingle(_surfaceTemp[carIndex, 0]), Convert.ToSingle(_surfaceTemp[carIndex, 1]),
                        Convert.ToSingle(_surfaceTemp[carIndex, 2]), Convert.ToSingle(_surfaceTemp[carIndex, 3]) },
                new[] { _brakeTemp[carIndex, 0], _brakeTemp[carIndex, 1], _brakeTemp[carIndex, 2], _brakeTemp[carIndex, 3] });

    private IReadOnlyList<DriverEntry> CopyDrivers()
    {
        var drivers = new List<DriverEntry>(TelemetryConstants.MaxCars);
        foreach (var driver in _drivers)
        {
            if (driver is not null)
            {
                drivers.Add(driver);
            }
        }

        return drivers;
    }
}