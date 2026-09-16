using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>Phase 2 gate: old-schema migration, round-trips through the new tables and
/// write-order independence (MergeLapHistory before/after AppendLap).</summary>
public class TelemetryDbReportTests : IDisposable
{
    private readonly TelemetryDb _db;

    public TelemetryDbReportTests() =>
        _db = new TelemetryDb(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db"));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Old_schema_db_is_upgraded_in_place()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _db.Dispose();
        File.WriteAllBytes(path, []);
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        using var create = raw.CreateCommand();
        create.CommandText = """
            CREATE TABLE sessions (
                id INTEGER PRIMARY KEY, session_uid INTEGER NOT NULL UNIQUE, session_type TEXT NOT NULL,
                track TEXT NOT NULL, total_laps INTEGER NOT NULL, track_length INTEGER NOT NULL,
                game_mode TEXT NOT NULL, weather TEXT NOT NULL, is_network_game INTEGER NOT NULL,
                player_car_index INTEGER NOT NULL, track_temp INTEGER NOT NULL, air_temp INTEGER NOT NULL,
                started_utc TEXT NOT NULL, finalized INTEGER NOT NULL, end_reason TEXT);
            """;
        create.ExecuteNonQuery();

        // laps table: pre-report shape (no report columns).
        using var lapsTable = raw.CreateCommand();
        lapsTable.CommandText = """
            CREATE TABLE laps (
                id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL, lap_number INTEGER NOT NULL, lap_time_ms INTEGER NOT NULL,
                sector1_ms INTEGER NOT NULL, tyre TEXT NOT NULL,
                tyre_age_laps INTEGER NOT NULL, position INTEGER NOT NULL,
                UNIQUE(session_id, car_index, lap_number));
            """;
        lapsTable.ExecuteNonQuery();
        raw.Dispose();

        // Opening upgrades in place — probe via the public API.
        using var upgraded = new TelemetryDb(path);
        Assert.True(upgraded.HasTable("tyre_stints"));
        Assert.True(upgraded.HasTable("lap_positions"));
        Assert.True(upgraded.HasTable("car_damage_log"));
        Assert.True(upgraded.HasTable("lap_motion_summary"));
        Assert.True(upgraded.HasTable("setups"));
        Assert.True(upgraded.HasColumn("sessions", "time_of_day"));
        Assert.True(upgraded.HasColumn("laps", "sector3_ms"));
        Assert.True(upgraded.HasColumn("results", "result_reason"));
        Assert.True(upgraded.HasColumn("events", "second_car_index"));
        Assert.True(upgraded.HasColumn("laps", "active_aero_mode"));
    }

    [Fact]
    public void Session_round_trip_with_extras()
    {
        var meta = new SessionMeta(
            77, SessionType.Race, Track.Bahrain, 12, 5412, true, 3,
            GameMode.OnlineCustom, Weather.LightRain, 31, 27, 0);
        var sessionId = _db.OpenSession(meta);
        _db.UpdateSessionExtras(meta with
        {
            TimeOfDay = 720,
            Formula = FormulaType.F1Modern,
            PitSpeedLimit = 80,
            Sector2LapDistanceStart = 2500.5f,
            Sector3LapDistanceStart = 4100f,
            NumDrsZones = 2,
            DrsZones = [new DrsZoneInfo(0.1f, 0.2f), new DrsZoneInfo(0.6f, 0.75f)],
            RuleSet = RuleSet.Race,
            SessionDuration = 5400,
        });

        var row = _db.GetSession(sessionId);
        Assert.NotNull(row);
        Assert.Equal(720, row.TimeOfDay);
        Assert.Equal("F1Modern", row.Formula); // stored as the enum name
        Assert.Equal(80, row.PitSpeedLimit);
        Assert.Equal(2500.5f, row.Sector2Start, precision: 3);
        Assert.Equal(4100f, row.Sector3Start, precision: 3);
        Assert.Equal(2, row.NumDrsZones);
        Assert.Contains("0.2", row.DrsZonesJson);
        Assert.Equal("Race", row.RuleSet); // stored as the enum name
        Assert.Equal(5400, row.SessionDuration);
    }

    [Fact]
    public void UpdateSessionExtras_without_drs_zones_does_not_throw()
    {
        // The Session packet may carry no DRS zones (null DrsZones) — the extras UPDATE
        // must bind NULL, not throw "Value must be set" (Microsoft.Data.Sqlite cannot
        // infer a type from a null string). A throw here aborts the SessionStarted
        // handler, so the session never finalizes and never shows in history.
        var meta = new SessionMeta(
            77, SessionType.Race, Track.Bahrain, 12, 5412, true, 3,
            GameMode.OnlineCustom, Weather.LightRain, 31, 27, 0);
        var sessionId = _db.OpenSession(meta);

        _db.UpdateSessionExtras(meta); // DrsZones is null — must not throw

        var row = _db.GetSession(sessionId);
        Assert.NotNull(row);
        Assert.Null(row.DrsZonesJson);
        Assert.Equal(0, row.NumDrsZones);
    }

    [Fact]
    public void Merge_lap_history_is_order_independent_with_append_lap()
    {
        var meta = new SessionMeta(9, SessionType.Race, Track.Bahrain, 5, 5412, false, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);
        var sessionId = _db.OpenSession(meta);

        // History first: upserts the full row (S3 + validity), tyre unknown yet.
        _db.MergeLapHistory(sessionId, new LapHistoryUpdated(2, 1,
            [new LapHistoryRow(1, 91_500u, 30_500u, 30_000u, 31_000u, 15)],
            [], 1, 0, 0, 0));

        // Then AppendLap: INSERT OR IGNORE — history row wins, nothing duplicated.
        _db.AppendLap(sessionId, new LapCompleted(2, "Driver B", 1, 91_500u, 30_500, 30_000,
            ActualCompound.F1C3, 1, 4, 0f));

        var rows = _db.GetLapRows(sessionId);
        var lap = Assert.Single(rows);
        Assert.Equal((byte)2, lap.CarIndex);
        Assert.Equal(1, lap.LapNumber);
        Assert.Equal(91_500u, lap.LapTimeMs);
        Assert.Equal(31_000u, lap.Sector3Ms);
        Assert.Equal(15, lap.ValidFlags);
        Assert.Equal(1, lap.IsValid);

        // Reverse order on a second lap: AppendLap first (tyre known), then history merge.
        _db.AppendLap(sessionId, new LapCompleted(2, "Driver B", 2, 92_000u, 30_700, 30_100,
            ActualCompound.F1C3, 2, 3, 0f));
        _db.MergeLapHistory(sessionId, new LapHistoryUpdated(2, 2,
            [new LapHistoryRow(2, 92_000u, 30_700u, 30_100u, 31_200u, 15)],
            [], 2, 0, 0, 0));

        var rows2 = _db.GetLapRows(sessionId);
        Assert.Equal(2, rows2.Count);
        var lap2 = rows2.Single(l => l.LapNumber == 2);
        Assert.Equal(31_200u, lap2.Sector3Ms);
        Assert.Equal(15, lap2.ValidFlags);
        Assert.Equal("F1C3", lap2.Tyre); // AppendLap's tyre/position survive the merge
    }

    [Fact]
    public void Stints_lap_positions_and_setup_round_trip()
    {
        var meta = new SessionMeta(11, SessionType.Race, Track.Bahrain, 5, 5412, false, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);
        var sessionId = _db.OpenSession(meta);

        _db.MergeLapHistory(sessionId, new LapHistoryUpdated(0, 2,
            [new LapHistoryRow(1, 90_000u, 30_000u, 30_000u, 30_000u, 15)],
            [new StoredTyreStint(0, ActualCompound.F1C3, VisualCompound.F1Soft, 255),
             new StoredTyreStint(1, ActualCompound.F1C4, VisualCompound.F1Medium, 6)],
            1, 0, 0, 0));

        _db.PutLapPositions(sessionId, new LapPositionsChunk(10, 2, [
            [3, 0, 1],  // lap 10: car 0=P1? no: row[car]=position → car0=3, car2=1
            [1, 0, 2],
        ]));

        _db.UpsertSetup(sessionId, 0, new CarSetupSnapshot(
            20, 10, 50, 45, -3.2f, -1.8f, 0.05f, 0.1f, 30, 28, 3, 12, 5, 6, 90, 55, 2,
            0.5f, 100f, [22.5f, 22.5f, 21.0f, 21.0f], 4));

        var stints = _db.GetStints(sessionId);
        Assert.Equal(2, stints.Count);
        Assert.Equal(ActualCompound.F1C3, stints[0].Actual);
        Assert.Equal((byte)255, stints[0].EndLap); // ongoing stint
        Assert.Equal("live", stints[0].Source);
        Assert.Equal(ActualCompound.F1C4, stints[1].Actual);
        Assert.Equal((byte)6, stints[1].EndLap);

        var positions = _db.GetLapPositions(sessionId);
        var chunk = Assert.Single(positions);
        Assert.Equal(10, chunk.Key);
        Assert.Equal((byte)3, chunk.Value[0][0]); // lap 10, car 0 → P3
        Assert.Equal((byte)1, chunk.Value[0][2]); // car 2 → P1
        Assert.Equal((byte)2, chunk.Value[1][2]);

        var setup = _db.GetSetup(sessionId, 0);
        Assert.NotNull(setup);
        Assert.Equal((byte)20, setup.FrontWing);
        Assert.Equal(-3.2f, setup.FrontCamber, precision: 3);
        Assert.Equal(22.5f, setup.TyresPressure[0], precision: 3);
        Assert.Equal((byte)4, setup.NextFrontWingValue);
    }

    [Fact]
    public void Damage_motion_and_final_stints_round_trip()
    {
        var meta = new SessionMeta(12, SessionType.Race, Track.Bahrain, 6, 5412, true, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);
        var sessionId = _db.OpenSession(meta);

        var damage = new CarDamageStatus(5,
            FrontLeftWing: 40, FrontRightWing: 0, RearWing: 55, Floor: 10, Diffuser: 0,
            DrsFault: true, Sidepod: 25,
            FrontBrakeDamage: 30, RearBrakeDamage: 5, GearBoxDamage: 45, EngineDamage: 60,
            ErsFault: false, GearBoxFault: true, EngineFault: false,
            EngineBlown: true, EngineSeized: false,
            EngineIceWear: 0.37f, EngineMguhWear: 0.82f, EngineEsWear: 0.11f, EngineCeWear: 0);
        _db.AppendDamageLog(sessionId, new CarDamageChanged(5, damage, TyreWorst: 12, DamageWorst: 60, LapNumber: 3));

        var logged = Assert.Single(_db.GetDamageLog(sessionId));
        Assert.Equal((byte)5, logged.CarIndex);
        Assert.Equal(3, logged.LapNumber);
        Assert.Equal((byte)12, logged.TyreWorst);
        Assert.Equal((byte)60, logged.DamageWorst);
        Assert.Equal((byte)55, logged.Damage.RearWing);
        Assert.Equal((byte)25, logged.Damage.Sidepod);
        Assert.Equal((byte)45, logged.Damage.GearBoxDamage);
        Assert.True(logged.Damage.DrsFault);
        Assert.True(logged.Damage.GearBoxFault);
        Assert.True(logged.Damage.EngineBlown);
        Assert.Equal(0.37f, logged.Damage.EngineIceWear, precision: 2);
        Assert.Equal(0.82f, logged.Damage.EngineMguhWear, precision: 2);

        var motion = new LapMotionSummaryData(
            [1.05f, 1.1f, 0.9f, 0.8f], 12.5f, 48.0f, 3.4f, -1.9f);
        _db.AppendMotionSummary(sessionId, new LapMotionSummary(0, 2, motion));

        var summary = Assert.Single(_db.GetMotionSummaries(sessionId));
        Assert.Equal((byte)0, summary.CarIndex);
        Assert.Equal(2, summary.LapNumber);
        Assert.Equal(motion.MaxWheelSlipRatio, summary.Data.MaxWheelSlipRatio);
        Assert.Equal(12.5f, summary.Data.MinFrontAeroHeight, precision: 3);
        Assert.Equal(-1.9f, summary.Data.MaxLongitudinalG, precision: 3);

        // JSON roundtrip through the serializer pair (internal helpers).
        var json = TelemetryDb.SerializeMotionSummary(motion);
        var back = TelemetryDb.DeserializeMotionSummary(json);
        Assert.NotNull(back);
        Assert.Equal(48.0f, back.MinRearAeroHeight, precision: 3);
        Assert.Equal(0.8f, back.MaxWheelSlipRatio[3], precision: 3);
        Assert.Null(TelemetryDb.DeserializeMotionSummary("not json"));

        // Final classification: pit stops, result reason and per-stint compounds.
        var result = new FinalResultRow(
            1, 0, "Player", Team.McLaren, 7, 22, 5, 25f, ResultStatus.Active, 88_500u,
            2743.5, 0, 0,
            NumPitStops: 2, ResultReason: "Finished", NumTyreStints: 2,
            StintsActual: [ActualCompound.F1C4, ActualCompound.F1C3],
            StintsVisual: [VisualCompound.F1Medium, VisualCompound.F1Hard],
            StintsEndLaps: [10, 22]);
        _db.FinalizeSession(sessionId, "checkered", [result]);
        _db.FinalizeSessionExtras(sessionId, [result]);

        var stored = Assert.Single(_db.GetResults(sessionId));
        Assert.Equal((byte)2, stored.NumPitStops);
        Assert.Equal("Finished", stored.ResultReason);

        var finalStints = _db.GetStints(sessionId).Where(s => s.Source == "final").ToList();
        Assert.Equal(2, finalStints.Count);
        Assert.Equal(ActualCompound.F1C4, finalStints[0].Actual);
        Assert.Equal((byte)10, finalStints[0].EndLap);
        Assert.Equal(ActualCompound.F1C3, finalStints[1].Actual);
        Assert.Equal((byte)22, finalStints[1].EndLap);
    }
}