using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Llm;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>LiveAnalysisTemplate (Layer 1): deterministic German sentences for each
/// trigger reason — the free baseline that works without an API key.</summary>
public sealed class LiveAnalysisTemplateTests
{
    private readonly LiveAnalysisTemplate _template = new();

    [Fact]
    public async Task Tyre_reason_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(Digest(wear: 60f), LiveAnalysisTriggerReason.TyreWear, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Reifen bei 60 Prozent Verschleiß", text);
        Assert.Contains("Boxenfenster", text);
    }

    [Fact]
    public async Task Fuel_reason_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(Digest(fuelUsedLastLap: 2.1f), LiveAnalysisTriggerReason.FuelShortfall, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("2.1 Liter pro Runde", text);
        Assert.Contains("Tank", text);
    }

    [Fact]
    public async Task Trend_reason_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(Digest(trend: LapTrend.Slower, delta: 400f), LiveAnalysisTriggerReason.TempoTrend, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("0.4 Sekunden pro Runde langsamer", text);
        Assert.Contains("Reifen", text);
    }

    [Fact]
    public async Task Status_reason_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(Digest(lap: 5, totalLaps: 30, position: 3, wear: 70f, fuelRemainingLaps: 8f), LiveAnalysisTriggerReason.StatusUpdate, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Runde 5 von 30", text);
        Assert.Contains("Position 3", text);
        Assert.Contains("Reifen bei 70 Prozent", text);
        Assert.Contains("Tank reicht für 8 Runden", text);
    }

    [Fact]
    public async Task Formatting_uses_invariant_decimal_separator()
    {
        var text = await _template.AnalyzeAsync(Digest(trend: LapTrend.Slower, delta: 400f), LiveAnalysisTriggerReason.TempoTrend, CancellationToken.None);

        Assert.Contains("0.4", text);
        Assert.DoesNotContain("0,4", text);
    }

    private static LiveAnalysisDigest Digest(
        byte lap = 5,
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
