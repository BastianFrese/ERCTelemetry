using F1Game.UDP.Enums;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

public class SessionComparerTests
{
    private static LapCompleted Lap(byte car, uint lapMs, ushort s1, ushort s2) => new(
        car, string.Empty, 1, lapMs, s1, s2, ActualCompound.F1C3, 1, 1);

    [Fact]
    public void Computes_best_lap_and_sector_deltas()
    {
        var lapsA = new[] { Lap(0, 90_000, 28_000, 30_000), Lap(0, 89_000, 27_500, 29_500) };
        var lapsB = new[] { Lap(0, 91_000, 28_200, 30_100) };
        var names = new Dictionary<byte, string> { [0] = "Pilot" };

        var row = Assert.Single(SessionComparer.Compare(lapsA, lapsB, names, names));

        Assert.True(row.HasBoth);
        Assert.Equal(89_000u, row.A.LapMs);
        Assert.Equal(91_000u, row.B.LapMs);
        Assert.Equal(32_000, row.A.S3Ms);
        Assert.Equal(27_500, row.A.S1Ms);
        Assert.Equal(28_200, row.B.S1Ms);
        // B slower by 2s on the lap.
        Assert.Equal(2_000, (int)row.B.LapMs - (int)row.A.LapMs);
    }

    [Fact]
    public void Driver_only_in_one_side_has_missing_side_zero()
    {
        var lapsA = new[] { Lap(0, 90_000, 28_000, 30_000) };
        var names = new Dictionary<byte, string> { [0] = "Pilot" };

        var row = Assert.Single(SessionComparer.Compare(
            lapsA, Array.Empty<LapCompleted>(), names, names));

        Assert.False(row.HasBoth);
        Assert.Equal(90_000u, row.A.LapMs);
        Assert.Equal(0u, row.B.LapMs);
        Assert.Equal(0, row.B.S1Ms);
    }

    [Fact]
    public void Zero_laps_are_skipped()
    {
        Assert.Empty(SessionComparer.Compare(
            new[] { Lap(0, 0, 0, 0) }, Array.Empty<LapCompleted>(),
            new Dictionary<byte, string>(), new Dictionary<byte, string>()));
    }

    [Fact]
    public void Drivers_in_both_sides_are_listed_first()
    {
        var lapsA = new[] { Lap(0, 90_000, 28_000, 30_000), Lap(1, 92_000, 29_000, 31_000) };
        var lapsB = new[] { Lap(1, 91_000, 28_500, 30_500) };
        var names = new Dictionary<byte, string> { [0] = "Alpha", [1] = "Beta" };

        var rows = SessionComparer.Compare(lapsA, lapsB, names, names);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Beta", rows[0].Name);      // in both sides
        Assert.True(rows[0].HasBoth);
        Assert.Equal("Alpha", rows[1].Name);     // A only
        Assert.False(rows[1].HasBoth);
    }
}