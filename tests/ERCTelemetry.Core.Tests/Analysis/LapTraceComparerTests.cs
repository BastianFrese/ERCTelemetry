using Xunit;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Comparer unit tests on synthetic traces: time-gap extremes (loss/gain),
/// brake-zone matching (brake point + apex tips), sparse-input guards.</summary>
public sealed class LapTraceComparerTests
{
    /// <summary>Builds a trace from constant-speed runs: each segment is (speed, samples).</summary>
    private static LapTrace Build(byte lap, ushort trackLength, params (ushort Speed, int Count)[] segments)
    {
        var samples = new List<LapTraceSample>();
        float d = 0;
        foreach (var (speed, count) in segments)
        {
            for (var i = 0; i < count; i++)
            {
                samples.Add(new LapTraceSample(d, speed));
                d += 20;
            }
        }

        return new LapTrace(lap, 0, trackLength, samples);
    }

    [Fact]
    public void Compare_finds_loss_and_gain_extremes()
    {
        // Lap 7: 200 km/h, slow section 1980-2160 m at 150, fast 2160-2460 m at 300.
        // Lap 9: constant 200 km/h. Track 5400 m.
        var a = Build(7, 5400, (200, 100), (150, 10), (300, 15), (200, 145));
        var b = Build(9, 5400, (200, 270));

        var tips = LapTraceComparer.Compare(a, b);

        Assert.Contains(tips, t => t.Kind == "loss" && t.Text.Contains("Runde 7"));
        Assert.Contains(tips, t => t.Kind == "loss" && t.Text.Contains("ggü. Runde 9"));
        Assert.Contains(tips, t => t.Kind == "gain" && t.Text.Contains("gewinnt"));
    }

    [Fact]
    public void Compare_matches_brake_zones_and_reports_offsets()
    {
        // Lap 3 brakes at 1000 m down to 85 km/h, lap 5 brakes at 940 m down to 90 km/h.
        var a = Build(3, 3000, (250, 51), (200, 1), (150, 1), (85, 1), (150, 1), (200, 1), (250, 1), (250, 94));
        var b = Build(5, 3000, (250, 49), (200, 1), (150, 1), (90, 1), (150, 1), (200, 1), (250, 1), (250, 96));

        var tips = LapTraceComparer.Compare(a, b);

        Assert.Contains(tips, t => t.Kind == "brake" && t.Text.Contains("40 m später"));
        Assert.Contains(tips, t => t.Kind == "apex" && t.Text.Contains("5 km/h langsamer"));
    }

    [Fact]
    public void Compare_returns_empty_for_too_few_samples()
    {
        var a = Build(1, 200, (200, 5));
        var b = Build(2, 200, (200, 5));

        Assert.Empty(LapTraceComparer.Compare(a, b));
    }

    [Fact]
    public void Compare_returns_empty_for_identical_traces()
    {
        var a = Build(7, 3000, (250, 51), (200, 1), (150, 1), (100, 1), (150, 1), (200, 1), (250, 1), (250, 94));
        var b = Build(9, 3000, (250, 51), (200, 1), (150, 1), (100, 1), (150, 1), (200, 1), (250, 1), (250, 94));

        Assert.Empty(LapTraceComparer.Compare(a, b));
    }

    // ---------- Timekiller (RankCornerLosses) ----------

[Fact]
public void RankCornerLosses_reports_corner_where_lap_lost_time()
// Reference lap B brakes twice (onsets 1000 m + 2920 m); lap A is 20 km/h slower
// through the first corner's plateau -> only corner 1 shows a loss (~8.4 s).
{
    var b = Build(3, 3000,
        (250, 51), (200, 1), (100, 1), (100, 47),
        (200, 1), (250, 1), (250, 45), (200, 1), (100, 1), (100, 2));
    var a = Build(7, 3000,
        (250, 51), (200, 1), (100, 1), (80, 1), (80, 46),
        (200, 1), (250, 1), (250, 45), (200, 1), (100, 1), (100, 2));

    var losses = LapTraceComparer.RankCornerLosses(a, b);

    var corner = Assert.Single(losses);
    Assert.Equal(1, corner.CornerNumber);
    Assert.InRange(corner.LossSeconds, 8.0f, 9.0f);
}

[Fact]
public void RankCornerLosses_sorts_by_magnitude_and_numbers_corners()
// Lap A loses in both corners: corner 1 slightly (90 vs 100 km/h plateau, ~3.7 s),
// corner 2 more (60 vs 100 in the final brake window, ~1.0 s) — wait, reversed:
// corner 1's loss is the larger one, so corner 1 must rank first despite the
// corner-2 loss also passing the threshold.
{
    var b = Build(3, 3000,
        (250, 51), (200, 1), (100, 1), (100, 47),
        (200, 1), (250, 1), (250, 45), (200, 1), (100, 1), (100, 2));
    var a = Build(7, 3000,
        (250, 51), (200, 1), (100, 1), (90, 1), (90, 46),
        (200, 1), (250, 1), (250, 45), (200, 1), (60, 1), (60, 2));

    var losses = LapTraceComparer.RankCornerLosses(a, b);

    Assert.Equal(2, losses.Count);
    Assert.Equal(1, losses[0].CornerNumber);
    Assert.InRange(losses[0].LossSeconds, 3.0f, 4.5f);
    Assert.Equal(2, losses[1].CornerNumber);
    Assert.InRange(losses[1].LossSeconds, 0.7f, 1.4f);
}

[Fact]
public void RankCornerLosses_returns_empty_without_brake_zones_or_overlap()
{
    var constant = Build(3, 3000, (200, 151));
    var normal = Build(7, 3000,
        (250, 51), (200, 1), (100, 1), (100, 47),
        (200, 1), (250, 1), (250, 45), (200, 1), (100, 1), (100, 2));

    Assert.Empty(LapTraceComparer.RankCornerLosses(constant, normal)); // no zones on ref
    Assert.Empty(LapTraceComparer.RankCornerLosses(normal, Build(9, 3000, (200, 20))));
}
}
