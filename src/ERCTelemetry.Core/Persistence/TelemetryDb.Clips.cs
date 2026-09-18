using System.Globalization;
using ERCTelemetry.Core.Clips;

namespace ERCTelemetry.Core.Persistence;

/// <summary>Collision-clip schema, write API and read API (partial of TelemetryDb, kept
/// out of TelemetryDb.cs for the file-size budget). The clips table links a recorded
/// MP4 file to its session with the collision facts shown in the History tab.</summary>
public sealed partial class TelemetryDb
{
    private void EnsureClipsSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS clips (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                utc TEXT NOT NULL,               -- ISO-8601
                file_path TEXT NOT NULL,
                lap_number INTEGER NOT NULL DEFAULT 0,
                car_index INTEGER NOT NULL,
                second_car_index INTEGER,        -- NULL = environment
                driver_name TEXT NOT NULL,
                second_driver_name TEXT,         -- NULL = environment
                severity INTEGER NOT NULL,       -- 0=gering, 1=mittel, 2=hoch
                duration_seconds REAL NOT NULL,
                file_size_bytes INTEGER NOT NULL
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_clips_session ON clips(session_id);");
    }

    /// <summary>Resolves a session row id by its uid (the ClipSaved event carries the
    /// uid, not the row id). Null when the session was never opened in this DB.</summary>
    public long? GetSessionIdByUid(ulong uid)
    {
        using var connection = OpenReader();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM sessions WHERE session_uid = @p1";
        Bind(cmd, 1, (long)uid);
        var result = cmd.ExecuteScalar();
        return result is null ? null : (long)result;
    }

    /// <summary>Stores one recorded clip for a session.</summary>
    public void AppendClip(long sessionId, ClipSaved clip)
    {
        Exec("""
            INSERT INTO clips (session_id, utc, file_path, lap_number, car_index,
                second_car_index, driver_name, second_driver_name, severity,
                duration_seconds, file_size_bytes)
            VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)
            """, cmd =>
        {
            Bind(cmd, 1, sessionId);
            Bind(cmd, 2, clip.Utc.ToString("o", CultureInfo.InvariantCulture));
            Bind(cmd, 3, clip.FilePath);
            Bind(cmd, 4, (long)clip.LapNumber);
            Bind(cmd, 5, (long)clip.CarIndex);
            Bind(cmd, 6, clip.SecondCarIndex is { } second ? (object)(long)second : DBNull.Value);
            Bind(cmd, 7, clip.DriverName);
            Bind(cmd, 8, clip.SecondDriverName is { } secondName ? (object)secondName : DBNull.Value);
            Bind(cmd, 9, (long)clip.Severity);
            Bind(cmd, 10, clip.DurationSeconds);
            Bind(cmd, 11, clip.FileSizeBytes);
        });
    }

    /// <summary>One stored clip with the collision facts shown in the History tab.</summary>
    public sealed record StoredClip(
        long Id,
        DateTimeOffset Utc,
        string FilePath,
        int LapNumber,
        byte CarIndex,
        byte? SecondCarIndex,
        string DriverName,
        string? SecondDriverName,
        int Severity,
        double DurationSeconds,
        long FileSizeBytes);

    /// <summary>All clips of a session, newest first.</summary>
    public IReadOnlyList<StoredClip> GetClips(long sessionId)
    {
        var list = new List<StoredClip>();
        using var connection = OpenReader();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, utc, file_path, lap_number, car_index, second_car_index,
                   driver_name, second_driver_name, severity, duration_seconds, file_size_bytes
            FROM clips WHERE session_id = @p1 ORDER BY utc DESC
            """;
        Bind(cmd, 1, sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new StoredClip(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetInt32(3),
                (byte)Math.Clamp(reader.GetInt64(4), 0, byte.MaxValue),
                reader.IsDBNull(5) ? null : (byte)reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetDouble(9),
                reader.GetInt64(10)));
        }

        return list;
    }
}
