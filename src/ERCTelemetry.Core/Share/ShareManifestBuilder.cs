using System.Globalization;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Share;

/// <summary>Builds a <see cref="ShareManifest"/> from the stored session facts. Pure —
/// no I/O, so the History tab can share a session without touching the database twice.</summary>
public static class ShareManifestBuilder
{
    /// <summary>Maps the DB rows to the share manifest. Clip file names are reduced to
    /// their plain file name (the server only ever sees clip-*.mp4).</summary>
    public static ShareManifest Build(
        TelemetryDb.SessionRow session,
        IReadOnlyList<FinalResultRow> results,
        IReadOnlyList<TelemetryDb.StoredClip> clips)
    {
        return new ShareManifest(
            session.SessionUid,
            session.Track,
            session.SessionType,
            DateTimeOffset.Parse(session.StartedUtc, CultureInfo.InvariantCulture),
            session.SessionDuration,
            session.PlayerCarIndex,
            results.Select(r => new ShareResultRow(
                r.Position,
                r.Name,
                r.Team.Display(),
                r.NumLaps,
                r.BestLapTimeMs,
                r.TotalRaceTimeSeconds,
                r.ResultStatus.ToString(),
                r.Points)).ToArray(),
            clips.Select(c => new ShareClip(
                Path.GetFileName(c.FilePath),
                c.LapNumber,
                c.CarIndex,
                c.SecondCarIndex,
                c.DriverName,
                c.SecondDriverName,
                c.Severity,
                c.DurationSeconds)).ToArray());
    }
}
