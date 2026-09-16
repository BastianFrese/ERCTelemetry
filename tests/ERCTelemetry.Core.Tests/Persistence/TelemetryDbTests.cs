using F1Game.UDP.Enums;
using Microsoft.Data.Sqlite;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>Round-trip tests on a temp .db file: schema, session lifecycle, lap storage
/// + idempotency, crash recovery (abandon), read APIs.</summary>
public sealed class TelemetryDbTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"f1telemetry-test-{Guid.NewGuid():N}.db");

    private readonly TelemetryDb _db;

    public TelemetryDbTests()
    {
        _db = new TelemetryDb(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_path);
    }

    private static SessionMeta Meta => new(
        SessionUid: 42_000,
        SessionType: SessionType.Race,
        Track: Track.Spa,
        TotalLaps: 44,
        TrackLength: 7004,
        IsNetworkGame: true,
        PlayerCarIndex: 3,
        GameMode: GameMode.OnlineCustom,
        Weather: Weather.LightRain,
        TrackTemperature: 27,
        AirTemperature: 19,
        SessionTimeLeft: 0);

    [Fact]
    public void Career_stats_aggregate_player_rows_across_races()
    {
        var id1 = _db.OpenSession(Meta with { SessionUid = 1, Track = Track.Spa });
        _db.FinalizeSession(id1, "final classification",
        [
            new FinalResultRow(1, 3, "Player One", Team.McLaren, 81, 44, 2, 25f,
                ResultStatus.Finished, 90_123, 3600.5, 0, 0),
            new FinalResultRow(2, 4, "Rival", Team.RedBullRacing, 1, 44, 1, 18f,
                ResultStatus.Finished, 90_500, 3601, 0, 0),
        ]);
        var id2 = _db.OpenSession(Meta with { SessionUid = 2, Track = Track.Monza });
        _db.FinalizeSession(id2, "final classification",
        [
            new FinalResultRow(4, 3, "Player One", Team.McLaren, 81, 44, 8, 12f,
                ResultStatus.Finished, 89_500, 3630, 0, 0),
        ]);

        var stats = _db.GetCareerStats();

        Assert.Equal(2, stats.Races);
        Assert.Equal(1, stats.Wins);
        Assert.Equal(1, stats.Podiums);
        Assert.Equal(5.0, stats.AverageGridPosition); // (2 + 8) / 2
        Assert.Equal(37.0, stats.Points);
        Assert.Equal((uint)89_500, stats.BestLapMs);
        // Spa: player avg position 1.0 beats Monza's 4.0.
        Assert.Equal("Spa", stats.BestTrack);
    }

    [Fact]
    public void Career_stats_empty_when_no_races()
    {
        var stats = _db.GetCareerStats();

        Assert.Equal(0, stats.Races);
        Assert.Equal(string.Empty, stats.BestTrack);
        Assert.Equal(0u, stats.BestLapMs);
    }

    [Fact]
    public void OpenSession_and_FinalizeSession_round_trip()
    {
        var id = _db.OpenSession(Meta);
        _db.FinalizeSession(id, "final classification",
        [
            new FinalResultRow(1, 3, "Player One", Team.McLaren, 81, 44, 2, 25f,
                ResultStatus.Finished, 90_123, 3600.5, 0, 0),
        ]);

        var summary = Assert.Single(_db.GetSessions());
        Assert.Equal(42_000ul, summary.SessionUid);
        Assert.Equal("Race", summary.SessionType);
        Assert.Equal("Spa", summary.Track);
        Assert.Equal(1, summary.Finalized);
        Assert.Equal(0, summary.LapCount);
        Assert.Equal(0, summary.DriverCount);

        var result = Assert.Single(_db.GetResults(id));
        Assert.Equal("Player One", result.Name);
        Assert.Equal(Team.McLaren, result.Team);
        Assert.Equal((uint)90_123, result.BestLapTimeMs);
        Assert.Equal(25f, result.Points);
        Assert.Equal(ResultStatus.Finished, result.ResultStatus);
    }

    [Fact]
    public void Laps_round_trip_and_are_idempotent()
    {
        var id = _db.OpenSession(Meta);
        var lap1 = new LapCompleted(3, "Player One", 1, 91_000, 33_110, 30_220,
            ActualCompound.F1C3, 1, 4);
        var lap2 = new LapCompleted(3, "Player One", 2, 90_500, 32_980, 30_100,
            ActualCompound.F1C3, 2, 3, ErsUsedJoules: 400_000f);
        var rivalLap = new LapCompleted(9, "Rival Driver", 1, 90_900, 33_000, 30_500,
            ActualCompound.F1C4, 1, 5);

        _db.AppendLap(id, lap1);
        _db.AppendLap(id, lap1); // same (session, car, lap) again — must not duplicate
        _db.AppendLap(id, lap2);
        _db.AppendLap(id, rivalLap);

        var laps = _db.GetLaps(id);
        Assert.Equal(3, laps.Count);
        var first = laps[0];
        Assert.Equal(3, first.CarIndex);
        Assert.Equal(1, first.LapNumber);
        Assert.Equal((uint)91_000, first.LapTimeMs);
        Assert.Equal((ushort)33_110, first.Sector1TimeMs);
        Assert.Equal(ActualCompound.F1C3, first.TyreCompound);
        var rival = laps[2];
        Assert.Equal(9, rival.CarIndex);
        Assert.Equal((byte)5, rival.Position);
        Assert.Equal(400_000f, lap2.ErsUsedJoules, 0.5f); // needs the lap carrying it — see ctor below
    }

    [Fact]
    public void New_session_abandons_an_open_one()
    {
        _db.OpenSession(Meta with { SessionUid = 1 });
        _db.OpenSession(Meta with { SessionUid = 2 });

        var abandoned = _db.GetSessions().Single(s => s.SessionUid == 1ul);
        Assert.Equal(2, abandoned.Finalized); // 2 = abandoned by the next OpenSession
        var kept = _db.GetSessions().Single(s => s.SessionUid == 2ul);
        Assert.Equal(0, kept.Finalized); // the new session stays open
    }

    [Fact]
    public void OpenSession_reuses_the_row_when_uid_is_resent()
    {
        // Rejoin case (seen live): the game re-sends the same session_uid after a
        // reconnect — the open row must be reused, not crash on the UNIQUE constraint.
        var id1 = _db.OpenSession(Meta with { SessionUid = 7 });
        _db.AppendLap(id1, new LapCompleted(3, "Player", 1, 91_000, 0, 0, 0, 1, 4));
        var id2 = _db.OpenSession(Meta with { SessionUid = 7 });

        Assert.Equal(id1, id2);
        Assert.Single(_db.GetSessions()); // still exactly one row for this uid
        Assert.Single(_db.GetLaps(id2)); // laps of the rejoined session survive
    }

    [Fact]
    public void OpenSession_reopens_a_finalized_row_when_uid_is_resent()
    {
        // Pause / lobby case: the idle timeout finalized the row, then the game resumes
        // with the same session_uid — the row must be re-opened so laps keep flowing.
        var id1 = _db.OpenSession(Meta with { SessionUid = 8 });
        _db.FinalizeSession(id1, "Idle", []);
        Assert.Equal(1, _db.GetSessions().Single(s => s.SessionUid == 8ul).Finalized);

        var id2 = _db.OpenSession(Meta with { SessionUid = 8 });

        Assert.Equal(id1, id2);
        Assert.Equal(0, _db.GetSessions().Single(s => s.SessionUid == 8ul).Finalized);
    }

    [Fact]
    public void Events_persist_with_sequence_and_utc()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 15, TimeSpan.Zero),
            "fastest-lap", 9, "Rival Driver · 1:30.001", 7));

        var evt = Assert.Single(_db.GetEvents(id));
        Assert.Equal(7, evt.Sequence);
        Assert.Equal("fastest-lap", evt.Type);
        Assert.Equal((byte)9, evt.CarIndex);
        Assert.Equal(2026, evt.Utc.Year);
    }

    [Fact]
    public void Events_persist_the_lap_they_happened_on()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 15, TimeSpan.Zero),
            "Penalty", 9, "Rival: TimePenalty (5s) · PitLaneSpeeding", 7, LapNumber: 12));
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 31, 0, TimeSpan.Zero),
            "SafetyCar", null, "Safety car deployed", 8)); // no lap known → 0

        var events = _db.GetEvents(id);
        Assert.Equal(2, events.Count);
        Assert.Equal(12, events[0].LapNumber);
        Assert.Equal(0, events[1].LapNumber);
    }

    [Fact]
    public void Events_persist_the_distance_around_lap()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 15, TimeSpan.Zero),
            "Warning", 3, "Player: Verwarnung (Abkürzen) — gesamt 2", 7, LapNumber: 4,
            LapDistance: 4210.5f));
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 31, 0, TimeSpan.Zero),
            "SafetyCar", null, "Safety car deployed", 8)); // no distance known → -1

        var events = _db.GetEvents(id);
        Assert.Equal(4210.5f, events[0].LapDistance, 0.5f);
        Assert.Equal(-1f, events[1].LapDistance);
    }

    [Fact]
    public void Events_filter_to_the_player_plus_collisions_they_are_part_of()
    {
        var id = _db.OpenSession(Meta); // player car index 3
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 15, TimeSpan.Zero),
            "Penalty", 3, "Player: TimePenalty", 1, LapNumber: 2));
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 20, TimeSpan.Zero),
            "Collision", 9, "Rival A vs Rival B", 2, SecondCarIndex: 4)); // both others
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 25, TimeSpan.Zero),
            "Collision", 9, "Rival A hits player", 3, SecondCarIndex: 3)); // + player
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 30, TimeSpan.Zero),
            "SafetyCar", null, "Safety car deployed", 4)); // session-wide, no car

        var playerEvents = _db.GetEvents(id, playerCarIndex: 3);

        Assert.Equal(2, playerEvents.Count); // own penalty + collision with player
        Assert.Equal("Penalty", playerEvents[0].Type);
        Assert.Equal("Collision", playerEvents[1].Type);
        Assert.Equal((byte)9, playerEvents[1].CarIndex);
    }

    [Fact]
    public void Events_are_unfiltered_without_a_player_index()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 15, TimeSpan.Zero),
            "Penalty", 9, "Rival: TimePenalty", 1));
        _db.AppendEvent(id, new RaceEventEntry(
            new DateTimeOffset(2026, 3, 29, 14, 30, 20, TimeSpan.Zero),
            "SafetyCar", null, "Safety car deployed", 2));

        Assert.Equal(2, _db.GetEvents(id).Count); // default = full timeline
    }

    [Fact]
    public void Session_cards_return_player_best_lap_team_and_position()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendLap(id, new LapCompleted(3, "Player One", 1, 91_000, 33_110, 30_220,
            ActualCompound.F1C3, 1, 2));
        _db.FinalizeSession(id, "final classification",
        [
            new FinalResultRow(2, 3, "Player One", Team.McLaren, 81, 44, 2, 25f,
                ResultStatus.Finished, 90_123, 3600.5, 0, 0),
        ]);

        var card = Assert.Single(_db.GetSessionCards(new TelemetryDb.SessionCardFilter()).Items);

        Assert.Equal(id, card.Id);
        Assert.Equal("Race", card.SessionType);
        Assert.Equal("Spa", card.Track);
        Assert.Equal(1, card.Finalized);
        Assert.Equal((uint)90_123, card.PlayerBestLapMs); // results row wins over laps
        Assert.Equal("McLaren", card.PlayerTeam);
        Assert.Equal(2, card.PlayerPosition);
    }

    [Fact]
    public void Session_cards_fall_back_to_min_lap_when_not_finalized()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendLap(id, new LapCompleted(3, "Player One", 1, 91_000, 0, 0, 0, 1, 4));
        _db.AppendLap(id, new LapCompleted(3, "Player One", 2, 90_500, 0, 0, 0, 2, 3));
        _db.AppendLap(id, new LapCompleted(9, "Rival", 1, 80_000, 0, 0, 0, 1, 1));

        var card = Assert.Single(_db.GetSessionCards(new TelemetryDb.SessionCardFilter()).Items);

        Assert.Equal(0, card.Finalized);
        Assert.Equal((uint)90_500, card.PlayerBestLapMs); // player's own laps, rival's 80s ignored
        Assert.Equal(string.Empty, card.PlayerTeam); // no results/drivers team yet
        Assert.Equal(0, card.PlayerPosition);
    }

    [Fact]
    public void Session_cards_show_race_length_not_all_cars_laps()
    {
        var id = _db.OpenSession(Meta); // player car index 3
        for (byte lap = 1; lap <= 5; lap++)
        {
            _db.AppendLap(id, new LapCompleted(3, "Player", lap, 91_000, 0, 0, 0, 1, lap));
            _db.AppendLap(id, new LapCompleted(9, "Rival", lap, 90_000, 0, 0, 0, 1, lap));
        }

        var card = Assert.Single(_db.GetSessionCards(new TelemetryDb.SessionCardFilter()).Items);

        // 2 cars × 5 laps used to be "10 Runden" (and 22 cars × 5 = 110 in a real race).
        Assert.Equal(5, card.LapCount);
    }

    [Fact]
    public void Session_cards_without_a_player_show_the_leaders_laps()
    {
        var id = _db.OpenSession(Meta with { PlayerCarIndex = 255 }); // e.g. spectator session
        for (byte lap = 1; lap <= 11; lap++)
        {
            _db.AppendLap(id, new LapCompleted(7, "Leader", lap, 90_000, 0, 0, 0, 1, lap));
        }

        var card = Assert.Single(_db.GetSessionCards(new TelemetryDb.SessionCardFilter()).Items);

        Assert.Equal(11, card.LapCount); // player-scoped count would collapse to 0
    }

    [Fact]
    public void Session_cards_filter_by_track_type_and_time_range()
    {
        var spaRace = _db.OpenSession(Meta with { SessionUid = 1 });
        var monzaRace = _db.OpenSession(Meta with { SessionUid = 2, Track = Track.Monza });
        var spaQuali = _db.OpenSession(Meta with
        {
            SessionUid = 3, Track = Track.Spa, SessionType = SessionType.ShortQualifying,
        });

        // started_utc is stamped at OpenSession (now) — pin historical dates via raw SQL.
        SetStartedUtc(spaRace, "2026-08-01T10:00:00.0000000Z");
        SetStartedUtc(monzaRace, "2026-08-20T10:00:00.0000000Z");
        SetStartedUtc(spaQuali, "2026-08-25T10:00:00.0000000Z");

        Assert.Equal(["Monza", "Spa"], _db.GetDistinctTracks()); // alphabetical
        Assert.Equal(["Race", "ShortQualifying"], _db.GetDistinctSessionTypes());

        var byTrack = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(Track: "Spa")).Items;
        Assert.Equal(2, byTrack.Count);
        Assert.All(byTrack, c => Assert.Equal("Spa", c.Track));

        var byType = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(SessionType: "ShortQualifying")).Items;
        var card = Assert.Single(byType);
        Assert.Equal(3, card.Id);

        var since = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
        var recent = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(StartedFromUtc: since)).Items;
        Assert.Equal(2, recent.Count); // Monza-Race + Spa-Qualifying, Race (1.8.) ausgeschlossen
        Assert.Equal("ShortQualifying", recent[0].SessionType); // newest first
    }

    [Fact]
    public void Session_cards_paginate_with_limit_offset_and_total_count()
    {
        for (var i = 1; i <= 5; i++)
        {
            _db.OpenSession(Meta with { SessionUid = (ulong)i });
        }

        var page0 = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(Page: 0, PageSize: 2));
        var page1 = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(Page: 1, PageSize: 2));
        var page2 = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(Page: 2, PageSize: 2));
        var page3 = _db.GetSessionCards(new TelemetryDb.SessionCardFilter(Page: 3, PageSize: 2));

        Assert.Equal(5, page0.TotalCount);
        Assert.Equal(2, page0.Items.Count);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.Empty(page3.Items);
        Assert.NotEqual(page0.Items[0].Id, page1.Items[0].Id); // pages do not overlap
    }

    private void SetStartedUtc(long sessionId, string isoUtc)
    {
        using var raw = new SqliteConnection($"Data Source={_path};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET started_utc = @p1 WHERE id = @p2";
        var p1 = cmd.CreateParameter();
        p1.ParameterName = "p1";
        p1.Value = isoUtc;
        cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter();
        p2.ParameterName = "p2";
        p2.Value = sessionId;
        cmd.Parameters.Add(p2);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Overtakes_round_trip_per_session()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendOvertake(id, new Overtake(
            3, "Player", 5, "Rival", 2, 12));

        var overtake = Assert.Single(_db.GetOvertakes(id));
        Assert.Equal(12, overtake.LapNumber);
        Assert.Equal((byte)3, overtake.CarIndex);
        Assert.Equal("Player", overtake.DriverName);
        Assert.Equal((byte)5, overtake.PassedCarIndex);
        Assert.Equal("Rival", overtake.PassedDriverName);
        Assert.Equal((byte)2, overtake.NewPosition);
        Assert.Equal(2026, overtake.Utc.Year); // now-stamped
        Assert.Empty(_db.GetOvertakes(id + 1)); // other sessions see nothing
    }

    [Fact]
    public void Overtakes_filter_to_overtakes_the_player_took_part_in()
    {
        var id = _db.OpenSession(Meta); // player car index 3
        _db.AppendOvertake(id, new Overtake(3, "Player", 5, "Rival", 2, 12));   // player overtook
        _db.AppendOvertake(id, new Overtake(1, "Rival A", 3, "Player", 4, 13)); // player overtaken
        _db.AppendOvertake(id, new Overtake(1, "Rival A", 5, "Rival", 3, 14));  // no player

        var playerOvertakes = _db.GetOvertakes(id, playerCarIndex: 3);

        Assert.Equal(2, playerOvertakes.Count);
        Assert.All(playerOvertakes, o => Assert.True(o.CarIndex == 3 || o.PassedCarIndex == 3));
    }

    [Fact]
    public void Existing_databases_get_the_lap_number_column()
    {
        // Simulate a DB created before the events.lap_number column existed: build the
        // old table shape manually, then open TelemetryDb on it (EnsureSchema migrates).
        var oldPath = Path.Combine(Path.GetTempPath(), $"f1telemetry-mig-{Guid.NewGuid():N}.db");
        try
        {
            using (var raw = new SqliteConnection($"Data Source={oldPath};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE events (
                        id INTEGER PRIMARY KEY,
                        session_id INTEGER NOT NULL,
                        seq INTEGER NOT NULL,
                        utc TEXT NOT NULL,
                        type TEXT NOT NULL,
                        text TEXT NOT NULL,
                        car_index INTEGER
                    );
                    INSERT INTO events (session_id, seq, utc, type, text, car_index)
                    VALUES (1, 1, '2026-03-29T14:30:15.0000000+00:00', 'Penalty', 'old row', 2);
                    """;
                cmd.ExecuteNonQuery();
            }

            using var db = new TelemetryDb(oldPath);
            var old = Assert.Single(db.GetEvents(1)); // migrated row reads back with lap 0
            Assert.Equal(0, old.LapNumber);

            db.AppendEvent(1, new RaceEventEntry(
                new DateTimeOffset(2026, 3, 29, 15, 0, 0, TimeSpan.Zero),
                "Penalty", 2, "new row", 2, LapNumber: 9));
            Assert.Contains(db.GetEvents(1), e => e.LapNumber == 9);
        }
        finally
        {
            File.Delete(oldPath);
        }
    }

    [Fact]
    public void Track_bests_keep_only_the_fastest_lap()
    {
        Assert.Equal(0u, _db.GetTrackBestLapMs("Spa")); // unknown track → 0

        _db.UpsertTrackBestLap("Spa", 91_000);
        _db.UpsertTrackBestLap("Spa", 90_500); // faster → replaces
        _db.UpsertTrackBestLap("Spa", 92_000); // slower → keeps 90_500
        _db.UpsertTrackBestLap("Monza", 81_500);

        Assert.Equal(90_500u, _db.GetTrackBestLapMs("Spa"));
        Assert.Equal(81_500u, _db.GetTrackBestLapMs("Monza"));
        Assert.Equal(0u, _db.GetTrackBestLapMs("UnknownTrack")); // never set
    }

    [Fact]
    public void Lap_traces_round_trip_and_are_idempotent()
    {
        var id = _db.OpenSession(Meta);
        var samples = new List<ERCTelemetry.Core.Analysis.LapTraceSample>
        {
            new(0f, 280), new(20f, 283), new(40f, 150), new(60f, 90),
        };
        var trace = new ERCTelemetry.Core.Analysis.LapTrace(7, 91_500, 7004, samples);

        _db.AppendLapTrace(id, 3, trace);
        _db.AppendLapTrace(id, 3, trace); // replay -> ignored

        var loaded = _db.GetLapTrace(id, 3, 7);
        Assert.NotNull(loaded);
        Assert.Equal((byte)7, loaded!.LapNumber);
        Assert.Equal(91_500u, loaded.LapTimeMs);
        Assert.Equal((ushort)7004, loaded.TrackLength);
        Assert.Equal(4, loaded.Samples.Count);
        Assert.Equal(20f, loaded.Samples[1].LapDistance);
        Assert.Equal(150, loaded.Samples[2].Speed);

        var summaries = _db.GetLapTraceSummaries(id);
        Assert.Single(summaries);
        Assert.Equal((byte)3, summaries[0].CarIndex);
        Assert.Equal((byte)7, summaries[0].LapNumber);
        Assert.Equal(91_500u, summaries[0].LapTimeMs);

        Assert.Null(_db.GetLapTrace(id, 3, 8)); // never traced
        Assert.Null(_db.GetLapTrace(id, 5, 7)); // wrong car
    }

    [Fact]
    public void Best_lap_trace_picks_fastest_lap_on_the_track_across_sessions()
    {
        // Two laps on Spa (fast one second), one slower lap on Monza (must not win).
        var spa1 = _db.OpenSession(Meta with { SessionUid = 1, Track = Track.Spa });
        var spa2 = _db.OpenSession(Meta with { SessionUid = 2, Track = Track.Spa });
        var monza = _db.OpenSession(Meta with { SessionUid = 3, Track = Track.Monza });
        _db.AppendLapTrace(spa1, 3, new ERCTelemetry.Core.Analysis.LapTrace(2, 93_000, 7004,
            [new(0f, 250), new(20f, 240)]));
        _db.AppendLapTrace(spa2, 3, new ERCTelemetry.Core.Analysis.LapTrace(5, 91_500, 7004,
            [new(0f, 260), new(20f, 250)]));
        _db.AppendLapTrace(monza, 3, new ERCTelemetry.Core.Analysis.LapTrace(9, 80_000, 5793,
            [new(0f, 300), new(20f, 290)]));

        var best = _db.GetBestLapTrace("Spa");

        Assert.NotNull(best);
        Assert.Equal(91_500u, best!.LapTimeMs);
        Assert.Equal((byte)5, best.Trace.LapNumber);
        Assert.Equal((ushort)7004, best.Trace.TrackLength);
        Assert.Equal(2, best.Trace.Samples.Count);
        Assert.False(string.IsNullOrEmpty(best.SessionType));
        Assert.False(string.IsNullOrEmpty(best.StartedUtc));

        Assert.Equal(80_000u, _db.GetBestLapTrace("Monza")!.LapTimeMs);
        Assert.Null(_db.GetBestLapTrace("Silverstone"));
    }

    [Fact]
    public void Lap_traces_round_trip_pedals_and_read_legacy_flat_rows()
    {
        var id = _db.OpenSession(Meta with { SessionUid = 7, Track = Track.Spa });
        var trace = new ERCTelemetry.Core.Analysis.LapTrace(3, 92_000, 7004,
        [
            new(0f, 280, 0.9f, 0f),
            new(20f, 240, 0.3f, 0.6f),
            new(40f, 150, 0f, 1f),
        ]);
        _db.AppendLapTrace(id, 3, trace);

        var loaded = _db.GetLapTrace(id, 3, 3);
        Assert.NotNull(loaded);
        Assert.Equal(0.9f, loaded!.Samples[0].Throttle, 3);
        Assert.Equal(0.6f, loaded.Samples[1].Brake, 3);
        Assert.Equal(1f, loaded.Samples[2].Brake, 3);

        // A legacy flat row (pre-pedal format) still decodes — pedals stay 0.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE lap_traces SET samples = @p1 WHERE lap_number = 3";
            var p = cmd.CreateParameter();
            p.ParameterName = "@p1";
            p.Value = "[0,280,20,240,40,150]";
            cmd.Parameters.Add(p);
            cmd.ExecuteNonQuery();
        }

        var legacy = _db.GetLapTrace(id, 3, 3);
        Assert.NotNull(legacy);
        Assert.Equal(3, legacy!.Samples.Count);
        Assert.Equal(0f, legacy.Samples[0].Throttle);
        Assert.Equal(0f, legacy.Samples[2].Brake);
        Assert.Equal(150, legacy.Samples[2].Speed);
    }

    [Fact]
    public void Best_lap_trace_skips_zero_time_and_corrupt_traces()
    {
        var id = _db.OpenSession(Meta with { SessionUid = 4, Track = Track.Spa });
        _db.AppendLapTrace(id, 3, new ERCTelemetry.Core.Analysis.LapTrace(1, 0, 7004,
            [new(0f, 250)])); // lap_time_ms = 0 -> not a reference

        Assert.Null(_db.GetBestLapTrace("Spa"));
    }

    [Fact]
    public void Lap_traces_ignore_corrupt_json_and_bad_values()
    {
        var id = _db.OpenSession(Meta);
        _db.AppendLapTrace(id, 3, new ERCTelemetry.Core.Analysis.LapTrace(
            2, 80_000, 7004, [new ERCTelemetry.Core.Analysis.LapTraceSample(0f, 250)]));

        // Hand-edit the samples column to junk JSON via a second connection.
        using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE lap_traces SET samples = @p1 WHERE lap_number = 2";
            var p = cmd.CreateParameter();
            p.ParameterName = "@p1";
            p.Value = "[0,250,not-a-number]";
            cmd.Parameters.Add(p);
            cmd.ExecuteNonQuery();
        }

        Assert.Null(_db.GetLapTrace(id, 3, 2));
    }
}