using ERCTelemetry.Core.Analysis;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>PitWindowCalculator: the live undercut/overcut question — "if I pit now, do I
/// come out ahead of the car in front?" — plus how long the undercut window stays open.</summary>
public sealed class PitWindowCalculatorTests
{
    private const int PitLane = 20_000;
    private const int InOut = 1_000;   // pit loss = 21 000 ms
    private const int TyreDelta = 1_500; // 1.5 s/lap fresh-tyre advantage
    private const int Laps = 15;         // 15 × 1500 = 22 500 ms total gain

    [Fact]
    public void Undercut_works_when_gain_covers_gap_plus_pit_loss()
    {
        // gap 1000 + pit loss 21000 = 22000 < 22500 total gain
        var advice = PitWindowCalculator.Compute(1_000, PitLane, InOut, TyreDelta, Laps);

        Assert.True(advice.UndercutWorks);
        Assert.Equal(21_000, advice.PitLossMs);
        Assert.Equal(1_500, advice.TyreDeltaMsPerLap);
    }

    [Fact]
    public void Undercut_fails_when_gap_is_too_large()
    {
        // gap 2000 + pit loss 21000 = 23000 > 22500 total gain
        var advice = PitWindowCalculator.Compute(2_000, PitLane, InOut, TyreDelta, Laps);

        Assert.False(advice.UndercutWorks);
        Assert.Null(advice.UndercutWindowLaps);
    }

    [Fact]
    public void Undercut_fails_without_tyre_delta_or_horizon()
    {
        Assert.False(PitWindowCalculator.Compute(0, PitLane, InOut, 0, Laps).UndercutWorks);
        Assert.False(PitWindowCalculator.Compute(0, PitLane, InOut, TyreDelta, 0).UndercutWorks);
    }

    [Fact]
    public void Window_stays_open_until_ahead_pits_when_gap_is_stable()
    {
        // margin = 22500 − 0 − 21000 = 1500; each lap waited costs 1500 → 1 lap left
        var advice = PitWindowCalculator.Compute(0, PitLane, InOut, TyreDelta, Laps);

        Assert.Equal(1, advice.UndercutWindowLaps);
    }

    [Fact]
    public void Window_closes_immediately_when_gap_grows_fast()
    {
        // gap grows 500 ms/lap → denom 2000, margin 1500 → floor(1500/2000) = 0
        var advice = PitWindowCalculator.Compute(0, PitLane, InOut, TyreDelta, Laps, gapChangeMsPerLap: 500);

        Assert.Equal(0, advice.UndercutWindowLaps);
    }

    [Fact]
    public void Window_never_closes_when_gap_shrinks_faster_than_tyre_delta()
    {
        // gap shrinks 2000 ms/lap → denom ≤ 0 → you can wait until the car ahead pits
        var advice = PitWindowCalculator.Compute(0, PitLane, InOut, TyreDelta, Laps, gapChangeMsPerLap: -2_000);

        Assert.Equal(Laps, advice.UndercutWindowLaps);
    }

    [Fact]
    public void Overcut_works_when_pit_loss_exceeds_tyre_gain()
    {
        // pit loss 21000 > 1000 × 15 = 15000
        var advice = PitWindowCalculator.Compute(0, PitLane, InOut, 1_000, Laps);

        Assert.True(advice.OvercutWorks);
    }

    [Fact]
    public void Overcut_fails_when_tyre_gain_dominates()
    {
        // pit loss 21000 < 1500 × 15 = 22500
        var advice = PitWindowCalculator.Compute(0, PitLane, InOut, TyreDelta, Laps);

        Assert.False(advice.OvercutWorks);
    }
}
