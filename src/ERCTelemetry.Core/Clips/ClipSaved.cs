using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Clips;

/// <summary>Facts about one collision that triggered a clip, captured at detection time
/// (before the clip is encoded) so the recorder can build the file path and the DB row.
/// SecondCarIndex/SecondDriverName are null when the collision was with the environment.</summary>
public sealed record ClipMetadata(
    ulong SessionUid,
    DateTimeOffset Utc,
    int LapNumber,
    byte CarIndex,
    byte? SecondCarIndex,
    string DriverName,
    string? SecondDriverName,
    int Severity);

/// <summary>A clip was recorded and saved to disk — persistence stores it in the clips
/// table. Routed through the existing event feed so the single-writer PersistencePump
/// stays the only DB writer.</summary>
public sealed record ClipSaved(
    ulong SessionUid,
    DateTimeOffset Utc,
    string FilePath,
    int LapNumber,
    byte CarIndex,
    byte? SecondCarIndex,
    string DriverName,
    string? SecondDriverName,
    int Severity,
    double DurationSeconds,
    long FileSizeBytes) : StoreEvent;
