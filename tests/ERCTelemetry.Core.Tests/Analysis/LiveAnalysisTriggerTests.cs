using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>LiveAnalysisTrigger: WHEN the live analysis speaks. Stateful (single consumer):
/// each warning fires once per stint (reset on compound change), the tempo trend re-arms
/// when the slowdown ends, and a status update runs every 5 laps.</summary>
public sealed class LiveAnalysisTriggerTests
{
    [Fact]
    public void Tyre_wear_fires_once_per_stint()
    {
        var trigger = new LiveAnalysisTrigger();

        var first = trigger.Evaluate(Digest(wear: 70f));
        var second = trigger.Evaluate(Digest(wear: 75f));

        Assert.Equal(LiveAnalysisTriggerReason.TyreWear, first);
        Assert.Null(second);
    }

    [Fact]
    public void Tyre_wear_rearms_on_compound_change()
    {
        var trigger = new LiveAnalysisTrigger();
        trigger.Evaluate(Digest(wear: 70f, compound: ActualCompound.F1C3));

        var rearmed = trigger.Evaluate(Digest(wear: 70f, compound: ActualCompound.F1C2));

        Assert.Equal(LiveAnalysisTriggerReason.TyreWear, rearmed);
    }

    [Fact]
    public void Tyre_wear_not_below_threshold()
    {
        var trigger = new LiveAnalysisTrigger();

        var result = trigger.Evaluate(Digest(wear: 59f));

        Assert.Null(result);
    }

    [Fact]
    public void Fuel_shortfall_fires_once_per_stint()
    {
        var trigger = new LiveAnalysisTrigger();

        var first = trigger.Evaluate(Digest(projected: -10f));
        var second = trigger.Evaluate(Digest(projected: -15f));

        Assert.Equal(LiveAnalysisTriggerReason.FuelShortfall, first);
        Assert.Null(second);
    }

    [Fact]
    public void Fuel_shortfall_rearms_on_compound_change()
    {
        var trigger = new LiveAnalysisTrigger();
        trigger.Evaluate(Digest(projected: -10f, compound: ActualCompound.F1C3));

        var rearmed = trigger.Evaluate(Digest(projected: -10f, compound: ActualCompound.F1C2));

        Assert.Equal(LiveAnalysisTriggerReason.FuelShortfall, rearmed);
    }

    [Fact]
    public void Fuel_shortfall_not_when_tank_sufficient()
    {
        var trigger = new LiveAnalysisTrigger();

        var result = trigger.Evaluate(Digest(projected: 5f));

        Assert.Null(result);
    }

    [Fact]
    public void Tempo_trend_fires_on_slower_switch()
    {
        var trigger = new LiveAnalysisTrigger();

        var first = trigger.Evaluate(Digest(trend: LapTrend.Slower, delta: 400f));
        var second = trigger.Evaluate(Digest(trend: LapTrend.Slower, delta: 500f));

        Assert.Equal(LiveAnalysisTriggerReason.TempoTrend, first);
        Assert.Null(second);
    }

    [Fact]
    public void Tempo_trend_rearms_when_slowdown_ends()
    {
        var trigger = new LiveAnalysisTrigger();
        trigger.Evaluate(Digest(trend: LapTrend.Slower, delta: 400f));
        trigger.Evaluate(Digest(trend: LapTrend.Stable, delta: 0f)); // slowdown over — re-arm

        var rearmed = trigger.Evaluate(Digest(trend: LapTrend.Slower, delta: 400f));

        Assert.Equal(LiveAnalysisTriggerReason.TempoTrend, rearmed);
    }

    [Fact]
    public void Tempo_trend_not_below_threshold()
    {
        var trigger = new LiveAnalysisTrigger();

        var result = trigger.Evaluate(Digest(trend: LapTrend.Slower, delta: 200f));

        Assert.Null(result);
    }

    [Fact]
    public void Status_update_every_five_laps()
    {
        var trigger = new LiveAnalysisTrigger();

        var lap5 = trigger.Evaluate(Digest(lap: 5));
        var lap6 = trigger.Evaluate(Digest(lap: 6));
        var lap10 = trigger.Evaluate(Digest(lap: 10));

        Assert.Equal(LiveAnalysisTriggerReason.StatusUpdate, lap5);
        Assert.Null(lap6);
        Assert.Equal(LiveAnalysisTriggerReason.StatusUpdate, lap10);
    }

    [Fact]
    public void No_trigger_when_unremarkable()
    {
        var trigger = new LiveAnalysisTrigger();

        var result = trigger.Evaluate(Digest(lap: 2, wear: 20f, projected: 30f, trend: LapTrend.Stable));

        Assert.Null(result);
    }

    [Fact]
    public void Reset_clears_all_warnings()
    {
        var trigger = new LiveAnalysisTrigger();
        trigger.Evaluate(Digest(wear: 70f));
        trigger.Reset();

        var result = trigger.Evaluate(Digest(wear: 70f));

        Assert.Equal(LiveAnalysisTriggerReason.TyreWear, result);
    }

    private static LiveAnalysisDigest Digest(
        byte lap = 2, // below the 5-lap status interval so unrelated tests stay silent
        byte totalLaps = 30,
        byte position = 1,
        int gap = 0,
        LapTrend trend = LapTrend.Stable,
        float delta = 0f,
        ActualCompound compound = ActualCompound.F1C3,
        byte tyreAge = 1,
        float wear = 20f,
        float fuelInTank = 50f,
        float fuelRemainingLaps = 20f,
        float fuelUsedLastLap = 2f,
        float projected = 30f) =>
        new(lap, totalLaps, position, gap, [], 0, trend, delta, compound, tyreAge, wear,
            fuelInTank, fuelRemainingLaps, fuelUsedLastLap, projected);
}
