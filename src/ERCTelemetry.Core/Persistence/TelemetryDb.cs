using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using F1Game.UDP.Enums;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Persistence;

/// <summary>SQLite persistence for completed sessions: schema, session lifecycle,
/// lap history and read APIs for the history browser. Single-writer: the persistence
/// pump is the only runtime writer.</summary>
public sealed partial class TelemetryDb : IDisposable
{
    private const int FinalizedState = 1;
    private const int AbandonedState = 2;

    private readonly SqliteConnection _connection;

    public TelemetryDb(string? path = null)
    {
        var dbPath = path ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _connection.Open();
        Exec("PRAGMA journal_mode=WAL;"); // crash-safe + concurrent readers
        Exec("PRAGMA synchronous=NORMAL;");
        EnsureSchema();
    }

    /// <summary>%LOCALAPPDATA%\ERCTelemetry\telemetry.db</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERCTelemetry", "telemetry.db");

    public void Dispose() => _connection.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY,
                session_uid INTEGER NOT NULL UNIQUE,
                session_type TEXT NOT NULL,
                track TEXT NOT NULL,
                total_laps INTEGER NOT NULL,
                track_length INTEGER NOT NULL,
                game_mode TEXT NOT NULL,
                weather TEXT NOT NULL,
                is_network_game INTEGER NOT NULL,
                player_car_index INTEGER NOT NULL,
                track_temp INTEGER NOT NULL,
                air_temp INTEGER NOT NULL,
                started_utc TEXT NOT NULL,   -- ISO-8601
                finalized INTEGER NOT NULL,  -- 0=open, 1=finalized, 2=abandoned
                end_reason TEXT
            );

            CREATE TABLE IF NOT EXISTS drivers (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                name TEXT NOT NULL,
                team TEXT NOT NULL,
                race_number INTEGER NOT NULL,
                is_ai INTEGER NOT NULL,
                is_player INTEGER NOT NULL,
                UNIQUE(session_id, car_index)
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS results (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                position INTEGER NOT NULL,
                name TEXT NOT NULL,
                team TEXT NOT NULL,
                race_number INTEGER NOT NULL,
                num_laps INTEGER NOT NULL,
                grid_position INTEGER NOT NULL,
                points REAL NOT NULL,
                result_status TEXT NOT NULL,
                best_lap_ms INTEGER NOT NULL,
                total_race_seconds REAL NOT NULL,
                penalties_time INTEGER NOT NULL,
                num_penalties INTEGER NOT NULL,
                UNIQUE(session_id, car_index)
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS laps (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                lap_number INTEGER NOT NULL,
                lap_time_ms INTEGER NOT NULL,
                sector1_ms INTEGER NOT NULL,
                sector2_ms INTEGER NOT NULL,
                tyre TEXT NOT NULL,
                tyre_age_laps INTEGER NOT NULL,
                position INTEGER NOT NULL,
                ers_used_j REAL NOT NULL DEFAULT 0,
                UNIQUE(session_id, car_index, lap_number)
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                utc TEXT NOT NULL,
                type TEXT NOT NULL,
                text TEXT NOT NULL,
                car_index INTEGER,
                lap_number INTEGER NOT NULL DEFAULT 0
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_laps_session ON laps(session_id);");
        Exec("CREATE INDEX IF NOT EXISTS idx_events_session ON events(session_id);");
        Exec("""
            CREATE TABLE IF NOT EXISTS overtakes (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                lap_number INTEGER NOT NULL,
                car_index INTEGER NOT NULL,
                driver_name TEXT NOT NULL,
                passed_car_index INTEGER NOT NULL,
                passed_driver_name TEXT NOT NULL,
                new_position INTEGER NOT NULL,
                utc TEXT NOT NULL
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_overtakes_session ON overtakes(session_id);");
        Exec("""
            CREATE TABLE IF NOT EXISTS lap_traces (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                car_index INTEGER NOT NULL,
                lap_number INTEGER NOT NULL,
                lap_time_ms INTEGER NOT NULL,
                samples TEXT NOT NULL,      -- JSON flat float array [d, s, d, s, ...]
                UNIQUE(session_id, car_index, lap_number)
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_lap_traces_session ON lap_traces(session_id);");
        Exec("""
            CREATE TABLE IF NOT EXISTS track_bests (
                track TEXT PRIMARY KEY,
                best_lap_ms INTEGER NOT NULL
            );
            """);

        // Migration: DBs created before lap-number stamping lack the events column —
        // existing rows keep 0 (= unknown lap), fresh rows get the real value.
        if (!ColumnExists("events", "lap_number"))
        {
            Exec("ALTER TABLE events ADD COLUMN lap_number INTEGER NOT NULL DEFAULT 0");
        }

        // Migration: DBs created before ERS-per-lap tracking lack the column —
        // existing rows keep 0 (= unknown), fresh rows get the real value.
        if (!ColumnExists("laps", "ers_used_j"))
        {
            Exec("ALTER TABLE laps ADD COLUMN ers_used_j REAL NOT NULL DEFAULT 0");
        }

        // Migration: penalties/warnings get a distance-around-lap stamp so the History
        // can show "Kurve/Sektor" per incident — existing rows keep -1 (= unknown).
        if (!ColumnExists("events", "lap_distance"))
        {
            Exec("ALTER TABLE events ADD COLUMN lap_distance REAL NOT NULL DEFAULT -1");
        }

        // Race report: extra tables + additive columns (kept in the partial to keep
        // this file under its size budget).
        EnsureReportSchema();

        // Collision clips: one row per recorded MP4 (kept in the partial too).
        EnsureClipsSchema();
    }

    /// <summary>True when the table has the given column (PRAGMA table_info probe;
    /// used for additive schema migrations).</summary>
    private bool ColumnExists(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- session lifecycle ----------------------------------------------------

    /// <summary>Opens a session row for <paramref name="meta"/> and returns its id.
    /// Any sessions still marked open are abandoned first (crash of a previous run).
    /// A reconnect can re-send the SAME session_uid (seen live: disconnect → rejoin);
    /// the existing row is then reused (INSERT OR IGNORE + lookup) instead of failing
    /// on the UNIQUE constraint — an open row just keeps receiving laps.</summary>
    public long OpenSession(SessionMeta meta)
    {
        AbandonOpenSessions(meta.SessionUid);
        var startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Exec("""
            INSERT OR IGNORE INTO sessions (session_uid, session_type, track, total_laps,
                track_length, game_mode, weather, is_network_game, player_car_index,
                track_temp, air_temp, started_utc, finalized, end_reason)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,0,NULL)
            """, cmd =>
        {
            Bind(cmd, 1, (long)meta.SessionUid);
            Bind(cmd, 2, meta.SessionType.ToString());
            Bind(cmd, 3, meta.Track.ToString());
            Bind(cmd, 4, (long)meta.TotalLaps);
            Bind(cmd, 5, (long)meta.TrackLength);
            Bind(cmd, 6, meta.GameMode.ToString());
            Bind(cmd, 7, meta.Weather.ToString());
            Bind(cmd, 8, meta.IsNetworkGame ? 1L : 0L);
            Bind(cmd, 9, (long)meta.PlayerCarIndex);
            Bind(cmd, 10, (long)meta.TrackTemperature);
            Bind(cmd, 11, (long)meta.AirTemperature);
            Bind(cmd, 12, startedUtc);
        });

        // The INSERT may have been ignored (rejoin with the same session_uid) — resolve
        // the row id by the uid instead of last_insert_rowid().
        var id = QueryLong("SELECT id FROM sessions WHERE session_uid = @p1",
            cmd => Bind(cmd, 1, (long)meta.SessionUid));
        // A rejoin can also re-send the uid after the row was finalized by the idle
        // timeout (pause / lobby) — re-open it so the resumed session keeps receiving
        // laps and can be finalized again at the real end.
        Exec("UPDATE sessions SET finalized=0, end_reason=NULL WHERE id=@p1 AND finalized<>0",
            cmd => Bind(cmd, 1, id));
        return id;
    }

    /// <summary>Single-cell long lookup helper (ExecuteScalar, 0 when nothing matches).</summary>
    private long QueryLong(string sql, Action<SqliteCommand> bind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Records one completed lap; idempotent per (session, car, lap).</summary>
    public void AppendLap(long sessionId, LapCompleted lap)
    {
        UpsertDriver(sessionId, lap.CarIndex, lap.DriverName);
        Exec("""
            INSERT OR IGNORE INTO laps (session_id, car_index, lap_number, lap_time_ms,
                sector1_ms, sector2_ms, tyre, tyre_age_laps, position, ers_used_j)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)lap.CarIndex);
            Bind(cmd, 3, (long)lap.LapNumber);
            Bind(cmd, 4, (long)lap.LapTimeMs);
            Bind(cmd, 5, (long)lap.Sector1TimeMs);
            Bind(cmd, 6, (long)lap.Sector2TimeMs);
            Bind(cmd, 7, lap.TyreCompound.ToString());
            Bind(cmd, 8, (long)lap.TyreAgeLaps);
            Bind(cmd, 9, (long)lap.Position);
            Bind(cmd, 10, (double)lap.ErsUsedJoules);
        });
    }

    /// <summary>One traced lap as listed for the History lap-comparison pickers.</summary>
    public sealed record LapTraceSummary(byte CarIndex, byte LapNumber, uint LapTimeMs);

    /// <summary>The player's fastest traced lap on one track across all sessions — the
    /// cross-session reference lap for the History comparison.</summary>
    public sealed record BestLapTraceRef(
        LapTrace Trace, uint LapTimeMs, string SessionType, string StartedUtc);

    /// <summary>Stores the player's speed trace of one completed lap (samples as a JSON
    /// flat array [d, s, d, s, ...]). Idempotent per (session, car, lap) — replays never
    /// overwrite.</summary>
    public void AppendLapTrace(long sessionId, byte carIndex, LapTrace trace)
    {
        // Nested [[d, s, throttle, brake], ...] — the flat [d, s, ...] legacy format is
        // still decoded, but nested rows are self-describing (pedals added in v2).
        var samples = new float[trace.Samples.Count][];
        for (var i = 0; i < trace.Samples.Count; i++)
        {
            samples[i] =
            [
                trace.Samples[i].LapDistance,
                trace.Samples[i].Speed,
                trace.Samples[i].Throttle,
                trace.Samples[i].Brake,
            ];
        }

        Exec("""
            INSERT OR IGNORE INTO lap_traces (session_id, car_index, lap_number, lap_time_ms, samples)
            VALUES (@p1,@p2,@p3,@p4,@p5)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)carIndex);
            Bind(cmd, 3, (long)trace.LapNumber);
            Bind(cmd, 4, (long)trace.LapTimeMs);
            Bind(cmd, 5, JsonSerializer.Serialize(samples));
        });
    }

    /// <summary>Loads one lap's trace back. Null when that lap was never traced or the
    /// stored JSON is corrupt (a bad row must not break the History tab).</summary>
    public LapTrace? GetLapTrace(long sessionId, byte carIndex, byte lapNumber)
    {
        List<LapTraceSample>? samples;
        uint lapTimeMs;
        ushort trackLength;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT lt.samples, lt.lap_time_ms, s.track_length
                FROM lap_traces lt JOIN sessions s ON lt.session_id = s.id
                WHERE lt.session_id = @p1 AND lt.car_index = @p2 AND lt.lap_number = @p3
                """;
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)carIndex);
            Bind(cmd, 3, (long)lapNumber);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            lapTimeMs = ToUint(reader.GetInt64(1));
            trackLength = ToUshort(reader.GetInt64(2));
            samples = DecodeTracePayload(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return samples is null || samples.Count == 0
            ? null
            : new LapTrace(lapNumber, lapTimeMs, trackLength, samples);
    }

    /// <summary>All traced laps of a session, ordered by lap number (only the player's
    /// laps get traced, so no car filter is needed for the pickers).</summary>
    public IReadOnlyList<LapTraceSummary> GetLapTraceSummaries(long sessionId)
    {
        var result = new List<LapTraceSummary>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, lap_number, lap_time_ms FROM lap_traces
            WHERE session_id = @p1 ORDER BY lap_number
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LapTraceSummary(
                (byte)Math.Clamp(reader.GetInt64(0), 0, byte.MaxValue),
                (byte)Math.Clamp(reader.GetInt64(1), 0, byte.MaxValue),
                ToUint(reader.GetInt64(2))));
        }

        return result;
    }

    /// <summary>The fastest traced lap ever recorded on <paramref name="track"/> across
    /// all sessions (lap_time_ms > 0). Null when no usable trace exists for the track.</summary>
    public BestLapTraceRef? GetBestLapTrace(string track)
    {
        string? json;
        uint lapTimeMs;
        byte lapNumber;
        ushort trackLength;
        string sessionType;
        string startedUtc;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT lt.samples, lt.lap_time_ms, lt.lap_number, s.track_length,
                       s.session_type, s.started_utc
                FROM lap_traces lt JOIN sessions s ON lt.session_id = s.id
                WHERE s.track = @p1 AND lt.lap_time_ms > 0
                ORDER BY lt.lap_time_ms ASC LIMIT 1
                """;
            Bind(cmd, 1, track);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            lapTimeMs = ToUint(reader.GetInt64(1));
            lapNumber = (byte)Math.Clamp(reader.GetInt64(2), 0, byte.MaxValue);
            trackLength = ToUshort(reader.GetInt64(3));
            sessionType = reader.GetString(4);
            startedUtc = reader.GetString(5);
            json = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        var samples = DecodeTracePayload(json);
        if (samples is null || samples.Count == 0)
        {
            return null;
        }

        return new BestLapTraceRef(
            new LapTrace(lapNumber, lapTimeMs, trackLength, samples),
            lapTimeMs, sessionType, startedUtc);
    }

    /// <summary>Routes a stored trace payload to its decoder: nested arrays = pedal
    /// format, anything else = legacy flat. Null when unusable.</summary>
    private static List<LapTraceSample>? DecodeTracePayload(string? json)
    {
        if (json is null)
        {
            return null;
        }

        return json.TrimStart().StartsWith("[[", StringComparison.Ordinal)
            ? DecodeNestedTraceJson(json)
            : DecodeTraceJson(json);
    }

    /// <summary>Tolerant trace decode: skips junk/odd-length/non-finite/negative values
    /// (system-boundary validation for hand-edited DB content). Null when unusable.</summary>
    private static List<LapTraceSample>? DecodeTraceJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        float[] flat;
        try
        {
            flat = JsonSerializer.Deserialize<float[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }

        var samples = new List<LapTraceSample>(flat.Length / 2);
        for (var i = 0; i + 1 < flat.Length; i += 2)
        {
            if (float.IsFinite(flat[i]) && float.IsFinite(flat[i + 1]) &&
                flat[i] >= 0f && flat[i + 1] >= 0f && flat[i + 1] <= ushort.MaxValue)
            {
                samples.Add(new LapTraceSample(flat[i], (ushort)flat[i + 1]));
            }
        }

        return samples;
    }

    /// <summary>Decodes the nested pedal-trace format <c>[[d, s, t, b], ...]</c>. Entries
    /// with 2 values are legacy (no pedals); 3/4 values take throttle (+brake). Invalid
    /// entries are skipped. Null when nothing usable survives.</summary>
    private static List<LapTraceSample>? DecodeNestedTraceJson(string json)
    {
        float[][] nested;
        try
        {
            nested = JsonSerializer.Deserialize<float[][]>(json) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }

        var samples = new List<LapTraceSample>(nested.Length);
        foreach (var entry in nested)
        {
            if (entry.Length < 2)
            {
                continue;
            }

            var (distance, speed) = (entry[0], entry[1]);
            if (!float.IsFinite(distance) || !float.IsFinite(speed) ||
                distance < 0f || speed < 0f || speed > ushort.MaxValue)
            {
                continue;
            }

            var throttle = entry.Length > 2 && float.IsFinite(entry[2]) ? Math.Clamp(entry[2], 0f, 1f) : 0f;
            var brake = entry.Length > 3 && float.IsFinite(entry[3]) ? Math.Clamp(entry[3], 0f, 1f) : 0f;
            samples.Add(new LapTraceSample(distance, (ushort)speed, throttle, brake));
        }

        return samples;
    }

    private static uint ToUint(long value) => (uint)Math.Clamp(value, 0, uint.MaxValue);

    private static ushort ToUshort(long value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    /// <summary>All-time best lap for a track (0 when no lap was ever recorded there).
    /// Feeds the PB threshold the session store compares the player's laps against.</summary>
    public uint GetTrackBestLapMs(string track)
    {
        return (uint)Math.Max(0, QueryLong("SELECT best_lap_ms FROM track_bests WHERE track = @p1",
            cmd => Bind(cmd, 1, track)));
    }

    /// <summary>Stores a track's all-time best lap — monotonic: a slower lap never
    /// overwrites a faster one.</summary>
    public void UpsertTrackBestLap(string track, uint bestLapMs)
    {
        Exec("""
            INSERT INTO track_bests (track, best_lap_ms) VALUES (@p1, @p2)
            ON CONFLICT(track) DO UPDATE SET best_lap_ms = @p2
                WHERE excluded.best_lap_ms < best_lap_ms
            """, cmd =>
        {
            Bind(cmd, 1, track);
            Bind(cmd, 2, (long)bestLapMs);
        });
    }

    /// <summary>Records a race-control style event for the session timeline.</summary>
    public void AppendEvent(long sessionId, RaceEventEntry evt)
    {
        Exec("""
            INSERT INTO events (session_id, seq, utc, type, text, car_index, lap_number,
                second_car_index, detail_value, lap_distance)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, evt.Sequence);
            Bind(cmd, 3, evt.Utc.ToString("o", CultureInfo.InvariantCulture));
            Bind(cmd, 4, evt.Type);
            Bind(cmd, 5, evt.Text);
            Bind(cmd, 6, evt.CarIndex is { } car ? (object)(long)car : DBNull.Value);
            Bind(cmd, 7, (long)evt.LapNumber);
            Bind(cmd, 8, evt.SecondCarIndex is { } second ? (object)(long)second : DBNull.Value);
            Bind(cmd, 9, (long)evt.DetailValue);
            Bind(cmd, 10, (double)evt.LapDistance);
        });
    }

    /// <summary>Records one detected overtake (adjacent position swap) for the
    /// session's highlights timeline.</summary>
    public void AppendOvertake(long sessionId, Overtake overtake)
    {
        Exec("""
            INSERT INTO overtakes
                (session_id, lap_number, car_index, driver_name, passed_car_index, passed_driver_name, new_position, utc)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)overtake.LapNumber);
            Bind(cmd, 3, (long)overtake.CarIndex);
            Bind(cmd, 4, overtake.DriverName);
            Bind(cmd, 5, (long)overtake.PassedCarIndex);
            Bind(cmd, 6, overtake.PassedDriverName);
            Bind(cmd, 7, (long)overtake.NewPosition);
            Bind(cmd, 8, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        });
    }
    public void FinalizeSession(long sessionId, string reason,
        IReadOnlyList<FinalResultRow> results)
    {
        using var tx = _connection.BeginTransaction();
        foreach (var r in results)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO results (session_id, car_index, position, name, team,
                    race_number, num_laps, grid_position, points, result_status, best_lap_ms,
                    total_race_seconds, penalties_time, num_penalties)
                VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14)
                """;
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)r.CarIndex);
            Bind(cmd, 3, (long)r.Position);
            Bind(cmd, 4, r.Name);
            Bind(cmd, 5, r.Team.ToString());
            Bind(cmd, 6, (long)r.RaceNumber);
            Bind(cmd, 7, (long)r.NumLaps);
            Bind(cmd, 8, (long)r.GridPosition);
            Bind(cmd, 9, (double)r.Points);
            Bind(cmd, 10, r.ResultStatus.ToString());
            Bind(cmd, 11, (long)r.BestLapTimeMs);
            Bind(cmd, 12, r.TotalRaceTimeSeconds);
            Bind(cmd, 13, (long)r.PenaltiesTime);
            Bind(cmd, 14, (long)r.NumPenalties);
            cmd.ExecuteNonQuery();
        }

        using var update = _connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE sessions SET finalized=@p1, end_reason=@p2 WHERE id=@p3";
        Bind(update, 1, (long)FinalizedState);
        Bind(update, 2, reason);
        Bind(update, 3, sessionId);
        update.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>Crash recovery: marks every still-open session abandoned. Call at startup;
    /// returns the number of affected rows.</summary>
    /// <summary>Crash recovery: marks every still-open session abandoned (finalized=2).
    /// Call at startup; returns affected rows. Sessions with <c>exceptSessionUid</c> are
    /// spared — a rejoin re-sends the same uid and must keep its open row open.</summary>
    public int AbandonOpenSessions(ulong exceptSessionUid = 0)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET finalized=@p1 WHERE finalized=0 " +
                          (exceptSessionUid != 0 ? "AND session_uid <> @p2" : string.Empty);
        Bind(cmd, 1, (long)AbandonedState);
        if (exceptSessionUid != 0)
        {
            Bind(cmd, 2, (long)exceptSessionUid);
        }

        return cmd.ExecuteNonQuery();
    }

    // ---- helpers ---------------------------------------------------------------

    private static void Bind(SqliteCommand cmd, int index, long value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = $"p{index}";
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static void Bind(SqliteCommand cmd, int index, double value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = $"p{index}";
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static void Bind(SqliteCommand cmd, int index, string? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = $"p{index}";
        // Microsoft.Data.Sqlite cannot infer a type from null — "Value must be set".
        // Nullable columns (e.g. drs_zones when the Session packet has no zones) must
        // bind DBNull.Value explicitly.
        p.Value = value is null ? DBNull.Value : value;
        cmd.Parameters.Add(p);
    }

    private static void Bind(SqliteCommand cmd, int index, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = $"p{index}";
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>Runs a scalar INSERT and returns last_insert_rowid().</summary>
    /// <summary>Ensures a drivers row exists (laps arrive before participants sometimes).</summary>
    private void UpsertDriver(long sessionId, byte carIndex, string name)
    {
        Exec("""
            INSERT OR IGNORE INTO drivers (session_id, car_index, name, team, race_number,
                is_ai, is_player)
            VALUES (@p1,@p2,@p3,'',0,0,0)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, (long)carIndex);
            Bind(cmd, 3, name);
        });
    }

    // ---- read APIs (history browser) -------------------------------------------

    /// <summary>One row of the history list (newest first).</summary>
    public sealed record SessionSummary(
        long Id,
        ulong SessionUid,
        string SessionType,
        string Track,
        bool IsNetworkGame,
        string StartedUtc,
        int Finalized, // 0,1,2 as stored
        int LapCount,
        int DriverCount);

    public IReadOnlyList<SessionSummary> GetSessions()
    {
        var list = new List<SessionSummary>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.session_uid, s.session_type, s.track, s.is_network_game,
                   s.started_utc, s.finalized,
                   (SELECT COUNT(*) FROM laps l WHERE l.session_id = s.id),
                   (SELECT COUNT(*) FROM drivers d WHERE d.session_id = s.id)
            FROM sessions s
            ORDER BY s.started_utc DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SessionSummary(
                reader.GetInt64(0),
                (ulong)reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return list;
    }

    // ---- session cards ("Deine Sessions" grid) ---------------------------------

    /// <summary>One card of the "Deine Sessions" grid: session facts plus the player's
    /// own best lap / team / finishing position (best lap falls back to the player's
    /// fastest stored lap while a session is not yet finalized — results only exist
    /// after FinalClassification).</summary>
    public sealed record SessionCard(
        long Id,
        ulong SessionUid,
        string SessionType,
        string Track,
        string StartedUtc,
        int Finalized, // 0,1,2 as stored
        int LapCount,
        int DriverCount,
        uint PlayerBestLapMs,
        string PlayerTeam,
        int PlayerPosition);

    /// <summary>Filter for <see cref="GetSessionCards"/>; null = no constraint on that
    /// axis. Page is 0-based, PageSize is clamped to >= 1.</summary>
    public sealed record SessionCardFilter(
        string? Track = null,
        string? SessionType = null,
        DateTime? StartedFromUtc = null,
        int Page = 0,
        int PageSize = 12);

    /// <summary>One page of session cards plus the unfiltered-by-page total (for the
    /// pagination controls).</summary>
    public sealed record SessionCardPage(IReadOnlyList<SessionCard> Items, int TotalCount);

    /// <summary>One page of session cards for the history grid, newest first. Filtering
    /// happens in SQL so page and count queries always agree.</summary>
    public SessionCardPage GetSessionCards(SessionCardFilter filter)
    {
        var pageSize = Math.Max(1, filter.PageSize);
        var page = Math.Max(0, filter.Page);

        var where = new List<string>();
        var bindIndex = 1;
        if (filter.Track is not null)
        {
            where.Add($"s.track = @p{bindIndex}");
            bindIndex++;
        }

        if (filter.SessionType is not null)
        {
            where.Add($"s.session_type = @p{bindIndex}");
            bindIndex++;
        }

        var fromUtc = filter.StartedFromUtc?.ToString("o", CultureInfo.InvariantCulture);
        if (fromUtc is not null)
        {
            where.Add($"s.started_utc >= @p{bindIndex}");
            bindIndex++;
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        var limitParam = $"@p{bindIndex}";
        var offsetParam = $"@p{bindIndex + 1}";

        void BindAll(SqliteCommand cmd, bool withPaging)
        {
            var index = 1;
            if (filter.Track is not null)
            {
                Bind(cmd, index++, filter.Track);
            }

            if (filter.SessionType is not null)
            {
                Bind(cmd, index++, filter.SessionType);
            }

            if (fromUtc is not null)
            {
                Bind(cmd, index++, fromUtc);
            }

            if (withPaging)
            {
                Bind(cmd, index++, (long)pageSize);
                Bind(cmd, index++, (long)(pageSize * page));
            }
        }

        var items = new List<SessionCard>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT s.id, s.session_uid, s.session_type, s.track, s.started_utc, s.finalized,
                       -- Race length = the most laps any single car completed (a 5-lap race
                       -- is 5, not 22 cars × 5 = 110; the player's car for 255 = no player).
                       (SELECT COALESCE(MAX(cnt), 0) FROM (
                            SELECT COUNT(*) AS cnt FROM laps l
                            WHERE l.session_id = s.id GROUP BY l.car_index)),
                       (SELECT COUNT(*) FROM drivers d WHERE d.session_id = s.id),
                       COALESCE(
                           (SELECT r.best_lap_ms FROM results r
                            WHERE r.session_id = s.id AND r.car_index = s.player_car_index
                              AND r.best_lap_ms > 0 LIMIT 1),
                           (SELECT MIN(l2.lap_time_ms) FROM laps l2
                            WHERE l2.session_id = s.id AND l2.car_index = s.player_car_index
                              AND l2.lap_time_ms > 0), 0),
                       COALESCE(
                           (SELECT r.team FROM results r
                            WHERE r.session_id = s.id AND r.car_index = s.player_car_index LIMIT 1),
                           (SELECT d.team FROM drivers d
                            WHERE d.session_id = s.id AND d.car_index = s.player_car_index LIMIT 1), ''),
                       COALESCE(
                           (SELECT r.position FROM results r
                            WHERE r.session_id = s.id AND r.car_index = s.player_car_index
                              AND r.position > 0 LIMIT 1), 0)
                FROM sessions s
                {whereSql}
                ORDER BY s.started_utc DESC
                LIMIT {limitParam} OFFSET {offsetParam}
                """;
            BindAll(cmd, withPaging: true);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new SessionCard(
                    reader.GetInt64(0),
                    (ulong)reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    ToUint(reader.GetInt64(8)),
                    reader.GetString(9),
                    reader.GetInt32(10)));
            }
        }

        int total;
        using (var cmd = _connection.CreateCommand())
        {
            total = checked((int)QueryLong($"SELECT COUNT(*) FROM sessions s {whereSql}",
                cmd => BindAll(cmd, withPaging: false)));
        }

        return new SessionCardPage(items, total);
    }

    /// <summary>Every track stored on at least one session (enum names, possibly
    /// "F1_"-prefixed), alphabetical — the Strecke filter dropdown.</summary>
    public IReadOnlyList<string> GetDistinctTracks()
    {
        return QueryStrings("SELECT DISTINCT track FROM sessions ORDER BY track");
    }

    /// <summary>Every session type stored on at least one session, alphabetical — the
    /// Session-Typ filter dropdown.</summary>
    public IReadOnlyList<string> GetDistinctSessionTypes()
    {
        return QueryStrings("SELECT DISTINCT session_type FROM sessions ORDER BY session_type");
    }

    private IReadOnlyList<string> QueryStrings(string sql)
    {
        var list = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Stored end-of-session classification rows, position order.</summary>
    public IReadOnlyList<FinalResultRow> GetResults(long sessionId)
    {
        var list = new List<FinalResultRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, position, name, team, race_number, num_laps, grid_position,
                   points, result_status, best_lap_ms, total_race_seconds,
                   penalties_time, num_penalties, num_pit_stops, result_reason
            FROM results WHERE session_id = @p1 ORDER BY position
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FinalResultRow(
                (byte)reader.GetInt64(1),
                (byte)reader.GetInt64(0),
                reader.GetString(2),
                Enum.TryParse<Team>(reader.GetString(3), out var team) ? team : (Team)0,
                (ushort)reader.GetInt64(4),
                (byte)reader.GetInt64(5),
                (byte)reader.GetInt64(6),
                (float)reader.GetDouble(7),
                Enum.TryParse<ResultStatus>(reader.GetString(8), out var status) ? status : (ResultStatus)0,
                (uint)reader.GetInt64(9),
                reader.GetDouble(10),
                (byte)reader.GetInt64(11),
                (byte)reader.GetInt64(12),
                NumPitStops: (byte)reader.GetInt64(13),
                ResultReason: reader.GetString(14)));
        }

        return list;
    }

    /// <summary>car_index → driver name for a session (from the drivers table).</summary>
    public IReadOnlyDictionary<byte, string> GetDriverNames(long sessionId)
    {
        var map = new Dictionary<byte, string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT car_index, name FROM drivers WHERE session_id = @p1";
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[(byte)reader.GetInt64(0)] = reader.GetString(1);
        }

        return map;
    }

    /// <summary>All stored laps of a session, car then lap order.</summary>
    public IReadOnlyList<LapCompleted> GetLaps(long sessionId)
    {
        var list = new List<LapCompleted>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT car_index, lap_number, lap_time_ms, sector1_ms, sector2_ms,
                   tyre, tyre_age_laps, position, ers_used_j
            FROM laps WHERE session_id = @p1 ORDER BY car_index, lap_number
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LapCompleted(
                (byte)reader.GetInt64(0),
                string.Empty, // name lives in drivers table
                (byte)reader.GetInt64(1),
                (uint)reader.GetInt64(2),
                (ushort)reader.GetInt64(3),
                (ushort)reader.GetInt64(4),
                Enum.TryParse<ActualCompound>(reader.GetString(5), out var tyre)
                    ? tyre
                    : (ActualCompound)0,
                (byte)reader.GetInt64(6),
                (byte)reader.GetInt64(7),
                (float)reader.GetDouble(8)));
        }

        return list;
    }

    /// <summary>Stored events of a session, sequence order. When <paramref name="playerCarIndex"/>
    /// is a valid car index, only events the player was involved in are returned (their
    /// car index or, for collisions, the second car index) — session-wide events without
    /// a car index are excluded, so the timeline shows what the player themselves got.</summary>
    public IReadOnlyList<RaceEventEntry> GetEvents(long sessionId, byte? playerCarIndex = null)
    {
        var filterPlayer = playerCarIndex is not null && playerCarIndex.Value < TelemetryConstants.MaxCars;
        var list = new List<RaceEventEntry>();
        using var cmd = _connection.CreateCommand();
        // Single interpolated literal so the line breaks between the WHERE clause and the
        // injected player filter / ORDER BY stay intact.
        var eventFilter = filterPlayer
            ? "AND (car_index = @p2 OR second_car_index = @p2)"
            : string.Empty;
        cmd.CommandText = $"""
            SELECT seq, utc, type, text, car_index, lap_number, second_car_index, detail_value,
                   lap_distance
            FROM events WHERE session_id = @p1
            {eventFilter}
            ORDER BY seq
            """;
        Bind(cmd, 1, sessionId);
        if (filterPlayer)
        {
            Bind(cmd, 2, (long)playerCarIndex!.Value);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RaceEventEntry(
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.IsDBNull(4) ? null : (byte)reader.GetInt64(4),
                reader.GetString(3),
                reader.GetInt64(0),
                (int)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : (byte)reader.GetInt64(6),
                (int)reader.GetInt64(7),
                (float)reader.GetDouble(8)));
        }

        return list;
    }

    /// <summary>Detected overtakes of a session, chronological. When <paramref name="playerCarIndex"/>
    /// is a valid car index, only overtakes the player took part in (as overtaker OR as the
    /// passed driver) are returned.</summary>
    public IReadOnlyList<StoredOvertake> GetOvertakes(long sessionId, byte? playerCarIndex = null)
    {
        var filterPlayer = playerCarIndex is not null && playerCarIndex.Value < TelemetryConstants.MaxCars;
        var list = new List<StoredOvertake>();
        using var cmd = _connection.CreateCommand();
        // Single interpolated literal so the line breaks around the injected player filter stay intact.
        var overtakeFilter = filterPlayer
            ? "AND (car_index = @p2 OR passed_car_index = @p2)"
            : string.Empty;
        cmd.CommandText = $"""
            SELECT lap_number, car_index, driver_name, passed_car_index, passed_driver_name,
                   new_position, utc FROM overtakes WHERE session_id = @p1
            {overtakeFilter}
            ORDER BY id
            """;
        Bind(cmd, 1, sessionId);
        if (filterPlayer)
        {
            Bind(cmd, 2, (long)playerCarIndex!.Value);
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new StoredOvertake(
                (byte)reader.GetInt64(0),
                (byte)reader.GetInt64(1),
                reader.GetString(2),
                (byte)reader.GetInt64(3),
                reader.GetString(4),
                (byte)reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    /// <summary>One stored overtake (positions at detection time).</summary>
    public sealed record StoredOvertake(
        byte LapNumber,
        byte CarIndex,
        string DriverName,
        byte PassedCarIndex,
        string PassedDriverName,
        byte NewPosition,
        DateTimeOffset Utc);

    /// <summary>Player career aggregates over stored races: the player's result row is the
    /// one whose car_index matches the session's player_car_index. "Best track" = lowest
    /// average finishing position (ties: most races), minimum one race.</summary>
    public sealed record CareerStats(
        int Races,
        int Wins,
        int Podiums,
        double AverageGridPosition,
        double Points,
        uint BestLapMs,
        string BestTrack,
        double BestTrackAvgPosition);

    public CareerStats GetCareerStats()
    {
        uint bestLap = 0;
        string bestTrack = string.Empty;
        var bestTrackAvg = 0.0;
        int races = 0, wins = 0, podiums = 0;
        double avgGrid = 0, points = 0;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN r.position = 1 THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN r.position BETWEEN 1 AND 3 THEN 1 ELSE 0 END), 0),
                       COALESCE(AVG(CASE WHEN r.grid_position > 0 THEN r.grid_position END), 0),
                       COALESCE(SUM(r.points), 0),
                       COALESCE(MIN(CASE WHEN r.best_lap_ms > 0 THEN r.best_lap_ms END), 0)
                FROM results r
                JOIN sessions s ON s.id = r.session_id
                WHERE r.car_index = s.player_car_index
                  AND s.session_type = 'Race'
                  AND r.position > 0
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                races = reader.GetInt32(0);
                wins = (int)reader.GetInt64(1);
                podiums = (int)reader.GetInt64(2);
                avgGrid = reader.GetDouble(3);
                points = reader.GetDouble(4);
                bestLap = reader.IsDBNull(5) ? 0u : (uint)reader.GetInt64(5);
            }
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.track, COUNT(*), AVG(r.position)
                FROM results r
                JOIN sessions s ON s.id = r.session_id
                WHERE r.car_index = s.player_car_index
                  AND s.session_type = 'Race'
                  AND r.position > 0
                GROUP BY s.track
                ORDER BY AVG(r.position), COUNT(*) DESC
                LIMIT 1
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                bestTrack = reader.GetString(0);
                bestTrackAvg = reader.GetDouble(2);
            }
        }

        return new CareerStats(
            races, wins, podiums, avgGrid, points, bestLap, bestTrack, bestTrackAvg);
    }

    /// <summary>Inputs for the cross-session mini-championship: every finalized Race session
    /// with its classified results (driver + constructor standings), plus the player's own
    /// results in chronological order (form curve).</summary>
    public ChampionshipData GetChampionship()
    {
        var races = new List<ChampionshipRace>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.id, s.track, s.started_utc
                FROM sessions s
                WHERE s.session_type = 'Race' AND s.finalized = 1
                ORDER BY s.started_utc
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sessionId = reader.GetInt64(0);
                var results = GetResults(sessionId)
                    .Where(r => r.Position > 0)
                    .Select(r => new ChampionshipResult(r.Name, r.Team, r.Position, r.Points))
                    .ToList();
                races.Add(new ChampionshipRace(sessionId, reader.GetString(1), reader.GetString(2), results));
            }
        }

        var playerRaces = new List<PlayerRaceResult>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.track, s.started_utc, r.position, r.points
                FROM results r
                JOIN sessions s ON s.id = r.session_id
                WHERE r.car_index = s.player_car_index
                  AND s.session_type = 'Race'
                  AND s.finalized = 1
                  AND r.position > 0
                ORDER BY s.started_utc
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                playerRaces.Add(new PlayerRaceResult(
                    reader.GetString(0),
                    reader.GetString(1),
                    (byte)reader.GetInt64(2),
                    (float)reader.GetDouble(3)));
            }
        }

        return new ChampionshipData(races, playerRaces);
    }

    /// <summary>The player's name from the most recent finalized Race session (the car
    /// whose index matches the session's player_car_index); null when there is none.
    /// Used to highlight the player's row in the local ELO leaderboard.</summary>
    public string? GetPlayerName()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.name
            FROM results r
            JOIN sessions s ON s.id = r.session_id
            WHERE r.car_index = s.player_car_index
              AND s.session_type = 'Race'
              AND s.finalized = 1
            ORDER BY s.started_utc DESC
            LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }
}

/// <summary>Raw inputs for <see cref="ChampionshipCalculator"/>: all finalized Race sessions
/// plus the player's own chronological results.</summary>
public sealed record ChampionshipData(
    IReadOnlyList<ChampionshipRace> Races,
    IReadOnlyList<PlayerRaceResult> PlayerRaces);