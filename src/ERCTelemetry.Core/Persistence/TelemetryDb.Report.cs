using System.Globalization;
using System.Text.Json;
using F1Game.UDP.Enums;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Persistence;

/// <summary>Race-report schema delta, write APIs and read APIs (partial of TelemetryDb,
/// kept out of TelemetryDb.cs for the file-size budget). All write APIs are idempotent
/// upserts — event order (MergeLapHistory before/after AppendLap) must not matter.</summary>
public sealed partial class TelemetryDb
{
    private const string StintSourceLive = "live";
    private const string StintSourceFinal = "final";

    private void EnsureReportSchema()
    {
        if (!ColumnExists("sessions", "time_of_day"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN time_of_day INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("sessions", "formula"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN formula TEXT NOT NULL DEFAULT ''");
        }

        if (!ColumnExists("sessions", "pit_speed_limit"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN pit_speed_limit INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("sessions", "sector2_lap_distance_start"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN sector2_lap_distance_start REAL NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("sessions", "sector3_lap_distance_start"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN sector3_lap_distance_start REAL NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("sessions", "num_drs_zones"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN num_drs_zones INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("sessions", "drs_zones"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN drs_zones TEXT");
        }

        if (!ColumnExists("sessions", "rule_set"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN rule_set TEXT NOT NULL DEFAULT ''");
        }

        if (!ColumnExists("sessions", "session_duration"))
        {
            Exec("ALTER TABLE sessions ADD COLUMN session_duration INTEGER NOT NULL DEFAULT 0");
        }

        // laps extras: S3, validity, fuel, pit/status facts, aero/behaviour flags.
        if (!ColumnExists("laps", "sector3_ms"))
        {
            Exec("ALTER TABLE laps ADD COLUMN sector3_ms INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("laps", "is_valid"))
        {
            Exec("ALTER TABLE laps ADD COLUMN is_valid INTEGER NOT NULL DEFAULT -1");
        }

        if (!ColumnExists("laps", "valid_flags"))
        {
            Exec("ALTER TABLE laps ADD COLUMN valid_flags INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("laps", "fuel_used"))
        {
            Exec("ALTER TABLE laps ADD COLUMN fuel_used REAL NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("laps", "pit_status"))
        {
            Exec("ALTER TABLE laps ADD COLUMN pit_status TEXT NOT NULL DEFAULT ''");
        }

        if (!ColumnExists("laps", "pit_stops"))
        {
            Exec("ALTER TABLE laps ADD COLUMN pit_stops INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("laps", "penalties_s"))
        {
            Exec("ALTER TABLE laps ADD COLUMN penalties_s INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("laps", "driver_status"))
        {
            Exec("ALTER TABLE laps ADD COLUMN driver_status TEXT NOT NULL DEFAULT ''");
        }

        if (!ColumnExists("laps", "result_status"))
        {
            Exec("ALTER TABLE laps ADD COLUMN result_status TEXT NOT NULL DEFAULT ''");
        }

        if (!ColumnExists("laps", "active_aero_mode"))
        {
            Exec("ALTER TABLE laps ADD COLUMN active_aero_mode INTEGER NOT NULL DEFAULT 255");
        }

        if (!ColumnExists("laps", "overtake_used"))
        {
            Exec("ALTER TABLE laps ADD COLUMN overtake_used INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("laps", "wrong_way_laps"))
        {
            Exec("ALTER TABLE laps ADD COLUMN wrong_way_laps INTEGER NOT NULL DEFAULT 0");
        }

        // results extras.
        if (!ColumnExists("results", "num_pit_stops"))
        {
            Exec("ALTER TABLE results ADD COLUMN num_pit_stops INTEGER NOT NULL DEFAULT 0");
        }

        if (!ColumnExists("results", "result_reason"))
        {
            Exec("ALTER TABLE results ADD COLUMN result_reason TEXT NOT NULL DEFAULT ''");
        }

        // events extras: collision detail.
        if (!ColumnExists("events", "second_car_index"))
        {
            Exec("ALTER TABLE events ADD COLUMN second_car_index INTEGER");
        }

        if (!ColumnExists("events", "detail_value"))
        {
            Exec("ALTER TABLE events ADD COLUMN detail_value INTEGER NOT NULL DEFAULT 0");
        }

        Exec("""
            CREATE TABLE IF NOT EXISTS tyre_stints (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                stint_index INTEGER NOT NULL,
                actual_compound TEXT NOT NULL,
                visual_compound TEXT NOT NULL,
                end_lap INTEGER NOT NULL,
                source TEXT NOT NULL,
                UNIQUE(session_id, car_index, stint_index, source)
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_tyre_stints_session ON tyre_stints(session_id);");

        Exec("""
            CREATE TABLE IF NOT EXISTS lap_positions (
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                starting_lap INTEGER NOT NULL,
                num_laps INTEGER NOT NULL,
                positions BLOB NOT NULL,
                PRIMARY KEY(session_id, starting_lap)
            );
            """);

        Exec("""
            CREATE TABLE IF NOT EXISTS car_damage_log (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                utc TEXT NOT NULL,
                lap_number INTEGER NOT NULL DEFAULT 0,
                flw INTEGER NOT NULL DEFAULT 0,
                frw INTEGER NOT NULL DEFAULT 0,
                rw INTEGER NOT NULL DEFAULT 0,
                floor INTEGER NOT NULL DEFAULT 0,
                diffuser INTEGER NOT NULL DEFAULT 0,
                sidepod INTEGER NOT NULL DEFAULT 0,
                brake_front INTEGER NOT NULL DEFAULT 0,
                brake_rear INTEGER NOT NULL DEFAULT 0,
                gearbox_damage INTEGER NOT NULL DEFAULT 0,
                engine_damage INTEGER NOT NULL DEFAULT 0,
                drs_fault INTEGER NOT NULL DEFAULT 0,
                ers_fault INTEGER NOT NULL DEFAULT 0,
                gearbox_fault INTEGER NOT NULL DEFAULT 0,
                engine_fault INTEGER NOT NULL DEFAULT 0,
                engine_blown INTEGER NOT NULL DEFAULT 0,
                engine_seized INTEGER NOT NULL DEFAULT 0,
                wear_ice REAL NOT NULL DEFAULT 0,
                wear_mguh REAL NOT NULL DEFAULT 0,
                wear_es REAL NOT NULL DEFAULT 0,
                wear_ce REAL NOT NULL DEFAULT 0,
                tyre_worst INTEGER NOT NULL DEFAULT 0,
                damage_worst INTEGER NOT NULL DEFAULT 0
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_car_damage_log_session ON car_damage_log(session_id);");

        Exec("""
            CREATE TABLE IF NOT EXISTS lap_motion_summary (
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                lap_number INTEGER NOT NULL,
                summary TEXT NOT NULL,
                PRIMARY KEY(session_id, car_index, lap_number)
            );
            """);

        Exec("""
            CREATE TABLE IF NOT EXISTS setups (
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                front_wing INTEGER NOT NULL DEFAULT 0,
                rear_wing INTEGER NOT NULL DEFAULT 0,
                on_throttle INTEGER NOT NULL DEFAULT 0,
                off_throttle INTEGER NOT NULL DEFAULT 0,
                front_camber REAL NOT NULL default 0,
                rear_camber REAL NOT NULL default 0,
                front_toe REAL NOT NULL default 0,
                rear_toe REAL NOT NULL default 0,
                front_suspension INTEGER NOT NULL DEFAULT 0,
                rear_suspension INTEGER NOT NULL DEFAULT 0,
                front_arb INTEGER NOT NULL DEFAULT 0,
                rear_arb INTEGER NOT NULL DEFAULT 0,
                front_susp_height INTEGER NOT NULL DEFAULT 0,
                rear_susp_height INTEGER NOT NULL DEFAULT 0,
                brake_pressure INTEGER NOT NULL DEFAULT 0,
                brake_bias INTEGER NOT NULL DEFAULT 0,
                engine_braking INTEGER NOT NULL DEFAULT 0,
                ballast REAL NOT NULL DEFAULT 0,
                fuel_load REAL NOT NULL DEFAULT 0,
                tyre_pressure_fl REAL NOT NULL DEFAULT 0,
                tyre_pressure_fr REAL NOT NULL DEFAULT 0,
                tyre_pressure_rl REAL NOT NULL DEFAULT 0,
                tyre_pressure_rr REAL NOT NULL DEFAULT 0,
                next_front_wing INTEGER NOT NULL DEFAULT 0,
                UNIQUE(session_id, car_index)
            );
            """);
    }

    // ---- write APIs -------------------------------------------------------------

    /// <summary>Upserts the full driver roster (team/number/AI/player flags). Called on
    /// DriversRegistered — the old AppendLap-only path wrote team='', race_number=0.</summary>
    public void UpsertDrivers(long sessionId, IReadOnlyList<DriverEntry> drivers)
    {
        foreach (var d in drivers)
        {
            Exec("""
                INSERT INTO drivers (session_id, car_index, name, team, race_number, is_ai, is_player)
                VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7)
                ON CONFLICT(session_id, car_index) DO UPDATE SET
                    name=@p3, team=@p4, race_number=@p5, is_ai=@p6, is_player=@p7
                """, cmd =>
            {
                Bind(cmd, 1, sessionId);
                Bind(cmd, 2, (long)d.CarIndex);
                Bind(cmd, 3, d.Name);
                Bind(cmd, 4, d.Team.ToString());
                Bind(cmd, 5, (long)d.RaceNumber);
                Bind(cmd, 6, d.IsAiControlled ? 1L : 0L);
                Bind(cmd, 7, d.IsPlayer ? 1L : 0L);
            });
        }
    }

    /// <summary>Writes the race-report session facts (idempotent UPDATE keyed by uid).</summary>
    public void UpdateSessionExtras(SessionMeta meta)
    {
        Exec("""
            UPDATE sessions SET time_of_day=@p1, formula=@p2, pit_speed_limit=@p3,
                sector2_lap_distance_start=@p4, sector3_lap_distance_start=@p5,
                num_drs_zones=@p6, drs_zones=@p7, rule_set=@p8, session_duration=@p9
            WHERE session_uid=@p10
            """, cmd =>
        {
            Bind(cmd, 1, (long)meta.TimeOfDay);
            Bind(cmd, 2, meta.Formula.ToString());
            Bind(cmd, 3, (long)meta.PitSpeedLimit);
            Bind(cmd, 4, (double)meta.Sector2LapDistanceStart);
            Bind(cmd, 5, (double)meta.Sector3LapDistanceStart);
            Bind(cmd, 6, (long)meta.NumDrsZones);
            Bind(cmd, 7, SerializeDrsZones(meta.DrsZones));
            Bind(cmd, 8, meta.RuleSet.ToString());
            Bind(cmd, 9, (long)meta.SessionDuration);
            Bind(cmd, 10, (long)meta.SessionUid);
        });
    }

    private static string SerializeDrsZones(IReadOnlyList<DrsZoneInfo>? zones) =>
        zones is { Count: > 0 }
            ? JsonSerializer.Serialize(zones.Select(z => new[] { z.ZoneStart, z.ZoneEnd }).ToArray())
            : null!;

    /// <summary>Upserts the player's setup (CarSetups packet, deduplicated by the store).
    /// ON CONFLICT DO UPDATE: the game re-reports the setup after flashback/setups changes.</summary>
    public void UpsertSetup(long sessionId, byte carIndex, CarSetupSnapshot s)
    {
        Exec("""
            INSERT INTO setups (session_id, car_index, front_wing, rear_wing, on_throttle,
                off_throttle, front_camber, rear_camber, front_toe, rear_toe,
                front_suspension, rear_suspension, front_arb, rear_arb,
                front_susp_height, rear_susp_height, brake_pressure, brake_bias,
                engine_braking, ballast, fuel_load, tyre_pressure_fl, tyre_pressure_fr,
                tyre_pressure_rl, tyre_pressure_rr, next_front_wing)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23,@p24,@p25,@p26)
            ON CONFLICT(session_id, car_index) DO UPDATE SET
                front_wing=@p3, rear_wing=@p4, on_throttle=@p5, off_throttle=@p6,
                front_camber=@p7, rear_camber=@p8, front_toe=@p9, rear_toe=@p10,
                front_suspension=@p11, rear_suspension=@p12, front_arb=@p13, rear_arb=@p14,
                front_susp_height=@p15, rear_susp_height=@p16, brake_pressure=@p17,
                brake_bias=@p18, engine_braking=@p19, ballast=@p20, fuel_load=@p21,
                tyre_pressure_fl=@p22, tyre_pressure_fr=@p23, tyre_pressure_rl=@p24,
                tyre_pressure_rr=@p25, next_front_wing=@p26
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)carIndex);
            Bind(cmd, 3, (long)s.FrontWing);
            Bind(cmd, 4, (long)s.RearWing);
            Bind(cmd, 5, (long)s.OnThrottle);
            Bind(cmd, 6, (long)s.OffThrottle);
            Bind(cmd, 7, (double)s.FrontCamber);
            Bind(cmd, 8, (double)s.RearCamber);
            Bind(cmd, 9, (double)s.FrontToe);
            Bind(cmd, 10, (double)s.RearToe);
            Bind(cmd, 11, (long)s.FrontSuspension);
            Bind(cmd, 12, (long)s.RearSuspension);
            Bind(cmd, 13, (long)s.FrontAntiRollBar);
            Bind(cmd, 14, (long)s.RearAntiRollBar);
            Bind(cmd, 15, (long)s.FrontSuspensionHeight);
            Bind(cmd, 16, (long)s.RearSuspensionHeight);
            Bind(cmd, 17, (long)s.BrakePressure);
            Bind(cmd, 18, (long)s.BrakeBias);
            Bind(cmd, 19, (long)s.EngineBraking);
            Bind(cmd, 20, (double)s.Ballast);
            Bind(cmd, 21, (double)s.FuelLoad);
            Bind(cmd, 22, (double)s.TyresPressure[0]);
            Bind(cmd, 23, (double)s.TyresPressure[1]);
            Bind(cmd, 24, (double)s.TyresPressure[2]);
            Bind(cmd, 25, (double)s.TyresPressure[3]);
            Bind(cmd, 26, (long)s.NextFrontWingValue);
        });
    }

    /// <summary>Upserts the SessionHistory lap rows (S3 + validity) and the stint list.
    /// May run BEFORE the matching AppendLap — the ON CONFLICT UPDATE merges both sides;
    /// whoever writes first wins the shared columns (identical values either way).</summary>
    public void MergeLapHistory(long sessionId, LapHistoryUpdated evt)
    {
        foreach (var row in evt.NewLaps)
        {
            Exec("""
                INSERT INTO laps (session_id, car_index, lap_number, lap_time_ms,
                    sector1_ms, sector2_ms, sector3_ms, valid_flags, is_valid,
                    tyre, tyre_age_laps, position)
                VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,'',0,0)
                ON CONFLICT(session_id, car_index, lap_number) DO UPDATE SET
                    lap_time_ms=@p4, sector1_ms=@p5, sector2_ms=@p6, sector3_ms=@p7,
                    valid_flags=@p8, is_valid=@p9
                """, cmd =>
            {
                Bind(cmd, 1, sessionId);
                Bind(cmd, 2, (long)evt.CarIndex);
                Bind(cmd, 3, (long)row.LapNumber);
                Bind(cmd, 4, (long)row.LapTimeMs);
                Bind(cmd, 5, (long)row.Sector1Ms);
                Bind(cmd, 6, (long)row.Sector2Ms);
                Bind(cmd, 7, (long)row.Sector3Ms);
                Bind(cmd, 8, (long)row.ValidFlags);
                Bind(cmd, 9, (row.ValidFlags & 1) != 0 ? 1L : 0L);
            });
        }

        UpsertLiveStints(sessionId, evt);
    }

    /// <summary>Stints are re-sent complete when the count changed — upsert every row.
    /// EndLap 255 (= ongoing stint) is stored as-is; the report renders it as ongoing.</summary>
    private void UpsertLiveStints(long sessionId, LapHistoryUpdated evt)
    {
        foreach (var stint in evt.Stints)
        {
            Exec("""
                INSERT INTO tyre_stints (session_id, car_index, stint_index,
                    actual_compound, visual_compound, end_lap, source)
                VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7)
                ON CONFLICT(session_id, car_index, stint_index, source) DO UPDATE SET
                    actual_compound=@p4, visual_compound=@p5, end_lap=@p6
                """, cmd =>
            {
                Bind(cmd, 1, sessionId);
                Bind(cmd, 2, (long)evt.CarIndex);
                Bind(cmd, 3, (long)stint.StintIndex);
                Bind(cmd, 4, stint.Actual.ToString());
                Bind(cmd, 5, stint.Visual.ToString());
                Bind(cmd, 6, (long)stint.EndLap);
                Bind(cmd, 7, StintSourceLive);
            });
        }
    }

    /// <summary>One chunk of lap-by-lap positions (row-major lap*24+car in the blob).</summary>
    public void PutLapPositions(long sessionId, LapPositionsChunk chunk)
    {
        var blob = new byte[chunk.NumLaps * TelemetryConstants.MaxCars];
        for (var lap = 0; lap < chunk.NumLaps; lap++)
        {
            var row = chunk.PositionsPerLap[lap];
            for (var car = 0; car < Math.Min(row.Length, TelemetryConstants.MaxCars); car++)
            {
                blob[(lap * TelemetryConstants.MaxCars) + car] = row[car];
            }
        }

        Exec("""
            INSERT INTO lap_positions (session_id, starting_lap, num_laps, positions)
            VALUES (@p1,@p2,@p3,@p4)
            ON CONFLICT(session_id, starting_lap) DO UPDATE SET num_laps=@p3, positions=@p4
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)chunk.StartingLap);
            Bind(cmd, 3, (long)chunk.NumLaps);
            Bind(cmd, 4, blob);
        });
    }

    /// <summary>Appends one damage-log row (store fires only on rounded-fingerprint
    /// change; utc is stamped at write time).</summary>
    public void AppendDamageLog(long sessionId, CarDamageChanged evt)
    {
        var d = evt.Damage;
        Exec("""
            INSERT INTO car_damage_log (session_id, car_index, utc, lap_number,
                flw, frw, rw, floor, diffuser, sidepod, brake_front, brake_rear,
                gearbox_damage, engine_damage, drs_fault, ers_fault, gearbox_fault,
                engine_fault, engine_blown, engine_seized,
                wear_ice, wear_mguh, wear_es, wear_ce, tyre_worst, damage_worst)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,
                @p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23,@p24,@p25,@p26)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)evt.CarIndex);
            Bind(cmd, 3, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            Bind(cmd, 4, (long)evt.LapNumber);
            Bind(cmd, 5, (long)d.FrontLeftWing);
            Bind(cmd, 6, (long)d.FrontRightWing);
            Bind(cmd, 7, (long)d.RearWing);
            Bind(cmd, 8, (long)d.Floor);
            Bind(cmd, 9, (long)d.Diffuser);
            Bind(cmd, 10, (long)d.Sidepod);
            Bind(cmd, 11, (long)d.FrontBrakeDamage);
            Bind(cmd, 12, (long)d.RearBrakeDamage);
            Bind(cmd, 13, (long)d.GearBoxDamage);
            Bind(cmd, 14, (long)d.EngineDamage);
            Bind(cmd, 15, d.DrsFault ? 1L : 0L);
            Bind(cmd, 16, d.ErsFault ? 1L : 0L);
            Bind(cmd, 17, d.GearBoxFault ? 1L : 0L);
            Bind(cmd, 18, d.EngineFault ? 1L : 0L);
            Bind(cmd, 19, d.EngineBlown ? 1L : 0L);
            Bind(cmd, 20, d.EngineSeized ? 1L : 0L);
            Bind(cmd, 21, (double)d.EngineIceWear);
            Bind(cmd, 22, (double)d.EngineMguhWear);
            Bind(cmd, 23, (double)d.EngineEsWear);
            Bind(cmd, 24, (double)d.EngineCeWear);
            Bind(cmd, 25, (long)evt.TyreWorst);
            Bind(cmd, 26, (long)evt.DamageWorst);
        });
    }

    /// <summary>Stores one per-lap motion aggregate (player only, JSON) — the raw ~60 Hz
    /// MotionEx stream is deliberately NOT persisted (≈1.5 MB/min).</summary>
    public void AppendMotionSummary(long sessionId, LapMotionSummary evt)
    {
        Exec("""
            INSERT INTO lap_motion_summary (session_id, car_index, lap_number, summary)
            VALUES (@p1,@p2,@p3,@p4)
            ON CONFLICT(session_id, car_index, lap_number) DO UPDATE SET summary=@p4
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)evt.CarIndex);
            Bind(cmd, 3, (long)evt.LapNumber);
            Bind(cmd, 4, SerializeMotionSummary(evt.Data));
        });
    }

    internal static string SerializeMotionSummary(LapMotionSummaryData d) => JsonSerializer.Serialize(new
    {
        sr = d.MaxWheelSlipRatio,
        fa = d.MinFrontAeroHeight,
        ra = d.MinRearAeroHeight,
        lg = d.MaxLateralG,
        lo = d.MaxLongitudinalG,
    });

    internal static LapMotionSummaryData? DeserializeMotionSummary(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ratios = root.GetProperty("sr");
            var slip = new float[ratios.GetArrayLength()];
            for (var i = 0; i < slip.Length; i++)
            {
                slip[i] = ratios[i].GetSingle();
            }

            return new LapMotionSummaryData(
                slip,
                root.GetProperty("fa").GetSingle(),
                root.GetProperty("ra").GetSingle(),
                root.GetProperty("lg").GetSingle(),
                root.GetProperty("lo").GetSingle());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Final classification extras: pit stops + result reason on the results
    /// rows, plus the game's per-car tyre stints (source 'final' — kept beside the live
    /// stints so a race without SessionHistory still has its stints).</summary>
    public void FinalizeSessionExtras(long sessionId, IReadOnlyList<FinalResultRow> results)
    {
        foreach (var r in results)
        {
            Exec("""
                UPDATE results SET num_pit_stops=@p1, result_reason=@p2
                WHERE session_id=@p3 AND car_index=@p4
                """, cmd =>
            {
                Bind(cmd, 1, (long)r.NumPitStops);
                Bind(cmd, 2, r.ResultReason);
                Bind(cmd, 3, sessionId);
                Bind(cmd, 4, (long)r.CarIndex);
            });
        }

        foreach (var r in results)
        {
            if (r.StintsEndLaps is not { Length: > 0 })
            {
                continue;
            }

            for (var i = 0; i < r.StintsEndLaps.Length; i++)
            {
                Exec("""
                    INSERT INTO tyre_stints (session_id, car_index, stint_index,
                        actual_compound, visual_compound, end_lap, source)
                    VALUES (@p1,@p2,@p3,@p4,@p5,@p6,'final')
                    ON CONFLICT(session_id, car_index, stint_index, source) DO UPDATE SET
                        actual_compound=@p4, visual_compound=@p5, end_lap=@p6
                    """, cmd =>
                {
                    Bind(cmd, 1, sessionId);
                    Bind(cmd, 2, (long)r.CarIndex);
                    Bind(cmd, 3, (long)i);
                    Bind(cmd, 4, r.StintsActual[i].ToString());
                    Bind(cmd, 5, r.StintsVisual[i].ToString());
                    Bind(cmd, 6, (long)r.StintsEndLaps[i]);
                });
            }
        }
    }

    // ---- read APIs ---------------------------------------------------------------

    /// <summary>One session row (full header facts for the race report).</summary>
    public sealed record SessionRow(
        long Id,
        ulong SessionUid,
        string SessionType,
        string Track,
        int TotalLaps,
        int TrackLength,
        string GameMode,
        string Weather,
        bool IsNetworkGame,
        byte PlayerCarIndex,
        int TrackTemp,
        int AirTemp,
        string StartedUtc,
        int Finalized,
        string? EndReason,
        int TimeOfDay,
        string Formula,
        int PitSpeedLimit,
        float Sector2Start,
        float Sector3Start,
        int NumDrsZones,
        string? DrsZonesJson,
        string RuleSet,
        int SessionDuration);

    /// <summary>Loads one session's header facts. Null when the id is unknown.</summary>
    public SessionRow? GetSession(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_uid, session_type, track, total_laps, track_length,
                   game_mode, weather, is_network_game, player_car_index, track_temp,
                   air_temp, started_utc, finalized, end_reason, time_of_day, formula,
                   pit_speed_limit, sector2_lap_distance_start, sector3_lap_distance_start,
                   num_drs_zones, drs_zones, rule_set, session_duration
            FROM sessions WHERE id = @p1
            """;
        Bind(cmd, 1, id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SessionRow(
            reader.GetInt64(0),
            (ulong)reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8) != 0,
            (byte)Math.Clamp(reader.GetInt64(9), 0, byte.MaxValue),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetInt32(15),
            reader.GetString(16),
            reader.GetInt32(17),
            (float)reader.GetDouble(18),
            (float)reader.GetDouble(19),
            reader.GetInt32(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.GetString(22),
            reader.GetInt32(23));
    }

    /// <summary>One stored lap with the full race-report facts (S3, validity, fuel,
    /// pit/status, aero/behaviour flags). Defaults (-1/0/255/'') = not seen that lap.</summary>
    public sealed record StoredLapRow(
        byte CarIndex,
        int LapNumber,
        uint LapTimeMs,
        uint Sector1Ms,
        uint Sector2Ms,
        uint Sector3Ms,
        int ValidFlags,        // raw SessionHistory bitfield (bit0 lap, bit1..3 S1..S3)
        int IsValid,           // -1 unknown, 1 valid, 0 invalid
        float FuelUsed,
        string PitStatus,
        int PitStops,
        int PenaltiesSeconds,
        string DriverStatus,
        string ResultStatus,
        byte ActiveAeroMode,
        bool OvertakeUsed,
        bool WrongWay,
        string Tyre,
        byte TyreAge,
        byte Position,
        float ErsUsedJ);

    /// <summary>All laps of a session (car, then lap order).</summary>
    public IReadOnlyList<StoredLapRow> GetLapRows(long sessionId)
    {
        var list = new List<StoredLapRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, lap_number, lap_time_ms, sector1_ms, sector2_ms, sector3_ms,
                   valid_flags, is_valid, fuel_used, pit_status, pit_stops, penalties_s,
                   driver_status, result_status, active_aero_mode, overtake_used,
                   wrong_way_laps, tyre, tyre_age_laps, position, ers_used_j
            FROM laps WHERE session_id = @p1 ORDER BY car_index, lap_number
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new StoredLapRow(
                (byte)reader.GetInt64(0),
                reader.GetInt32(1),
                ToUint(reader.GetInt64(2)),
                ToUint(reader.GetInt64(3)),
                ToUint(reader.GetInt64(4)),
                ToUint(reader.GetInt64(5)),
                reader.GetInt32(6),
                reader.GetInt32(7),
                (float)reader.GetDouble(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetString(12),
                reader.GetString(13),
                (byte)Math.Clamp(reader.GetInt64(14), 0, byte.MaxValue),
                reader.GetInt64(15) != 0,
                reader.GetInt64(16) != 0,
                reader.GetString(17),
                (byte)Math.Clamp(reader.GetInt64(18), 0, byte.MaxValue),
                (byte)Math.Clamp(reader.GetInt64(19), 0, byte.MaxValue),
                (float)reader.GetDouble(20)));
        }

        return list;
    }

    /// <summary>One stored tyre stint (live or final source; EndLap 255 = ongoing).</summary>
    public sealed record StoredStintRow(
        byte CarIndex,
        int StintIndex,
        ActualCompound Actual,
        VisualCompound Visual,
        byte EndLap,
        string Source);

    /// <summary>All tyre stints of a session, car then stint order.</summary>
    public IReadOnlyList<StoredStintRow> GetStints(long sessionId)
    {
        var list = new List<StoredStintRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, stint_index, actual_compound, visual_compound, end_lap, source
            FROM tyre_stints WHERE session_id = @p1 ORDER BY car_index, stint_index, source
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new StoredStintRow(
                (byte)reader.GetInt64(0),
                reader.GetInt32(1),
                ParseEnum<ActualCompound>(reader.GetString(2)),
                ParseEnum<VisualCompound>(reader.GetString(3)),
                (byte)Math.Clamp(reader.GetInt64(4), 0, byte.MaxValue),
                reader.GetString(5)));
        }

        return list;
    }

    /// <summary>All lap-position chunks of a session, starting-lap order. Values are
    /// 1..22 positions per car slot (0 = slot empty/unknown).</summary>
    public IReadOnlyDictionary<int, byte[][]> GetLapPositions(long sessionId)
    {
        var map = new Dictionary<int, byte[][]>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT starting_lap, num_laps, positions FROM lap_positions WHERE session_id = @p1 ORDER BY starting_lap";
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var startingLap = reader.GetInt32(0);
            var numLaps = reader.GetInt32(1);
            var blob = (byte[])reader.GetValue(2);
            var rows = new byte[numLaps][];
            for (var lap = 0; lap < numLaps && (lap + 1) * TelemetryConstants.MaxCars <= blob.Length; lap++)
            {
                var row = new byte[TelemetryConstants.MaxCars];
                Buffer.BlockCopy(blob, lap * TelemetryConstants.MaxCars, row, 0, TelemetryConstants.MaxCars);
                rows[lap] = row;
            }

            map[startingLap] = rows;
        }

        return map;
    }

    /// <summary>One damage-log row (one entry per rounded-fingerprint change).</summary>
    public sealed record DamageLogRow(
        byte CarIndex,
        DateTimeOffset Utc,
        int LapNumber,
        CarDamageStatus Damage,
        byte TyreWorst,
        byte DamageWorst);

    /// <summary>All damage-log rows of a session, chronological.</summary>
    public IReadOnlyList<DamageLogRow> GetDamageLog(long sessionId)
    {
        var list = new List<DamageLogRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, utc, lap_number, flw, frw, rw, floor, diffuser, sidepod,
                   brake_front, brake_rear, gearbox_damage, engine_damage, drs_fault,
                   ers_fault, gearbox_fault, engine_fault, engine_blown, engine_seized,
                   wear_ice, wear_mguh, wear_es, wear_ce, tyre_worst, damage_worst
            FROM car_damage_log WHERE session_id = @p1 ORDER BY id
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var damage = new CarDamageStatus(
                (byte)reader.GetInt64(0),
                (byte)Math.Clamp(reader.GetInt64(3), 0, 255),
                (byte)Math.Clamp(reader.GetInt64(4), 0, 255),
                (byte)Math.Clamp(reader.GetInt64(5), 0, 255),
                (byte)Math.Clamp(reader.GetInt64(6), 0, 255),
                (byte)reader.GetInt64(7),
                reader.GetInt64(13) != 0,
                (byte)reader.GetInt64(8),
                (byte)Math.Clamp(reader.GetInt64(9), 0, 255),
                (byte)Math.Clamp(reader.GetInt64(10), 0, 255),
                (byte)Math.Clamp(reader.GetInt64(11), 0, 255),
                (byte)Math.Clamp(reader.GetInt64(12), 0, 255),
                reader.GetInt64(14) != 0,
                reader.GetInt64(15) != 0,
                reader.GetInt64(16) != 0,
                reader.GetInt64(17) != 0,
                reader.GetInt64(18) != 0,
                (float)reader.GetDouble(19),
                (float)reader.GetDouble(20),
                (float)reader.GetDouble(21),
                (float)reader.GetDouble(22));

            list.Add(new DamageLogRow(
                (byte)reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetInt32(2),
                damage,
                (byte)reader.GetInt64(23),
                (byte)reader.GetInt64(24)));
        }

        return list;
    }

    /// <summary>All per-lap motion aggregates of a session, car then lap order.</summary>
    public sealed record MotionSummaryRow(byte CarIndex, int LapNumber, LapMotionSummaryData Data);

    public IReadOnlyList<MotionSummaryRow> GetMotionSummaries(long sessionId)
    {
        var list = new List<MotionSummaryRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, lap_number, summary FROM lap_motion_summary
            WHERE session_id = @p1 ORDER BY car_index, lap_number
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (DeserializeMotionSummary(reader.GetString(2)) is { } data)
            {
                list.Add(new MotionSummaryRow(
                    (byte)reader.GetInt64(0), reader.GetInt32(1), data));
            }
        }

        return list;
    }

    /// <summary>The player's stored setup for a session (null when none was captured).</summary>
    public CarSetupSnapshot? GetSetup(long sessionId, byte carIndex)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT front_wing, rear_wing, on_throttle, off_throttle, front_camber,
                   rear_camber, front_toe, rear_toe, front_suspension, rear_suspension,
                   front_arb, rear_arb, front_susp_height, rear_susp_height,
                   brake_pressure, brake_bias, engine_braking, ballast, fuel_load,
                   tyre_pressure_fl, tyre_pressure_fr, tyre_pressure_rl, tyre_pressure_rr,
                   next_front_wing
            FROM setups WHERE session_id = @p1 AND car_index = @p2
            """;
        Bind(cmd, 1, sessionId);
        Bind(cmd, 2, (long)carIndex);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CarSetupSnapshot(
            (byte)reader.GetInt64(0),
            (byte)reader.GetInt64(1),
            (byte)reader.GetInt64(2),
            (byte)reader.GetInt64(3),
            (float)reader.GetDouble(4),
            (float)reader.GetDouble(5),
            (float)reader.GetDouble(6),
            (float)reader.GetDouble(7),
            (byte)reader.GetInt64(8),
            (byte)reader.GetInt64(9),
            (byte)reader.GetInt64(10),
            (byte)reader.GetInt64(11),
            (byte)reader.GetInt64(12),
            (byte)reader.GetInt64(13),
            (byte)reader.GetInt64(14),
            (byte)reader.GetInt64(15),
            (byte)reader.GetInt64(16),
            (float)reader.GetDouble(17),
            (float)reader.GetDouble(18),
            [
                (float)reader.GetDouble(19),
                (float)reader.GetDouble(20),
                (float)reader.GetDouble(21),
                (float)reader.GetDouble(22),
            ],
            (byte)reader.GetInt64(23));
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : default;

    /// <summary>True when the table exists (sqlite_master probe; migration + tests).</summary>
    public bool HasTable(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@p1";
        Bind(cmd, 1, table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>True when the table has the given column (public ColumnExists).</summary>
    public bool HasColumn(string table, string column) => ColumnExists(table, column);
}
