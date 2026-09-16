using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Llm;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>RivalTemplate (Layer 1): deterministic German sentences for each notable rival
/// action — the free baseline that works without an API key.</summary>
public sealed class RivalTemplateTests
{
    private readonly RivalTemplate _template = new();

    [Fact]
    public async Task Pit_stop_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(
            Digest(ev: RivalEvent.PitStop, lap: 12, compound: ActualCompound.F1C2), CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Bob", text);
        Assert.Contains("Runde 12", text);
        Assert.Contains("Medium", text);
    }

    [Fact]
    public async Task Tyre_change_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(
            Digest(ev: RivalEvent.TyreChange, compound: ActualCompound.F1C3), CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Bob", text);
        Assert.Contains("Weich", text);
    }

    [Fact]
    public async Task Fast_lap_returns_german_sentence()
    {
        var text = await _template.AnalyzeAsync(
            Digest(ev: RivalEvent.FastLap), CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Bob", text);
        Assert.Contains("schnelle Runde", text);
    }

    [Fact]
    public async Task Classic_compound_has_german_name()
    {
        var text = await _template.AnalyzeAsync(
            Digest(ev: RivalEvent.TyreChange, compound: ActualCompound.F1ClassicDry), CancellationToken.None);

        Assert.Contains("Trockenreifen", text);
    }

    private static RivalDigest Digest(
        string name = "Bob",
        RivalEvent ev = RivalEvent.PitStop,
        byte lap = 12,
        ActualCompound compound = ActualCompound.F1C2,
        byte tyreAge = 1,
        uint lastLapMs = 90_000,
        uint bestLapMs = 88_000,
        int gap = 5_000,
        float trend = 0f) =>
        new(name, ev, lap, compound, tyreAge, lastLapMs, bestLapMs, gap, trend);
}
