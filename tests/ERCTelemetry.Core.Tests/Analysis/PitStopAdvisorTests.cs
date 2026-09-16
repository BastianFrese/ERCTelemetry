using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>PitStopAdvisor: pit-lap recommendation from the tyre-degradation curve, the tank
/// and the undercut window. Deterministic (Layer 1) — no LLM. The static Compute is pure;
/// the stateful Evaluate speaks on recommendation transitions, once per stint.</summary>
public sealed class PitStopAdvisorTests
{
    [Fact]
    public void Tyre_degradation_drives_pit_lap()
    {
        // 60 % wear, 3 %/lap → 13 laps left → box in lap 5 + 12 = 17.
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 60f, wearPerLap: 3f,
            fuelLapsRemaining: 30f, fuelPerLap: 2f, gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.PitInLap, advice.Recommendation);
        Assert.Equal(17, advice.SuggestedPitLap);
        Assert.Contains("Runde 17", advice.Reason);
    }

    [Fact]
    public void Fuel_emergency_overrides_tyres()
    {
        // 3 laps of fuel left, 25 to go → shortfall 22 → pit now, regardless of tyres.
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 20f, wearPerLap: 1f,
            fuelLapsRemaining: 3f, fuelPerLap: 2f, gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.PitNow, advice.Recommendation);
        Assert.Contains("Tank-Notfall", advice.Reason);
    }

    [Fact]
    public void Undercut_window_when_car_behind_close()
    {
        // 90 % wear, 5 %/lap → 2 laps left; car behind 0.8 s → undercut now.
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 90f, wearPerLap: 5f,
            fuelLapsRemaining: 30f, fuelPerLap: 2f, gapToCarBehindMs: 800);

        Assert.Equal(PitStopRecommendation.PitNow, advice.Recommendation);
        Assert.Contains("Undercut", advice.Reason);
    }

    [Fact]
    public void Pit_now_when_tyres_done()
    {
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 100f, wearPerLap: 5f,
            fuelLapsRemaining: 30f, fuelPerLap: 2f, gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.PitNow, advice.Recommendation);
        Assert.Contains("Reifen sind durch", advice.Reason);
    }

    [Fact]
    public void Pit_now_when_window_opens()
    {
        // 90 % wear, 5 %/lap → box in lap 6, one lap away → pit now.
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 90f, wearPerLap: 5f,
            fuelLapsRemaining: 30f, fuelPerLap: 2f, gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.PitNow, advice.Recommendation);
        Assert.Contains("Boxenfenster jetzt", advice.Reason);
    }

    [Fact]
    public void Keep_going_when_tyres_last_to_end()
    {
        // 10 % wear, 0.5 %/lap → 180 laps left → tyres outlast the race.
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 10f, wearPerLap: 0.5f,
            fuelLapsRemaining: 30f, fuelPerLap: 2f, gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.KeepGoing, advice.Recommendation);
        Assert.Contains("Reifen halten bis zum Ende", advice.Reason);
    }

    [Fact]
    public void Keep_going_without_wear_trend()
    {
        // Wear-per-lap below the noise floor → no usable trend.
        var advice = PitStopAdvisor.Compute(
            currentLap: 5, totalLaps: 30, wearNow: 60f, wearPerLap: 0.01f,
            fuelLapsRemaining: 30f, fuelPerLap: 2f, gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.KeepGoing, advice.Recommendation);
        Assert.Contains("Weiter fahren", advice.Reason);
    }

    [Fact]
    public void Evaluate_speaks_on_transition_only()
    {
        var advisor = new PitStopAdvisor();

        var first = advisor.Evaluate(Digest(wear: 60f, tyreAge: 10), gapToCarBehindMs: 0);
        var second = advisor.Evaluate(Digest(wear: 60f, tyreAge: 10), gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.PitInLap, first!.Recommendation);
        Assert.Null(second);
    }

    [Fact]
    public void Evaluate_rearms_on_compound_change()
    {
        var advisor = new PitStopAdvisor();
        advisor.Evaluate(Digest(wear: 60f, tyreAge: 10, compound: ActualCompound.F1C3), gapToCarBehindMs: 0);

        var rearmed = advisor.Evaluate(Digest(wear: 60f, tyreAge: 10, compound: ActualCompound.F1C2), gapToCarBehindMs: 0);

        Assert.Equal(PitStopRecommendation.PitInLap, rearmed!.Recommendation);
    }

    [Fact]
    public void Evaluate_escalates_to_pit_now()
    {
        var advisor = new PitStopAdvisor();
        advisor.Evaluate(Digest(wear: 60f, tyreAge: 10), gapToCarBehindMs: 0); // box in lap 10

        var escalated = advisor.Evaluate(Digest(wear: 90f, tyreAge: 10), gapToCarBehindMs: 0); // pit now

        Assert.Equal(PitStopRecommendation.PitNow, escalated!.Recommendation);
    }

    [Fact]
    public void Evaluate_ignores_keep_going()
    {
        var advisor = new PitStopAdvisor();

        var silent = advisor.Evaluate(Digest(wear: 10f, tyreAge: 10), gapToCarBehindMs: 0);
        var later = advisor.Evaluate(Digest(wear: 60f, tyreAge: 10), gapToCarBehindMs: 0);

        Assert.Null(silent);
        Assert.Equal(PitStopRecommendation.PitInLap, later!.Recommendation);
    }

    private static LiveAnalysisDigest Digest(
        byte lap = 5,
        byte totalLaps = 30,
        ActualCompound compound = ActualCompound.F1C3,
        byte tyreAge = 10,
        float wear = 60f,
        float fuelRemainingLaps = 30f,
        float fuelUsedLastLap = 2f) =>
        new(lap, totalLaps, 1, 0, [], 0, LapTrend.Stable, 0f, compound, tyreAge, wear,
            50f, fuelRemainingLaps, fuelUsedLastLap, 30f);
}
