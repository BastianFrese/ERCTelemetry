using Xunit;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Tests.Analysis;

public sealed class StrategyAdvisorTests
{
    [Fact]
    public void Computes_tyre_laps_left_and_pit_lap()
    {
        var advice = StrategyAdvisor.Compute(
            bestLapMs: 91_000, fuelInTank: 40f, fuelPerLap: 2.2f,
            wearNow: 60f, wearPerLap: 2.5f, currentLap: 10);

        Assert.Equal((100 - 60) / 2.5, advice.TyreLapsLeft); // 16 laps left
        Assert.Equal((ushort)25, advice.SuggestedPitLap);    // 10 + 16 − 1
        Assert.Equal(92_400u, advice.TargetLapMs);           // 91 s + 40 l × 35 ms
    }

    [Fact]
    public void Unknown_wear_trend_yields_no_pit_advice()
    {
        var advice = StrategyAdvisor.Compute(
            bestLapMs: 91_000, fuelInTank: 40f, fuelPerLap: 2.2f,
            wearNow: 60f, wearPerLap: 0f, currentLap: 10);

        Assert.Equal(-1, advice.TyreLapsLeft);
        Assert.Equal(0, advice.SuggestedPitLap);
        Assert.Equal(92_400u, advice.TargetLapMs); // target works without wear trend
    }
}