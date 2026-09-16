using F1Game.UDP.Enums;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

/// <summary>Sector derivation of the leaderboard projection.</summary>
public class StandingsCalculatorTests
{
    private static CarTiming Timing(byte position, uint lastLapMs = 0, ushort s1 = 0, ushort s2 = 0,
        byte sector = 0, ushort bestS1 = 0, ushort bestS2 = 0) =>
        new(0, position, 1, 0f, 0, lastLapMs, s1, s2, 0, 0,
            PitStatus.None, 0, 0, DriverStatus.InGarage, ResultStatus.Active,
            BestSector1Ms: bestS1, BestSector2Ms: bestS2, Sector: sector);

    [Fact]
    public void Sector3_is_derived_from_last_lap_minus_s1_s2()
    {
        var rows = StandingsCalculator.BuildStandings(
            new DriverEntry?[] { new(0, "Driver", Team.Ferrari, 5, false, true, true) },
            new CarTiming?[] { Timing(1, lastLapMs: 88_000, s1: 28_000, s2: 30_000) },
            new CarCondition?[] { null },
            new uint[] { 0 });

        var row = rows.Single();
        Assert.Equal(28_000, row.Sector1TimeMs);
        Assert.Equal(30_000, row.Sector2TimeMs);
        Assert.Equal(30_000, row.Sector3TimeMs);
    }

    [Theory]
    [InlineData(88_000, 0, 30_000)]
    [InlineData(88_000, 28_000, 0)]
    [InlineData(90_000, 44_000, 46_000)]
    [InlineData(88_000, 46_000, 44_000)]
    [InlineData(150_000, 40_000, 45_000)]
    public void Sector3_is_unavailable_when_input_is_incomplete(uint lastLap, ushort s1, ushort s2)
    {
        var rows = StandingsCalculator.BuildStandings(
            new DriverEntry?[] { new(0, "Driver", Team.Ferrari, 5, false, true, true) },
            new CarTiming?[] { Timing(1, lastLapMs: lastLap, s1: s1, s2: s2) },
            new CarCondition?[] { null },
            new uint[] { 0 });

        Assert.Equal(0, rows.Single().Sector3TimeMs);
    }

    [Fact]
    public void Positionless_cars_are_appended_after_ranked_cars()
    {
        var rows = StandingsCalculator.BuildStandings(
            new DriverEntry?[] { null, null },
            new CarTiming?[] { Timing(0), Timing(2) },
            new CarCondition?[] { null, null },
            new uint[] { 0, 0 });

        Assert.Equal(new[] { (byte)2, (byte)0 }, rows.Select(r => r.Position));
    }

    [Fact]
    public void Lap_delta_is_unknown_in_sector_1()
    {
        var timing = Timing(1, s1: 28_000, bestS1: 27_500);

        Assert.Equal(0, StandingsCalculator.LapDeltaMs(timing));
        Assert.Equal(0u, StandingsCalculator.PredictedLapMs(timing, 88_000));
    }

    [Fact]
    public void Lap_delta_compares_completed_s1_while_in_sector_2()
    {
        var timing = Timing(1, s1: 28_000, bestS1: 27_500, sector: 1);

        Assert.Equal(500, StandingsCalculator.LapDeltaMs(timing));
        Assert.Equal(88_500u, StandingsCalculator.PredictedLapMs(timing, 88_000));
    }

    [Fact]
    public void Lap_delta_compares_completed_s1_s2_while_in_sector_3()
    {
        var timing = Timing(1, s1: 28_000, s2: 30_500, bestS1: 27_500, bestS2: 30_000, sector: 2);

        // 28_000 + 30_500 vs. 27_500 + 30_000 → +1000 on the lap; predicted assumes best S3.
        Assert.Equal(1_000, StandingsCalculator.LapDeltaMs(timing));
        Assert.Equal(89_000u, StandingsCalculator.PredictedLapMs(timing, 88_000));
    }

    [Fact]
    public void Lap_delta_is_negative_when_running_under_the_reference()
    {
        var timing = Timing(1, s1: 27_000, bestS1: 27_500, sector: 1);

        Assert.Equal(-500, StandingsCalculator.LapDeltaMs(timing));
    }

    [Fact]
    public void Lap_delta_is_unknown_without_reference_sectors()
    {
        var timing = Timing(1, s1: 28_000, sector: 1);

        Assert.Equal(0, StandingsCalculator.LapDeltaMs(timing));
        Assert.Equal(0u, StandingsCalculator.PredictedLapMs(timing, 88_000));
    }

    [Fact]
    public void Predicted_lap_needs_a_best_lap()
    {
        var timing = Timing(1, s1: 28_000, bestS1: 27_500, sector: 1);

        Assert.Equal(0u, StandingsCalculator.PredictedLapMs(timing, 0));
    }

    [Fact]
    public void Standings_rows_carry_the_live_delta_and_predicted_lap()
    {
        var rows = StandingsCalculator.BuildStandings(
            new DriverEntry?[] { new(0, "Driver", Team.Ferrari, 5, false, true, true) },
            new CarTiming?[] { Timing(1, s1: 28_000, bestS1: 27_500, sector: 1) },
            new CarCondition?[] { null },
            new uint[] { 88_000 });

        var row = rows.Single();
        Assert.Equal(500, row.LapDeltaMs);
        Assert.Equal(88_500u, row.PredictedLapMs);
    }
}