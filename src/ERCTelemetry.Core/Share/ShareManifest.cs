namespace ERCTelemetry.Core.Share;

/// <summary>One collision clip of a shared session — the MP4 file lives on the share
/// server under <see cref="FileName"/> (a plain clip-*.mp4 name).</summary>
public sealed record ShareClip(
    string FileName,
    int LapNumber,
    byte CarIndex,
    byte? SecondCarIndex,
    string DriverName,
    string? SecondDriverName,
    int Severity,
    double DurationSeconds);

/// <summary>One final-classification row of a shared session.</summary>
public sealed record ShareResultRow(
    byte Position,
    string Name,
    string Team,
    byte Laps,
    uint BestLapMs,
    double TotalSeconds,
    string Status,
    float Points);

/// <summary>Everything the share server needs to render a public session page: header
/// facts, the final results and the collision clips. Serialized as manifest.json.</summary>
public sealed record ShareManifest(
    ulong SessionUid,
    string Track,
    string SessionType,
    DateTimeOffset StartUtc,
    int DurationSeconds,
    byte PlayerCarIndex,
    IReadOnlyList<ShareResultRow> Results,
    IReadOnlyList<ShareClip> Clips);
