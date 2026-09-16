using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Phase 3 gate: hand-computed lap/sector tallies, deltas, tie handling,
/// stint pairing and the trace-duel skip.</summary>
public class DriverDuelTests
{
    private static PerDriverSummary Summary(byte car, uint bestMs, double avgMs, double sigmaMs) =>
        new(car, $"D{car}", default, 0, 1, 0f, "", "",
            0, 0, 2, bestMs, avgMs, sigmaMs,
            new SectorStats(0, 0, 0), new SectorStats(0, 0, 0), new SectorStats(0, 0, 0),
            0, 0, 0, 0, 0, 0, 0.0, [], null);

    [Fact]
    public void Ties_count_neither_side()
    {
        // Lap 1: identical times → tie. Lap 2: A faster. Lap 3: B faster.
        var lapsA = new List<LapPoint>
        {
            new(1, 90_000u, 30_000u, 30_000u, 30_000u, 1, "", 1),
            new(2, 90_000u, 30_000u, 30_000u, 30_000u, 1, "", 1),
            new(3, 92_000u, 30_600u, 30_600u, 30_800u, 1, "", 1),
        };
        var lapsB = new List<LapPoint>
        {
            new(1, 90_000u, 30_000u, 30_000u, 30_000u, 1, "", 2),
            new(2, 91_000u, 30_400u, 30_200u, 30_400u, 1, "", 2),
            new(3, 91_000u, 30_200u, 30_200u, 30_600u, 1, "", 2),
        };

        var duel = DriverDuel.Compare(Summary(0, 90_000u, 90_666.7, 0), Summary(1, 91_000u, 90_666.7, 0), lapsA, lapsB, null, null);

        Assert.Equal(3, duel.LapPairs.Count);
        Assert.Equal(1, duel.LapWinsA); // lap 3 only (lap 1 tie, lap 2 A)
        Assert.Equal(1, duel.LapWinsB); // lap 2 only
    }

    [Fact]
    public void Sector_wins_respect_zero_and_ties()
    {
        var lapsA = new List<LapPoint> { new(1, 90_000u, 30_000u, 30_000u, 30_000u, 1, "", 1) };
        var lapsB = new List<LapPoint>
        {
            new(1, 90_500u, 29_900u, 30_000u, 30_000u, 1, "", 2),
            new(2, 91_000u, 0u, 0u, 0u, 1, "", 2), // B-only lap: not joined
        };

        var duel = DriverDuel.Compare(Summary(0, 90_000u, 90_000, 0), Summary(1, 90_500u, 90_750, 0), lapsA, lapsB, null, null);

        Assert.Single(duel.LapPairs);
        // S1: A 30_000 vs B 29_900 → B; S2/S3: tie → neither.
        Assert.Equal(0, duel.SectorWins.A1);
        Assert.Equal(1, duel.SectorWins.B1);
        Assert.Equal(0, duel.SectorWins.A2);
        Assert.Equal(0, duel.SectorWins.B2);
    }

    [Fact]
    public void Stints_pair_by_index_and_trace_duel_skips_without_traces()
    {
        var a = Summary(0, 90_000u, 90_000, 0) with
        {
            Stints =
            [
                new StintSummary(1, "C3", 0, 1, 5, 5, 90_000u, 90_500, 0, 0),
                new StintSummary(2, "C4", 5, 6, 9, 4, 91_000u, 91_500, 0, 0),
            ],
        };
        var b = Summary(1, 90_500u, 90_750, 0) with
        {
            Stints = [new StintSummary(1, "C4", 0, 1, 6, 6, 90_500u, 91_000, 0, 0)],
        };

        var duel = DriverDuel.Compare(a, b, lapsA: [], lapsB: [], null, null);

        Assert.Equal(2, duel.Stints.Count);
        Assert.Equal(1, duel.Stints[0].StintNumber);
        Assert.NotNull(duel.Stints[0].A);
        Assert.NotNull(duel.Stints[0].B);
        Assert.Equal(2, duel.Stints[1].StintNumber);
        Assert.Null(duel.Stints[1].B); // B has no second stint
        Assert.Empty(duel.TraceTips);  // no traces → duel skips the trace comparison
        Assert.Empty(duel.CornerLosses);
    }

    [Fact]
    public void Deltas_are_signed()
    {
        var duel = DriverDuel.Compare(
            Summary(0, 90_000u, 90_500.0, 120.0), Summary(1, 91_500u, 91_000.0, 150.0),
            [], [], null, null);

        Assert.Equal(-1_500.0, duel.BestDeltaMs, precision: 1);
        Assert.Equal(-500.0, duel.AverageDeltaMs, precision: 1);
        Assert.Equal(-30.0, duel.SigmaDeltaMs, precision: 1);
    }
}