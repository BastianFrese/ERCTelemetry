using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Phase 3 gate: empty session → empty report, per-driver aggregates from stored
/// laps, grid→finish gains and the duel projection.</summary>
public class RaceReportBuilderTests : IDisposable
{
    private readonly TelemetryDb _db;

    public RaceReportBuilderTests() =>
        _db = new TelemetryDb(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db"));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Unknown_session_yields_empty_report()
    {
        var report = RaceReportBuilder.Build(_db, sessionId: 4711);

        Assert.Null(report.Header);
        Assert.Empty(report.Classification);
        Assert.Null(report.Player);
        Assert.Empty(report.LapSeries);
        Assert.Null(report.Duel);
        Assert.Empty(report.Timeline);
        Assert.Null(report.Setup);
        Assert.Empty(report.Traces);
    }

    private long SeedRace()
    {
        var meta = new SessionMeta(21, SessionType.Race, Track.Bahrain, 3, 5412, true, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);
        var sessionId = _db.OpenSession(meta);
        _db.UpsertDrivers(sessionId, [
            new DriverEntry(0, "Alice", Team.McLaren, 7, false, true, false),
            new DriverEntry(1, "Bob", Team.Ferrari, 44, true, false, false),
        ]);
        _db.MergeLapHistory(sessionId, new LapHistoryUpdated(0, 2,
            [new LapHistoryRow(1, 90_000u, 30_000u, 30_000u, 30_000u, 15)],
            [new StoredTyreStint(0, ActualCompound.F1C3, VisualCompound.F1Soft, 255)],
            1, 0, 0, 0));
        _db.MergeLapHistory(sessionId, new LapHistoryUpdated(1, 2,
            [new LapHistoryRow(1, 91_000u, 30_400u, 30_200u, 30_400u, 15)],
            [new StoredTyreStint(0, ActualCompound.F1C4, VisualCompound.F1Medium, 255)],
            1, 0, 0, 0));
        _db.AppendLap(sessionId, new LapCompleted(0, "Alice", 2, 89_500u, 30_100, 30_000,
            ActualCompound.F1C3, 1, 1, 0f));
        _db.AppendLap(sessionId, new LapCompleted(1, "Bob", 2, 92_500u, 31_000, 30_500,
            ActualCompound.F1C4, 2, 2, 0f));
        _db.FinalizeSession(sessionId, "checkered", [
            new FinalResultRow(1, 0, "Alice", Team.McLaren, 7, 2, 5, 25f, ResultStatus.Active, 89_500u, 181.5, 0, 0,
                NumPitStops: 1, ResultReason: "Finished"),
            new FinalResultRow(2, 1, "Bob", Team.Ferrari, 44, 2, 8, 18f, ResultStatus.Active, 91_000u, 183.9, 0, 0,
                NumPitStops: 0, ResultReason: "Finished"),
        ]);
        _db.FinalizeSessionExtras(sessionId, [
            new FinalResultRow(1, 0, "Alice", Team.McLaren, 7, 2, 5, 25f, ResultStatus.Active, 89_500u, 181.5, 0, 0,
                NumPitStops: 1, ResultReason: "Finished"),
            new FinalResultRow(2, 1, "Bob", Team.Ferrari, 44, 2, 8, 18f, ResultStatus.Active, 91_000u, 183.9, 0, 0,
                NumPitStops: 0, ResultReason: "Finished"),
        ]);
        return sessionId;
    }

    [Fact]
    public void Seeded_race_builds_full_report()
    {
        var sessionId = SeedRace();
        var report = RaceReportBuilder.Build(_db, sessionId);

        Assert.NotNull(report.Header);
        Assert.Equal("Bahrain", report.Header.Track);
        Assert.Equal(2, report.Header.NumDrivers);

        // Classification: position order, Alice P1, Bob P2.
        Assert.Equal(2, report.Classification.Count);
        Assert.Equal("Alice", report.Classification[0].Name);
        Assert.Equal("Bob", report.Classification[1].Name);
        Assert.Equal((byte)1, report.Classification[0].Position);
        Assert.Equal(25f, report.Classification[0].Points);
        Assert.Equal((ushort)5, report.Classification[0].GridPosition);
        Assert.Equal(1, report.Classification[0].NumPitStops);
        Assert.Equal("Finished", report.Classification[0].ResultReason);

        // Grid 5 → finish 1 = 4 positions gained.
        Assert.Equal(4, report.Classification[0].PositionsGained);
        Assert.Equal(6, report.Classification[1].PositionsGained); // 8 → 2

        // Player summary is Alice (player car index 0).
        Assert.NotNull(report.Player);
        Assert.Equal("Alice", report.Player.Name);

        // Best laps from the merged lap rows.
        Assert.Equal(89_500u, report.Player.BestLapMs);
        Assert.Equal(1, report.Player.LapsLed);   // lap 1 (P1 by history) — Alice only led lap 1
        Assert.Equal(1, report.Player.LapsValid);

        // Sector best from the merged rows: S1 30_000, S2 30_000, S3 30_000.
        Assert.Equal(30_000u, report.Player.S1.BestMs);
        Assert.Equal(30_000u, report.Player.S2.BestMs);
        Assert.Equal(30_000u, report.Player.S3.BestMs);

        // Lap series: one per driver, two points for Alice (laps 1+2).
        Assert.Equal(2, report.LapSeries.Count);
        Assert.Equal(2, report.LapSeries[0].Points.Count);
        Assert.Equal(2, report.LapSeries[0].Points[1].LapNumber);

        // Duel A vs. B: two joined laps, Alice faster on both.
        var duel = RaceReportBuilder.Build(_db, sessionId, carA: 0, carB: 1).Duel;
        Assert.NotNull(duel);
        Assert.Equal(2, duel.LapWinsA);
        Assert.Equal(0, duel.LapWinsB);
        Assert.Equal(2, duel.LapPairs.Count);
        Assert.Equal(90_000u, duel.LapPairs[0].LapTimeA);
        Assert.Equal(91_000u, duel.LapPairs[0].LapTimeB);
        Assert.Equal(92_500u, duel.LapPairs[1].LapTimeB);

        // Sector wins over the two joined laps: Alice sweeps all sectors.
        Assert.Equal(2, duel.SectorWins.A1);
        Assert.Equal(0, duel.SectorWins.B1);
        Assert.Equal(2, duel.SectorWins.A2);
        Assert.Equal(0, duel.SectorWins.B2);
        Assert.Equal(1, duel.SectorWins.A3);
        Assert.Equal(0, duel.SectorWins.B3);

        // Best-lap delta A − B = 89_500 − 91_000 = −1_500.
        Assert.Equal(-1_500.0, duel.BestDeltaMs, precision: 1);
    }

    [Fact]
    public void Dns_car_without_lap_history_does_not_crash()
    {
        // Charlie car 2 appears in the final classification but never set a lap — the
        // DNS/DQ case. Lap-series build must not KeyNotFoundException a byCar lookup.
        var meta = new SessionMeta(22, SessionType.Race, Track.Bahrain, 3, 5412, true, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);
        var sessionId = _db.OpenSession(meta);
        _db.UpsertDrivers(sessionId, [
            new DriverEntry(0, "Alice", Team.McLaren, 7, false, true, false),
            new DriverEntry(1, "Bob", Team.Ferrari, 44, true, false, false),
            new DriverEntry(2, "Charlie", Team.RedBullRacing, 1, false, false, false),
        ]);
        _db.MergeLapHistory(sessionId, new LapHistoryUpdated(0, 2,
            [new LapHistoryRow(1, 90_000u, 30_000u, 30_000u, 30_000u, 15)],
            [new StoredTyreStint(0, ActualCompound.F1C3, VisualCompound.F1Soft, 255)],
            1, 0, 0, 0));
        _db.FinalizeSession(sessionId, "checkered", [
            new FinalResultRow(1, 0, "Alice", Team.McLaren, 7, 2, 5, 25f, ResultStatus.Active, 89_500u, 181.5, 0, 0,
                NumPitStops: 1, ResultReason: "Finished"),
            new FinalResultRow(2, 1, "Bob", Team.Ferrari, 44, 2, 8, 18f, ResultStatus.Active, 91_000u, 183.9, 0, 0,
                NumPitStops: 0, ResultReason: "Finished"),
            new FinalResultRow(3, 2, "Charlie", Team.RedBullRacing, 1, 0, 0, 0f, ResultStatus.Active, 0u, 0.0, 0, 0,
                NumPitStops: 0, ResultReason: "DNF"),
        ]);

        var report = RaceReportBuilder.Build(_db, sessionId);

        Assert.Equal(3, report.Classification.Count);
        // Charlie is classified but has an empty lap series — the crash before the fix.
        var charlie = report.LapSeries.Single(s => s.CarIndex == 2);
        Assert.Empty(charlie.Points);
    }
}