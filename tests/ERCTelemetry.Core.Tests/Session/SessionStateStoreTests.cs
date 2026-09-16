using F1Game.UDP.Data;
using F1Game.UDP.Enums;
using F1Game.UDP.Events;
using F1Game.UDP.Packets;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

public class SessionStateStoreTests
{
    private const ulong SessionA = 1111;
    private const ulong SessionB = 2222;

    private static PacketHeader Header(PacketType type, ulong sessionUid, byte playerIndex = 0) => new()
    {
        PacketFormat = 2026,
        GameYear = 26,
        GameMajorVersion = 1,
        PacketVersion = 1,
        PacketType = type,
        SessionUID = sessionUid,
        PlayerCarIndex = playerIndex,
    };

    private static SessionDataPacket SessionPacket(ulong uid, byte playerIndex = 0,
        params (int OffsetMinutes, Weather Weather, byte RainPercent)[] forecast) => new()
    {
        Header = Header(PacketType.Session, uid, playerIndex),
        SessionType = SessionType.Race,
        TotalLaps = 20,
        TrackLength = 5000,
        Weather = Weather.Clear,
        NumWeatherForecastSamples = (byte)forecast.Length,
        WeatherForecastSamples = forecast
            .Select(f => new WeatherForecastSample
            {
                TimeOffset = (byte)f.OffsetMinutes,
                Weather = f.Weather,
                RainPercentage = f.RainPercent,
                TrackTemperature = 26,
                AirTemperature = 21,
            })
            .ToArray(),
    };

    private static LapDataPacket LapPacket(ulong uid,
        params (int Index, byte Position, byte Lap, uint LastLap, uint CurrentLap)[] cars)
    {
        var laps = new LapData[22];
        foreach (var (index, position, lap, lastLap, currentLap) in cars)
        {
            laps[index] = new LapData
            {
                CarPosition = position,
                CurrentLapNum = lap,
                LastLapTimeInMS = lastLap,
                CurrentLapTimeInMS = currentLap,
            };
        }

        return new LapDataPacket { Header = Header(PacketType.LapData, uid), LapData = laps };
    }

    private static ParticipantsDataPacket ParticipantsPacket(ulong uid,
        params (byte Index, string Name, Team Team, byte RaceNumber)[] drivers)
    {
        var participants = new ParticipantData[22];
        foreach (var (index, name, team, raceNumber) in drivers)
        {
            participants[index] = new ParticipantData
            {
                Name = name,
                Team = team,
                RaceNumber = raceNumber,
            };
        }

        return new ParticipantsDataPacket
        {
            Header = Header(PacketType.Participants, uid),
            NumActiveCars = (byte)drivers.Length,
            Participants = participants,
        };
    }

    private static CarStatusDataPacket StatusPacket(ulong uid,
        params (byte CarIndex, float Fuel, float Ers)[] cars)
    {
        var statuses = new CarStatusData[22];
        foreach (var (carIndex, fuel, ers) in cars)
        {
            statuses[carIndex] = new CarStatusData
            {
                FuelInTank = fuel,
                FuelCapacity = 110,
                ErsStoreEnergy = ers,
            };
        }

        return new CarStatusDataPacket
        {
            Header = Header(PacketType.CarStatus, uid),
            CarStatusData = statuses,
        };
    }

    private static CarTelemetryDataPacket TelemetryPacket(ulong uid,
        params (byte CarIndex, ushort Speed, float Throttle)[] cars)
    {
        var data = new CarTelemetryData[22];
        foreach (var (carIndex, speed, throttle) in cars)
        {
            data[carIndex] = new CarTelemetryData
            {
                Speed = speed,
                Throttle = throttle,
                Gear = 6,
                EngineRPM = 11000,
            };
        }

        return new CarTelemetryDataPacket
        {
            Header = Header(PacketType.CarTelemetry, uid),
            CarTelemetryData = data,
        };
    }

    private static EventDataPacket EventPacket(ulong uid, EventDetails details) => new()
    {
        Header = Header(PacketType.Event, uid),
        EventDetails = details,
    };

    [Fact]
    public void Session_uid_change_ends_previous_session_and_starts_new_one()
    {
        var store = new SessionStateStore();

        var eventsA = store.Apply(SessionPacket(SessionA));
        var eventsB = store.Apply(SessionPacket(SessionB));

        Assert.IsType<SessionStarted>(eventsA.Single());
        Assert.Equal(2, eventsB.Count);
        var ended = Assert.IsType<SessionEnded>(eventsB[0]);
        Assert.Equal(SessionA, ended.SessionUid);
        Assert.Equal("SessionChange", ended.Reason);
        Assert.IsType<SessionStarted>(eventsB[1]);
        Assert.Equal(SessionB, store.Meta!.SessionUid);
    }

    [Fact]
    public void CheckIdle_finalizes_an_open_session_after_the_timeout()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // A fresh packet keeps the session alive.
        Assert.Null(store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));

        // No packets for the timeout → the session is finalized as idle.
        var ended = Assert.IsType<SessionEnded>(
            store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)));
        Assert.Equal(SessionA, ended.SessionUid);
        Assert.Equal("Idle", ended.Reason);

        // Already finalized → no second event.
        Assert.Null(store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CheckIdle_returns_null_without_an_open_session()
    {
        var store = new SessionStateStore();

        Assert.Null(store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CheckIdle_returns_null_after_final_classification()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(new FinalClassificationDataPacket
        {
            Header = Header(PacketType.FinalClassification, SessionA),
            NumCars = 0,
            ClassificationData = Array.Empty<FinalClassificationData>(),
        });

        Assert.Null(store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CheckIdle_then_same_uid_resume_reopens_the_session()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Idle timeout finalizes the session.
        Assert.IsType<SessionEnded>(
            store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)));

        // The game resumes with the same session UID (pause / lobby) — the session is
        // re-opened and a SessionStarted is re-emitted so persistence re-opens the row.
        var events = store.Apply(SessionPacket(SessionA));
        Assert.IsType<SessionStarted>(events.Single());

        // The re-opened session can be finalized again (a second idle or the real end).
        var ended = Assert.IsType<SessionEnded>(
            store.CheckIdle(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30)));
        Assert.Equal("Idle", ended.Reason);
    }

    [Fact]
    public void Participants_populate_drivers_and_player_flag()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA, playerIndex: 1));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Rival One", Team.Mercedes, 44),
            (1, "Stream Driver", Team.Mercedes, 7)));

        var snapshot = store.BuildSnapshot();

        Assert.Equal(2, snapshot.Drivers.Count);
        Assert.Equal("Stream Driver", snapshot.Drivers.Single(d => d.IsPlayer).Name);
    }

    [Fact]
    public void Lap_completed_event_fires_once_per_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA, (0, "Driver", Team.Ferrari, 5)));

        var first = store.Apply(LapPacket(SessionA, (0, 1, 1, 84_500, 1_200)));
        var repeat = store.Apply(LapPacket(SessionA, (0, 1, 1, 84_500, 2_000)));

        var lapEvent = Assert.IsType<LapCompleted>(first.Single());
        Assert.Equal((byte)0, lapEvent.CarIndex);
        Assert.Equal((byte)1, lapEvent.LapNumber);
        Assert.Equal(84_500u, lapEvent.LapTimeMs);
        Assert.Empty(repeat);
    }

    [Fact]
    public void Warning_counter_increase_emits_events_with_lap_and_distance()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA, (0, "Basti", Team.Ferrari, 5)));
        store.Apply(LapPacket(SessionA, (0, 1, 3, 90_000, 1_200))); // seeds the counters at 0/0

        var laps = new LapData[22];
        laps[0] = new LapData
        {
            CarPosition = 1,
            CurrentLapNum = 3,
            LastLapTimeInMS = 90_000,
            CurrentLapTimeInMS = 1_200,
            LapDistance = 2450f,
            CornerCuttingWarnings = 1,
            TotalWarnings = 1,
        };
        var events = store.Apply(
            new LapDataPacket { Header = Header(PacketType.LapData, SessionA), LapData = laps });

        var control = events.OfType<RaceControl>().ToList();
        Assert.Contains(control, e => e.Event.Type == "CornerCuttingWarning");
        var warning = control.Single(e => e.Event.Type == "Warning").Event;
        Assert.Equal(3, warning.LapNumber);
        Assert.Equal(2450f, warning.LapDistance);
    }

    [Fact]
    public void Warning_counter_decrease_is_a_flashback_and_does_not_re_emit()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        LapDataPacket WithWarnings(byte cornerCutting, byte total) => new()
        {
            Header = Header(PacketType.LapData, SessionA),
            LapData = BuildLapsWithWarnings(cornerCutting, total),
        };

        store.Apply(WithWarnings(1, 1)); // first sighting: seeds, emits nothing
        store.Apply(WithWarnings(2, 2)); // increase: emits
        var rewind = store.Apply(WithWarnings(1, 1)); // flashback: counter goes down

        Assert.DoesNotContain(rewind,
            e => e is RaceControl rc && rc.Event.Type is "Warning" or "CornerCuttingWarning");
    }

    [Fact]
    public void First_warning_sighting_only_seeds_the_counters()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        var events = store.Apply(new LapDataPacket
        {
            Header = Header(PacketType.LapData, SessionA),
            LapData = BuildLapsWithWarnings(2, 2), // joined mid-session: counters already high
        });

        Assert.DoesNotContain(events,
            e => e is RaceControl rc && rc.Event.Type is "Warning" or "CornerCuttingWarning");
    }

    [Fact]
    public void Penalty_event_keeps_the_game_lap_and_gets_the_car_distance()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        var laps = new LapData[22];
        laps[0] = new LapData
        {
            CarPosition = 1,
            CurrentLapNum = 7,
            CurrentLapTimeInMS = 1_200,
            LapDistance = 2450f,
        };
        store.Apply(new LapDataPacket { Header = Header(PacketType.LapData, SessionA), LapData = laps });

        var events = store.Apply(EventPacket(SessionA, new EventDetails(new PenaltyEvent
        {
            VehicleIdx = 0,
            PenaltyType = PenaltyType.TimePenalty,
            InfringementType = InfringementType.PitLaneSpeeding,
            Time = 5,
            LapNum = 7, // the game reports the lap itself
        })));

        var entry = Assert.IsType<RaceControl>(events.Single()).Event;
        Assert.Equal("Penalty", entry.Type);
        Assert.Equal(7, entry.LapNumber);
        Assert.Equal(2450f, entry.LapDistance);
    }

    /// <summary>22-slot LapData array with car 0 carrying the given warning counters.</summary>
    private static LapData[] BuildLapsWithWarnings(byte cornerCutting, byte total)
    {
        var laps = new LapData[22];
        laps[0] = new LapData
        {
            CarPosition = 1,
            CurrentLapNum = 3,
            CurrentLapTimeInMS = 1_200,
            CornerCuttingWarnings = cornerCutting,
            TotalWarnings = total,
        };
        return laps;
    }

    [Fact]
    public void Best_lap_tracks_minimum_completed_lap_time()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(LapPacket(SessionA, (0, 1, 1, 90_000, 1)));
        store.Apply(LapPacket(SessionA, (0, 1, 2, 85_000, 1)));
        store.Apply(LapPacket(SessionA, (0, 1, 3, 88_000, 1)));

        var snapshot = store.BuildSnapshot();

        Assert.Equal(85_000u, snapshot.Standings.Single().BestLapTimeMs);
    }

    [Fact]
    public void Standings_are_ordered_by_position()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(LapPacket(SessionA,
            (0, 3, 1, 0, 1),
            (1, 1, 1, 0, 1),
            (2, 2, 1, 0, 1)));

        var snapshot = store.BuildSnapshot();

        Assert.Equal(new[] { (byte)1, (byte)2, (byte)3 },
            snapshot.Standings.Select(r => r.Position));
        Assert.Equal(1, snapshot.Standings[0].CarIndex);
    }

    [Fact]
    public void Default_rival_is_the_teammate()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA, playerIndex: 1));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Rival One", Team.RedBullRacing, 1),
            (1, "Stream Driver", Team.Mercedes, 7),
            (2, "Rival Two", Team.Mercedes, 12)));
        store.Apply(TelemetryPacket(SessionA, (1, 200, 0.9f), (2, 180, 0.8f)));
        store.Apply(StatusPacket(SessionA, (1, 50, 3000), (2, 40, 3000)));

        var snapshot = store.BuildSnapshot();

        Assert.NotNull(snapshot.Player);
        Assert.NotNull(snapshot.Rival);
        Assert.Equal(1, snapshot.Player!.CarIndex);
        Assert.Equal(2, snapshot.Rival!.CarIndex); // same team (Mercedes), different car
        Assert.Equal(200, snapshot.Player!.Speed);
        Assert.Equal(180, snapshot.Rival!.Speed);
    }

    [Fact]
    public void Rival_override_is_respected()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA, playerIndex: 0));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Stream Driver", Team.Mercedes, 7),
            (1, "Rival One", Team.RedBullRacing, 1)));
        store.Apply(TelemetryPacket(SessionA, (1, 250, 0.9f)));
        store.RivalOverride = 1;

        var snapshot = store.BuildSnapshot();

        Assert.Equal(1, snapshot.Rival!.CarIndex);
        Assert.Equal(250, snapshot.Rival!.Speed);
    }

    [Fact]
    public void Flashback_does_not_duplicate_lap_events_or_pollute_best_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA, (0, "Driver", Team.Ferrari, 5)));

        // Lap 2 completed, then a flashback rewinds to lap 1 and the lap is re-crossed.
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0, 1)));
        store.Apply(LapPacket(SessionA, (0, 1, 2, 85_000, 1)));
        var afterFlashback = store.Apply(LapPacket(SessionA, (0, 1, 1, 85_000, 1)));
        var onReplayCrossing = store.Apply(LapPacket(SessionA, (0, 1, 2, 80_000, 1)));

        var lapEvents = new[] { afterFlashback, onReplayCrossing }.SelectMany(e => e)
            .OfType<LapCompleted>().ToList();

        Assert.Empty(lapEvents); // the replayed lap completion is suppressed
        Assert.Equal(85_000u, store.BuildSnapshot().Standings.Single().BestLapTimeMs);
    }

    [Fact]
    public void Participants_arriving_before_session_still_get_the_player_flag()
    {
        var store = new SessionStateStore();

        // Online lobbies can send Participants before the first Session packet.
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Someone Else", Team.Mercedes, 44),
            (1, "Stream Driver", Team.Mercedes, 7)));
        store.Apply(SessionPacket(SessionA, playerIndex: 1));

        var snapshot = store.BuildSnapshot();

        Assert.Equal("Stream Driver", snapshot.Drivers.Single(d => d.IsPlayer).Name);
    }

    [Fact]
    public void Race_events_carry_strictly_increasing_sequence_numbers()
    {
        var feed = new EventFeed();
        var first = feed.Add(new RaceEventEntry(DateTimeOffset.UtcNow, "FastestLap", 0, "a", 0));
        var second = feed.Add(new RaceEventEntry(DateTimeOffset.UtcNow, "Penalty", 1, "b", 0));
        feed.Clear(); // session change — sequences must not restart
        var third = feed.Add(new RaceEventEntry(DateTimeOffset.UtcNow, "Retirement", 2, "c", 0));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.True(third.Sequence > second.Sequence);
        Assert.Single(feed.Latest(10));
    }

    [Fact]
    public void Out_of_range_rival_override_falls_back_to_auto()
    {
        var store = new SessionStateStore();
        store.RivalOverride = 99; // beyond the 22-car grid

        Assert.Null(store.RivalOverride);
    }

    [Fact]
    public void Final_classification_builds_results_and_ends_session_once()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Winner", Team.Ferrari, 1),
            (1, "Second", Team.Mercedes, 2)));

        var classification = new FinalClassificationDataPacket
        {
            Header = Header(PacketType.FinalClassification, SessionA),
            NumCars = 2,
            ClassificationData = new FinalClassificationData[]
            {
                new() { Position = 1, NumLaps = 20, Points = 25, BestLapTimeInMS = 82_000 },
                new() { Position = 2, NumLaps = 20, Points = 18, BestLapTimeInMS = 83_000 },
            },
        };

        var first = store.Apply(classification);
        var second = store.Apply(classification); // must be idempotent

        var ended = Assert.IsType<SessionEnded>(first.Single());
        Assert.Equal("FinalClassification", ended.Reason);
        Assert.Equal(2, ended.Results.Count);
        Assert.Equal("Winner", ended.Results[0].Name);
        Assert.Empty(second);
    }

    private static MotionDataPacket MotionPacket(ulong uid,
        params (byte Index, float X, float Z)[] positions)
    {
        var cars = new CarMotionData[22];
        foreach (var (index, x, z) in positions)
        {
            cars[index] = new CarMotionData { WorldPositionX = x, WorldPositionZ = z };
        }

        return new MotionDataPacket { Header = Header(PacketType.Motion, uid), CarMotionData = cars };
    }

    [Fact]
    public void Session_packet_carries_weather_forecast()
    {
        var store = new SessionStateStore();

        store.Apply(SessionPacket(SessionA, forecast:
        [
            (10, Weather.LightRain, 65),
            (30, Weather.Clear, 0),
        ]));

        var snapshot = store.BuildSnapshot();

        Assert.NotNull(snapshot.Meta!.Forecast);
        Assert.Equal(2, snapshot.Meta.Forecast!.Count);
        Assert.Equal(10, snapshot.Meta.Forecast[0].TimeOffsetMinutes);
        Assert.Equal(Weather.LightRain, snapshot.Meta.Forecast[0].Weather);
        Assert.Equal(65, snapshot.Meta.Forecast[0].RainPercent);
        Assert.Equal(26, snapshot.Meta.Forecast[0].TrackTemperature);
        Assert.Equal(21, snapshot.Meta.Forecast[0].AirTemperature);
    }

    [Fact]
    public void Forecast_repeat_packets_keep_one_list_only_on_changed_content_a_new_list()
    {
        var store = new SessionStateStore();

        store.Apply(SessionPacket(SessionA, forecast: [(10, Weather.LightRain, 65)]));
        var first = store.BuildSnapshot().Meta!.Forecast;
        store.Apply(SessionPacket(SessionA, forecast: [(10, Weather.LightRain, 65)]));
        var repeat = store.BuildSnapshot().Meta!.Forecast;
        store.Apply(SessionPacket(SessionA, forecast: [(10, Weather.LightRain, 90)]));
        var changed = store.BuildSnapshot().Meta!.Forecast;

        Assert.Same(first, repeat); // unchanged content must not dirty overlay state
        Assert.NotSame(first, changed);
        Assert.Equal(90, changed![0].RainPercent);
    }

    [Fact]
    public void Motion_packet_captures_world_positions()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        store.Apply(MotionPacket(SessionA, (0, 12.5f, -30.25f), (1, -40f, 100f)));
        var positions = store.BuildSnapshot().Positions;

        Assert.NotNull(positions);
        Assert.Equal(22, positions!.Count); // full grid array; renderers skip (0,0) slots
        Assert.Equal(12.5f, positions.X[0]);
        Assert.Equal(-30.25f, positions.Z[0]);
        Assert.Equal(0f, positions.X[2]); // unseen slots stay zero
        // The snapshot copy must be detached from the store's working arrays.
        store.Apply(MotionPacket(SessionA, (0, 999f, 999f)));
        Assert.NotSame(positions.X, store.BuildSnapshot().Positions!.X);
    }

    /// <summary>Motion packet with quantised g-forces (int16, game sends g × 1000).</summary>
    private static MotionDataPacket GForcePacket(ulong uid, short gLatRaw, short gLongRaw)
    {
        var cars = new CarMotionData[22];
        cars[0] = new CarMotionData
        {
            WorldForwardDirX = 0,
            WorldForwardDirZ = 1,
            GForceLateral = gLatRaw,
            GForceLongitudinal = gLongRaw,
        };

        return new MotionDataPacket { Header = Header(PacketType.Motion, uid), CarMotionData = cars };
    }

    [Fact]
    public void Motion_packet_captures_g_forces_into_player_frame()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(TelemetryPacket(SessionA, (0, 250, 1f))); // player frame needs telemetry

        store.Apply(GForcePacket(SessionA, 1500, -3200));
        var player = store.BuildSnapshot().Player;

        Assert.NotNull(player);
        Assert.Equal(1.5f, player!.GLat); // ÷1000 quantised int16 → g
        Assert.Equal(-3.2f, player.GLong);
    }

    [Fact]
    public void LapData_pit_lane_swaps_do_not_fire_overtakes()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(SwapPacket([(0, 1), (1, 2)]));
        var events = store.Apply(SwapPacket([(0, 2), (1, 1)], pitCar: 0));

        Assert.DoesNotContain(events, e => e is Overtake or RaceControl);
    }

    [Fact]
    public void LapData_first_packet_populates_grid_without_overtakes()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        var events = store.Apply(SwapPacket([(0, 1), (1, 2)]));

        Assert.DoesNotContain(events, e => e is Overtake or RaceControl);
    }

    /// <summary>Standings packet for swap tests: active, on-track cars on lap 1;
    /// optional PitStatus on one car. Unlisted cars arrive all-zero (filtered slots).</summary>
    private static LapDataPacket SwapPacket((byte Index, byte Position)[] cars, byte pitCar = 255)
    {
        var laps = new LapData[22];
        foreach (var (index, position) in cars)
        {
            laps[index] = new LapData
            {
                CarPosition = position,
                CurrentLapNum = 1,
                CurrentLapTimeInMS = 10_000,
                ResultStatus = ResultStatus.Active,
                PitStatus = index == pitCar ? PitStatus.InPitArea : PitStatus.None,
            };
        }

        return new LapDataPacket { Header = Header(PacketType.LapData, SessionA), LapData = laps };
    }

    [Fact]
    public void LapData_position_swap_fires_overtake_events()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(SwapPacket([(0, 1), (1, 2)]));
        var events = store.Apply(SwapPacket([(0, 2), (1, 1)]));

        var swap = Assert.Single(events.OfType<Overtake>());
        Assert.Equal(1, swap.CarIndex);          // car 1 passed car 0
        Assert.Equal(0, swap.PassedCarIndex);
        Assert.Equal("Car 2", swap.DriverName);
        Assert.Equal(1, swap.NewPosition);
        Assert.Equal(1, swap.LapNumber);          // SwapPacket stamps lap 1
        var feed = Assert.Single(events.OfType<RaceControl>());
        Assert.Contains("überholt", feed.Event.Text);
    }

    [Fact]
    public void Motion_before_session_leaves_positions_null()
    {
        var store = new SessionStateStore();

        var snapshot = store.BuildSnapshot();

        Assert.Null(snapshot.Positions);
    }

    [Fact]
    public void Name_override_rewrites_everywhere_and_keeps_game_name()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Car 22", Team.Mercedes, 22),
            (1, "Car 23", Team.Mercedes, 23)));

        store.SetNameOverrides(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Car 22"] = "Basti",
        });
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0, 1)));
        var snapshot = store.BuildSnapshot();

        var driver = snapshot.Drivers.Single(d => d.CarIndex == 0);
        Assert.Equal("Basti", driver.Name);
        Assert.Equal("Car 22", driver.GameName); // original name preserved for rival matching
        Assert.Equal("Car 23", snapshot.Drivers.Single(d => d.CarIndex == 1).Name);
        Assert.Equal("Basti", snapshot.Standings.Single(r => r.CarIndex == 0).Name);

        // The override survives a session change (participants re-arrive with the
        // same game names in a real lobby; the settings map outlives sessions).
        store.Apply(SessionPacket(SessionB));
        store.Apply(ParticipantsPacket(SessionB,
            (0, "Car 22", Team.Mercedes, 22),
            (1, "Car 23", Team.Mercedes, 23)));
        Assert.Equal("Basti", store.BuildSnapshot().Drivers.Single(d => d.CarIndex == 0).Name);

        store.SetNameOverrides(null);
        Assert.Equal("Car 22", store.BuildSnapshot().Drivers.Single(d => d.CarIndex == 0).Name);
    }

    [Fact]
    public void Lap_completed_event_text_uses_overridden_name()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA, (0, "Car 5", Team.Ferrari, 5)));
        store.SetNameOverrides(new Dictionary<string, string> { ["Car 5"] = "Gast" });

        var events = store.Apply(LapPacket(SessionA, (0, 1, 1, 84_500, 1_200)));

        Assert.Equal("Gast", Assert.IsType<LapCompleted>(events.Single()).DriverName);
    }

    [Fact]
    public void Session_packet_maps_pit_window_safety_car_and_marshal_zones()
    {
        var store = new SessionStateStore();

        var packet = SessionPacket(SessionA) with
        {
            PitStopWindowIdealLap = 12,
            PitStopWindowLatestLap = 16,
            NumMarshalZones = 2,
            MarshalZones = new[]
            {
                new MarshalZone { ZoneStart = 0.1f, ZoneFlag = FiaFlag.Green },
                new MarshalZone { ZoneStart = 0.6f, ZoneFlag = FiaFlag.Yellow },
            },
        };
        store.Apply(packet);

        var meta = store.BuildSnapshot().Meta!;
        Assert.Equal(12, meta.PitWindowIdealLap);
        Assert.Equal(16, meta.PitWindowLatestLap);
        Assert.Equal(2, meta.MarshalZones!.Count);
        Assert.Equal(0.1f, meta.MarshalZones[0].ZoneStart);
        Assert.Equal((int)FiaFlag.Green, meta.MarshalZones[0].Flag);
        Assert.Equal((int)FiaFlag.Yellow, meta.MarshalZones[1].Flag);
    }

    [Fact]
    public void Marshal_zone_flag_change_produces_new_list_reference()
    {
        var store = new SessionStateStore();
        var unchanged = SessionPacket(SessionA) with
        {
            NumMarshalZones = 1,
            MarshalZones = new[] { new MarshalZone { ZoneStart = 0.2f, ZoneFlag = FiaFlag.None } },
        };
        var changed = unchanged with
        {
            MarshalZones = new[] { new MarshalZone { ZoneStart = 0.2f, ZoneFlag = FiaFlag.Yellow } },
        };
        store.Apply(unchanged);
        var first = store.BuildSnapshot().Meta!.MarshalZones;

        store.Apply(unchanged);
        var repeat = store.BuildSnapshot().Meta!.MarshalZones;
        store.Apply(changed);
        var flagged = store.BuildSnapshot().Meta!.MarshalZones;

        Assert.Same(first, repeat);
        Assert.NotSame(first, flagged);
        Assert.Equal((int)FiaFlag.Yellow, flagged![0].Flag);
    }

    [Fact]
    public void Sector_marks_track_session_and_personal_bests()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        var laps = new LapData[22];
        laps[0] = new LapData { CarPosition = 1, CurrentLapNum = 1, Sector1TimeInMS = 30_000 };
        store.Apply(new LapDataPacket { Header = Header(PacketType.LapData, SessionA), LapData = laps });
        var first = store.BuildSnapshot().Standings.Single(r => r.CarIndex == 0);

        laps[1] = new LapData { CarPosition = 2, CurrentLapNum = 1, Sector1TimeInMS = 29_500 };
        store.Apply(new LapDataPacket { Header = Header(PacketType.LapData, SessionA), LapData = laps });
        var second = store.BuildSnapshot().Standings;

        // First car set the session best → purple; after the faster rival arrives it is
        // "only" a personal best → green. The rival's session best stays purple.
        Assert.Equal(SectorMark.Purple, first.S1Status);
        Assert.Equal(SectorMark.Green, second.Single(r => r.CarIndex == 0).S1Status);
        Assert.Equal(SectorMark.Purple, second.Single(r => r.CarIndex == 1).S1Status);
        Assert.Equal(SectorMark.None, first.S2Status);
    }

    [Fact]
    public void Personal_best_event_fires_when_player_beats_all_time_best()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA)); // player is car 0
        store.AllTimeBestLapMs = 90_000u; // as seeded by the App layer from the DB

        store.Apply(LapPacket(SessionA, (0, 1, 1, 90_500, 1_200))); // slower — silent
        store.Apply(LapPacket(SessionA, (0, 1, 2, 89_500, 1_100))); // beats 90_000 → PB!

        var events = store.Apply(LapPacket(SessionA, (0, 1, 3, 89_400, 1_000)));
        var pb = events.OfType<RaceControl>()
            .Single(rc => rc.Event.Type == "personal-best");
        Assert.True(pb.Event.Sequence > 0); // stamped by the EventFeed (consumers dedupe on it)
        Assert.Contains("PERSONAL BEST", pb.Event.Text);
        Assert.Equal(89_400u, store.AllTimeBestLapMs); // threshold adopted
    }

    [Fact]
    public void Lap_validity_flows_into_standings_row()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        var laps = new LapData[22];
        laps[0] = new LapData { CarPosition = 1, CurrentLapNum = 1, IsCurrentLapInvalid = true };
        store.Apply(new LapDataPacket { Header = Header(PacketType.LapData, SessionA), LapData = laps });

        Assert.Equal(1, store.BuildSnapshot().Standings.Single(r => r.CarIndex == 0).LapValidity);
    }

    [Fact]
    public void Ers_used_last_lap_is_signed_delta_between_lap_start_and_completion()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(StatusPacket(SessionA, (0, 100f, 4_000_000f)));  // store full at lap-1 start
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0, 30_000)));      // lap 1 running

        store.Apply(StatusPacket(SessionA, (0, 97f, 3_600_000f)));   // 400 kJ deployed
        var events = store.Apply(LapPacket(SessionA, (0, 1, 2, 84_500, 500)));

        var lap = Assert.Single(events.OfType<LapCompleted>());
        Assert.Equal(400_000f, lap.ErsUsedJoules, 0.5f);
        // The crossing packet already carries the finished lap's consumption.
        Assert.Equal(400_000f, store.BuildSnapshot().Standings.Single(r => r.CarIndex == 0).ErsUsedLastLapJ, 0.5f);
    }

    [Fact]
    public void Ers_used_defaults_to_zero_without_lap_start_seed()
    {
        // Lap 1 completes before any CarStatus packet seeded the lap-start store level —
        // the unseeded guard must report 0 instead of a bogus huge delta.
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0, 30_000)));

        store.Apply(StatusPacket(SessionA, (0, 100f, 1_000_000f)));
        var events = store.Apply(LapPacket(SessionA, (0, 1, 2, 84_500, 500)));

        var lap = Assert.Single(events.OfType<LapCompleted>());
        Assert.Equal(0f, lap.ErsUsedJoules);
    }

    [Fact]
    public void Fuel_used_last_lap_computed_on_lap_completion()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(StatusPacket(SessionA, (0, 100f, 4000)));   // fuel at lap-1 start
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0, 30_000))); // lap 1 running

        store.Apply(StatusPacket(SessionA, (0, 97f, 4000)));    // 3 litres burnt
        var events = store.Apply(LapPacket(SessionA, (0, 1, 2, 84_500, 500)));

        Assert.Single(events.OfType<LapCompleted>());
        // The crossing packet already carries the finished lap's consumption.
        Assert.Equal(3f, store.BuildSnapshot().Standings.Single(r => r.CarIndex == 0).FuelUsedLastLap, 2);
    }

    [Fact]
    public void Tyre_sets_captured_for_player_only()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA, playerIndex: 0));

        var bank = new TyreSetData[20];
        bank[0] = new TyreSetData { Wear = 5, IsAvailable = true };
        bank[1] = new TyreSetData { Wear = 0, IsAvailable = true };
        store.Apply(new TyreSetsDataPacket
        {
            Header = Header(PacketType.TyreSets, SessionA),
            CarIndex = 0,
            TyreSetDatas = bank,
            FittedIndex = 1,
        });
        var sets = store.BuildSnapshot().TyreSets!;

        Assert.Equal(20, sets.Count);
        Assert.False(sets[0].IsFitted);
        Assert.True(sets[1].IsFitted);
        Assert.Equal(5, sets[0].Wear);

        // The packet cycles through cars — a rival's bank must not overwrite the player's.
        store.Apply(new TyreSetsDataPacket
        {
            Header = Header(PacketType.TyreSets, SessionA),
            CarIndex = 3,
            TyreSetDatas = bank,
        });
        Assert.True(store.BuildSnapshot().TyreSets![1].IsFitted);
    }

    [Fact]
    public void Car_damage_packet_maps_wings_floor_diffuser_and_drs_fault()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        var damages = new CarDamageData[22];
        damages[0] = new CarDamageData
        {
            FrontLeftWingDamage = 40,
            FrontRightWingDamage = 10,
            RearWingDamage = 55,
            FloorDamage = 25,
            DiffuserDamage = 80,
            DrsFault = true,
        };
        store.Apply(new CarDamageDataPacket
        {
            Header = Header(PacketType.CarDamage, SessionA),
            CarDamageData = damages,
        });

        var damage = store.BuildSnapshot().Damages!.Single(d => d.CarIndex == 0);
        Assert.Equal(40, damage.FrontLeftWing);
        Assert.Equal(10, damage.FrontRightWing);
        Assert.Equal(55, damage.RearWing);
        Assert.Equal(25, damage.Floor);
        Assert.Equal(80, damage.Diffuser);
        Assert.True(damage.DrsFault);
    }

    [Fact]
    public void Penalty_events_carry_the_game_reported_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA, (0, "Driver", Team.Ferrari, 5)));

        var events = store.Apply(EventPacket(SessionA, new PenaltyEvent
        {
            VehicleIdx = 0,
            PenaltyType = PenaltyType.TimePenalty,
            Time = 5,
            LapNum = 7,
        }));

        var rc = Assert.Single(events.OfType<RaceControl>());
        Assert.Equal(7, rc.Event.LapNumber);
        Assert.Equal("Penalty", rc.Event.Type);
        Assert.Contains("TimePenalty", rc.Event.Text);
        Assert.Contains("(5s)", rc.Event.Text);
        Assert.Contains("BlockingBySlowDriving", rc.Event.Text); // infringement value 0
    }

    [Fact]
    public void Other_events_are_stamped_with_the_involved_cars_current_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "Player", Team.Ferrari, 5), (1, "Rival", Team.McLaren, 7)));
        store.Apply(LapPacket(SessionA, (0, 1, 4, 0, 12_000), (1, 2, 4, 0, 12_100)));

        var events = store.Apply(EventPacket(SessionA, new OvertakeEvent
        {
            OvertakingVehicleIdx = 0,
            BeingOvertakenVehicleIdx = 1,
        }));

        var rc = Assert.Single(events.OfType<RaceControl>());
        Assert.Equal(4, rc.Event.LapNumber); // car 0 is on lap 4 when the overtake fires
    }

    [Fact]
    public void Carless_events_are_stamped_with_the_players_current_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA, playerIndex: 1));
        store.Apply(ParticipantsPacket(SessionA,
            (0, "P2", Team.Ferrari, 5), (1, "Player", Team.McLaren, 7)));
        store.Apply(LapPacket(SessionA, (1, 2, 9, 0, 12_000)));

        var events = store.Apply(EventPacket(SessionA, new RaceWinnerEvent { VehicleIdx = 1 }));

        var rc = Assert.Single(events.OfType<RaceControl>());
        Assert.Equal(9, rc.Event.LapNumber);
    }

    [Fact]
    public void PlayerCarIndex_255_no_active_player_does_not_crash()
    {
        // The game sends PlayerCarIndex=255 while no player is active (lobby/spectating).
        // Every path that indexes the 22-slot arrays by player must guard against it.
        var store = new SessionStateStore();

        store.Apply(SessionPacket(SessionA, playerIndex: 255));
        store.Apply(ParticipantsPacket(SessionA, (0, "Driver", Team.Ferrari, 5)));
        store.Apply(TelemetryPacket(SessionA, (0, 200, 0.5f)));
        store.Apply(new MotionExDataPacket
        {
            Header = Header(PacketType.MotionEx, SessionA, playerIndex: 255),
            WheelSlipRatio = new Tyres<float> { FrontLeft = 1f, FrontRight = 1f, RearLeft = 1f, RearRight = 1f },
            FrontAeroHeight = 0.05f,
            RearAeroHeight = 0.08f,
        });

        var snapshot = store.BuildSnapshot();
        Assert.Null(snapshot.Player);
        Assert.Null(snapshot.Rival);
        Assert.Null(snapshot.Strategy);
        Assert.Null(snapshot.Fuel);
    }
}