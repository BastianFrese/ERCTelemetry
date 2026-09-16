using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Headless tests for the pace analytics (stints, degradation, consistency).</summary>
public sealed class PaceAnalyzerTests
{
    private static LapCompleted Lap(byte lap, uint ms, ActualCompound tyre = ActualCompound.F1C3,
        byte age = 0, byte carIndex = 0) =>
        new(0, "Player", lap, ms, 0, 0, tyre, age, 0);

    [Fact]
    public void Single_compound_run_is_one_stint()
    {
        var report = PaceAnalyzer.Analyze(new[]
        {
            Lap(1, 91_000u, age: 0),
            Lap(2, 91_100u, age: 1),
            Lap(3, 91_200u, age: 2),
        }, 0);

        Assert.Single(report.Stints);
        var stint = report.Stints[0];
        Assert.Equal(1, stint.StartLap);
        Assert.Equal(3, stint.EndLap);
        Assert.Equal(3, stint.Laps);
        Assert.Equal(91_000u, stint.BestLapMs);
        Assert.Equal("C3", stint.Tyre);
        Assert.True(stint.DegradationMsPerLap > 0);
        Assert.True(stint.ConsistencySigmaMs > 0);
    }

    [Fact]
    public void Compound_change_starts_new_stint()
    {
        var report = PaceAnalyzer.Analyze(new[]
        {
            Lap(1, 91_000u, ActualCompound.F1C3),
            Lap(2, 91_400u, ActualCompound.F1C3),
            Lap(3, 90_500u, ActualCompound.F1C4),
        }, 0);

        Assert.Equal(2, report.Stints.Count);
        Assert.Equal("C3", report.Stints[0].Tyre);
        Assert.Equal("C4", report.Stints[1].Tyre);
        Assert.Equal(3, report.Stints[1].StartLap);
    }

    [Fact]
    public void Tyre_age_reset_starts_new_stint()
    {
        var report = PaceAnalyzer.Analyze(new[]
        {
            Lap(5, 91_000u, age: 4),
            Lap(6, 91_100u, age: 5),
            Lap(7, 91_200u, age: 0),
        }, 0);

        Assert.Equal(2, report.Stints.Count);
        Assert.Equal(4, report.Stints[0].AgeAtStart);
        Assert.Equal(0, report.Stints[1].AgeAtStart);
        Assert.Equal(7, report.Stints[1].EndLap);
    }

    [Fact]
    public void Outlier_lap_does_not_skew_average_or_sigma()
    {
        var report = PaceAnalyzer.Analyze(new[]
        {
            Lap(1, 91_000u),
            Lap(2, 91_100u),
            Lap(3, 91_200u),
            Lap(4, 120_000u),
        }, 0);

        var stint = Assert.Single(report.Stints);
        Assert.Equal(3, stint.Laps);
        Assert.True(stint.AverageLapMs < 92_000);
        Assert.True(stint.ConsistencySigmaMs < 500);
        Assert.Equal(3, report.Stints.Sum(s => s.Laps));
    }

    [Fact]
    public void Other_cars_laps_are_ignored()
    {
        var laps = new List<LapCompleted>
        {
            Lap(1, 91_000u, carIndex: 0),
            new(1, "Rival", 1, 89_000u, 0, 0, ActualCompound.F1C3, 0, 0),
        };
        var report = PaceAnalyzer.Analyze(laps, 0);

        Assert.Single(report.Stints);
        Assert.Equal(91_000u, report.BestLapMs);
    }

    [Fact]
    public void Empty_input_gives_empty_report()
    {
        var report = PaceAnalyzer.Analyze([], 0);
        Assert.Empty(report.Stints);
        Assert.Equal(0u, report.BestLapMs);
        Assert.Equal(0.0, report.ConsistencySigmaMs);
    }

    // ---------- Konsistenz (AnalyzeConsistency) ----------

    /// <summary>Lap of an arbitrary driver (the Lap helper above fixes carIndex 0).</summary>
    private static LapCompleted DriverLap(byte carIndex, string name, byte lap, uint ms) =>
        new(carIndex, name, lap, ms, 0, 0);

    [Fact]
    public void AnalyzeConsistency_scores_each_driver_by_lap_spread()
    {
        var rows = PaceAnalyzer.AnalyzeConsistency(
        [
            DriverLap(0, "Consistent", 1, 80_000u),
            DriverLap(0, "Consistent", 2, 80_200u),
            DriverLap(0, "Consistent", 3, 80_400u), // mean 80 200, sample σ 200
            DriverLap(1, "Wobbly", 1, 90_000u),
            DriverLap(1, "Wobbly", 2, 93_500u),     // mean 91 750, σ 2475 — both laps stay (spread < 5 %)
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].CarIndex); // faster best lap sorts first
        Assert.Equal("Consistent", rows[0].DriverName);
        Assert.Equal(80_000u, rows[0].BestLapMs);
        Assert.Equal(3, rows[0].RacingLaps);
        Assert.InRange(rows[0].ConsistencySigmaMs, 199.0, 201.0);
        Assert.InRange(rows[0].Score, 99.0, 100.0);
        Assert.InRange(rows[1].Score, 97.0, 98.0);
    }

    [Fact]
    public void AnalyzeConsistency_excludes_outlier_laps_from_average_and_sigma()
    {
        var rows = PaceAnalyzer.AnalyzeConsistency(
        [
            DriverLap(0, "Player", 1, 80_000u),
            DriverLap(0, "Player", 2, 80_000u),
            DriverLap(0, "Player", 3, 120_000u), // in-lap, > 5 % over the median
        ]);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.RacingLaps);          // outlier excluded
        Assert.Equal(80_000.0, row.AverageLapMs); // unchanged by the in-lap
        Assert.Equal(100.0, row.Score);           // σ 0 over two identical laps
        Assert.Equal(80_000u, row.BestLapMs);     // min over ALL laps, not just racing
    }

    [Fact]
    public void AnalyzeConsistency_single_lap_has_no_score()
    {
        var rows = PaceAnalyzer.AnalyzeConsistency([DriverLap(4, "Solo", 1, 85_000u)]);

        var row = Assert.Single(rows);
        Assert.True(double.IsNaN(row.Score)); // one lap = no spread to grade
        Assert.Equal(0.0, row.ConsistencySigmaMs);
        Assert.Equal(85_000u, row.BestLapMs);
    }

    [Fact]
    public void AnalyzeConsistency_ignores_zero_time_laps()
    {
        var rows = PaceAnalyzer.AnalyzeConsistency(
        [
            DriverLap(3, "Slow", 1, 95_000u),
            DriverLap(1, "Fast", 1, 81_000u),
            DriverLap(1, "Fast", 2, 0u), // zero-time laps carry no metric
        ]);

        Assert.Equal(2, rows.Count); // the zero-time lap creates no row
        Assert.Equal(1, rows[0].CarIndex);
        Assert.Equal(1, rows[0].RacingLaps);
        Assert.Equal(95_000u, rows[1].BestLapMs);
    }
}