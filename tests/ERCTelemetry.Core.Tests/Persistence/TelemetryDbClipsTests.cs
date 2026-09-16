using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>Clips table: schema creation, AppendClip/GetClips round-trip (including the
/// environment-collision nulls) and the session-uid → row-id lookup used by the pump.</summary>
public class TelemetryDbClipsTests : IDisposable
{
    private readonly TelemetryDb _db;

    public TelemetryDbClipsTests() =>
        _db = new TelemetryDb(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db"));

    public void Dispose() => _db.Dispose();

    private static long OpenSession(TelemetryDb db, ulong uid = 77) =>
        db.OpenSession(new SessionMeta(
            uid, SessionType.Race, Track.Bahrain, 12, 5412, true, 3,
            GameMode.OnlineCustom, Weather.LightRain, 31, 27, 0));

    [Fact]
    public void Append_clip_round_trips_all_fields()
    {
        var sessionId = OpenSession(_db);
        var clip = new ClipSaved(
            SessionUid: 77,
            Utc: new DateTimeOffset(2026, 9, 7, 18, 30, 0, TimeSpan.Zero),
            FilePath: @"C:\clips\77\clip-20260907-183000-L12.mp4",
            LapNumber: 12,
            CarIndex: 3,
            SecondCarIndex: 7,
            DriverName: "Basti",
            SecondDriverName: "Rival",
            Severity: 2,
            DurationSeconds: 20.5,
            FileSizeBytes: 1_234_567);

        _db.AppendClip(sessionId, clip);

        var stored = Assert.Single(_db.GetClips(sessionId));
        Assert.Equal(clip.Utc, stored.Utc);
        Assert.Equal(clip.FilePath, stored.FilePath);
        Assert.Equal(12, stored.LapNumber);
        Assert.Equal((byte)3, stored.CarIndex);
        Assert.Equal((byte)7, stored.SecondCarIndex);
        Assert.Equal("Basti", stored.DriverName);
        Assert.Equal("Rival", stored.SecondDriverName);
        Assert.Equal(2, stored.Severity);
        Assert.Equal(20.5, stored.DurationSeconds, precision: 3);
        Assert.Equal(1_234_567, stored.FileSizeBytes);
    }

    [Fact]
    public void Environment_collision_round_trips_null_second_car()
    {
        var sessionId = OpenSession(_db);
        var clip = new ClipSaved(
            SessionUid: 77,
            Utc: new DateTimeOffset(2026, 9, 7, 18, 31, 0, TimeSpan.Zero),
            FilePath: @"C:\clips\77\clip-20260907-183100-L13.mp4",
            LapNumber: 13,
            CarIndex: 3,
            SecondCarIndex: null,
            DriverName: "Basti",
            SecondDriverName: null,
            Severity: 1,
            DurationSeconds: 20.0,
            FileSizeBytes: 900_000);

        _db.AppendClip(sessionId, clip);

        var stored = Assert.Single(_db.GetClips(sessionId));
        Assert.Null(stored.SecondCarIndex);
        Assert.Null(stored.SecondDriverName);
    }

    [Fact]
    public void Get_clips_returns_empty_for_session_without_clips()
    {
        var sessionId = OpenSession(_db);

        Assert.Empty(_db.GetClips(sessionId));
    }

    [Fact]
    public void Get_clips_orders_newest_first()
    {
        var sessionId = OpenSession(_db);
        _db.AppendClip(sessionId, new ClipSaved(
            77, new DateTimeOffset(2026, 9, 7, 18, 30, 0, TimeSpan.Zero),
            @"C:\clips\77\a.mp4", 12, 3, 7, "Basti", "Rival", 1, 20, 100));
        _db.AppendClip(sessionId, new ClipSaved(
            77, new DateTimeOffset(2026, 9, 7, 18, 40, 0, TimeSpan.Zero),
            @"C:\clips\77\b.mp4", 14, 3, 9, "Basti", "Other", 2, 20, 200));

        var clips = _db.GetClips(sessionId);

        Assert.Equal(2, clips.Count);
        Assert.Equal(@"C:\clips\77\b.mp4", clips[0].FilePath); // newest first
        Assert.Equal(@"C:\clips\77\a.mp4", clips[1].FilePath);
    }

    [Fact]
    public void Get_session_id_by_uid_resolves_and_returns_null_for_unknown()
    {
        var sessionId = OpenSession(_db, uid: 99);

        Assert.Equal(sessionId, _db.GetSessionIdByUid(99));
        Assert.Null(_db.GetSessionIdByUid(12345));
    }
}
