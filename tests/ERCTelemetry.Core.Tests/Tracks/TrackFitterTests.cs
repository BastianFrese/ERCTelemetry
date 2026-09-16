using System.Text.Json;
using ERCTelemetry.Core.OverlayProtocol;
using ERCTelemetry.Core.Tracks;
using Xunit;

namespace ERCTelemetry.Core.Tests.Tracks;

/// <summary>TrackFitter recovery on a synthetic chiral circuit (ellipse plus one bump,
/// so the mirror image is geometrically distinct and a winning mirror hypothesis is
/// detectable), plus catalog lookups and the tracklayout wire-message roundtrip.</summary>
public class TrackFitterTests
{
    private const double Theta = 37 * Math.PI / 180; // recovery rotation
    private const float Tx = 1000;
    private const float Tz = 2000;

    /// <summary>Closed, chiral ring: an ellipse with two outward bumps of different
    /// size on opposite sides. Together the bumps span ~45% of the ring arc — more
    /// than the fitter's 25% ICP trim can discard — so the mirror image is
    /// geometrically distinct and a winning mirror hypothesis is detectable.</summary>
    private static TrackLayout TestLayout()
    {
        const int points = 240;
        var xs = new float[points];
        var ys = new float[points];
        for (var i = 0; i < points; i++)
        {
            var t = 2 * Math.PI * i / points;
            var bump = 1
                + 0.4 * Math.Exp(-Math.Pow((t - Math.PI) / 0.35, 2))
                + 0.25 * Math.Exp(-Math.Pow((t - 0.7) / 0.45, 2));
            xs[i] = (float)(600 * bump * Math.Cos(t));
            ys[i] = (float)(350 * bump * Math.Sin(t));
        }

        var length = 0f;
        for (var i = 0; i < points; i++)
        {
            var dx = xs[(i + 1) % points] - xs[i];
            var dy = ys[(i + 1) % points] - ys[i];
            length += MathF.Sqrt(dx * dx + dy * dy);
        }

        return new TrackLayout(xs, ys, length);
    }

    /// <summary>World position of one layout point under the test transform: optional
    /// y-mirror first, then rotation by <see cref="Theta"/> and translation.</summary>
    private static (float X, float Z) WorldOf(float lx, float ly, bool mirrorY)
    {
        if (mirrorY)
        {
            ly = -ly;
        }

        var cos = (float)Math.Cos(Theta);
        var sin = (float)Math.Sin(Theta);
        return (cos * lx - sin * ly + Tx, sin * lx + cos * ly + Tz);
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Walks the ring once (~4 m steps: 3 samples per layout segment), feeding
    /// each point through the test transform with Gaussian noise. Phase shifts the
    /// sampling between laps so the second lap still creates new trail cells and the
    /// fit attempt (250 ms cooldown, 30 new cells) actually fires.</summary>
    private static void FeedLap(
        TrackFitter fitter, TrackLayout layout, bool mirrorY, float sigma, double phase, Random rng,
        float worldScale = 1f)
    {
        var cos = (float)Math.Cos(Theta);
        var sin = (float)Math.Sin(Theta);
        const int samplesPerSegment = 3;
        for (var i = 0; i < layout.PointCount; i++)
        {
            var j = (i + 1) % layout.PointCount;
            for (var s = 0; s < samplesPerSegment; s++)
            {
                var t = (float)((s + phase) / samplesPerSegment);
                var lx = layout.X[i] + t * (layout.X[j] - layout.X[i]);
                var ly = layout.Y[i] + t * (layout.Y[j] - layout.Y[i]);
                if (mirrorY)
                {
                    ly = -ly;
                }

                var wx = worldScale * (cos * lx - sin * ly + Tx) + (float)(Gaussian(rng) * sigma);
                var wz = worldScale * (sin * lx + cos * ly + Tz) + (float)(Gaussian(rng) * sigma);
                fitter.Feed(wx, wz);
            }
        }
    }

    private static void AssertRecovered(TrackFitter fitter, TrackLayout layout, bool mirrorY)
    {
        Assert.True(fitter.IsLocked);
        var fit = fitter.Fit!;
        Assert.InRange(fit.Scale, 0.9f, 1.1f);
        foreach (var k in new[] { 0, 60, 120, 180 })
        {
            var (wx, wz) = WorldOf(layout.X[k], layout.Y[k], mirrorY);
            var (rx, rz) = fit.ToWorld(layout.X[k], layout.Y[k]);
            var err = Math.Sqrt((rx - wx) * (rx - wx) + (rz - wz) * (rz - wz));
            Assert.True(err < 10, $"layout point {k}: recovered {err:F1} m off (mirror={mirrorY})");
        }
    }

    [Fact]
    public void Recovers_rotation_translation_and_mild_noise()
    {
        var layout = TestLayout();
        var fitter = new TrackFitter(layout);
        var rng = new Random(20260908);

        FeedLap(fitter, layout, mirrorY: false, sigma: 2f, phase: 0.0, rng);
        Assert.False(fitter.IsLocked); // attempt cooldown: no lock before 250 ms of feeding

        Thread.Sleep(350); // let the re-attempt timer elapse, then feed fresh cells
        FeedLap(fitter, layout, mirrorY: false, sigma: 2f, phase: 0.5, rng);
        AssertRecovered(fitter, layout, mirrorY: false);
    }

    [Fact]
    public void Recovers_mirrored_world_orientation()
    {
        var layout = TestLayout();
        var fitter = new TrackFitter(layout);
        var rng = new Random(4711);

        FeedLap(fitter, layout, mirrorY: true, sigma: 2f, phase: 0.0, rng);
        Thread.Sleep(350);
        FeedLap(fitter, layout, mirrorY: true, sigma: 2f, phase: 0.5, rng);

        Assert.True(fitter.IsLocked);
        Assert.True(fitter.Fit!.Mirrored, "the mirror hypothesis must win for mirrored world data");
        AssertRecovered(fitter, layout, mirrorY: true);
    }

    [Fact]
    public void Rejects_world_data_at_wrong_scale()
    {
        // 3× scaled world data: ICP may converge, but the scale gate must refuse to lock.
        var layout = TestLayout();
        var fitter = new TrackFitter(layout);
        var rng = new Random(99);

        FeedLap(fitter, layout, mirrorY: false, sigma: 2f, phase: 0.0, rng, worldScale: 3f);
        Thread.Sleep(350);
        FeedLap(fitter, layout, mirrorY: false, sigma: 2f, phase: 0.5, rng, worldScale: 3f);

        Assert.False(fitter.IsLocked);
    }

    [Fact]
    public void Locks_despite_pit_and_grid_outliers()
    {
        var layout = TestLayout();
        var fitter = new TrackFitter(layout);
        var rng = new Random(555);

        FeedLap(fitter, layout, mirrorY: false, sigma: 2f, phase: 0.0, rng);
        // Pit lane: a tight cluster offset from one ring point…
        var (px, py) = (layout.X[30], layout.Y[30]);
        for (var i = 0; i < 25; i++)
        {
            fitter.Feed(px + 15 + i * 0.2f, py + 10);
        }

        // …and the starting grid: cars queued along a line near the ring.
        var (gx, gy) = (layout.X[200], layout.Y[200]);
        for (var i = 0; i < 20; i++)
        {
            fitter.Feed(gx + i * 8f, gy + i * 0.5f);
        }

        Thread.Sleep(350);
        FeedLap(fitter, layout, mirrorY: false, sigma: 2f, phase: 0.5, rng);
        AssertRecovered(fitter, layout, mirrorY: false);
    }

    [Fact]
    public void Does_not_lock_on_insufficient_data()
    {
        var layout = TestLayout();
        var fitter = new TrackFitter(layout);
        var rng = new Random(7);

        for (var i = 0; i < 100; i++)
        {
            var t = 2 * Math.PI * i / 100;
            fitter.Feed((float)(600 * Math.Cos(t)) + Tx, (float)(350 * Math.Sin(t)) + Tz);
        }

        Thread.Sleep(350);
        for (var i = 0; i < 10; i++)
        {
            fitter.Feed(Tx + i * 3f, Tz);
        }

        // ~110 trail cells < MinCells (120): no attempt, no exception, no lock.
        Assert.False(fitter.IsLocked);
    }

    [Fact]
    public void StartFinish_is_median_of_three_crossings()
    {
        var fitter = new TrackFitter(TestLayout());

        Assert.Null(fitter.StartFinishWorld);
        fitter.MarkStartFinishCrossing(0, 100);
        fitter.MarkStartFinishCrossing(10, 90);
        Assert.Null(fitter.StartFinishWorld);
        fitter.MarkStartFinishCrossing(20, 110);

        var sf = fitter.StartFinishWorld!.Value;
        Assert.Equal(10, sf.X);
        Assert.Equal(100, sf.Z);

        fitter.MarkStartFinishCrossing(999, 999); // frozen once the median is set
        Assert.Equal(10, fitter.StartFinishWorld!.Value.X);
    }

    [Fact]
    public void Catalog_lookups_strip_prefix_and_match_aliases()
    {
        Assert.True(TrackLayoutCatalog.TryGet("Bahrain", out var bahrain));
        Assert.True(bahrain.PointCount > 10);
        Assert.True(bahrain.LengthMetres > 1000);

        Assert.True(TrackLayoutCatalog.TryGet("F1_Bahrain", out var prefixed));
        Assert.Same(bahrain, prefixed); // prefix-stripped name resolves the same entry

        Assert.True(TrackLayoutCatalog.TryGet("monaco", out _));
        Assert.True(TrackLayoutCatalog.TryGet("Monte_Carlo", out var alias));
        Assert.True(alias.PointCount > 10);

        Assert.False(TrackLayoutCatalog.TryGet("NotARealTrack", out _));
        Assert.False(TrackLayoutCatalog.TryGet(null, out _));
        Assert.False(TrackLayoutCatalog.TryGet("", out _));
    }

    [Fact]
    public void Catalog_resolves_game_enum_names_for_mismatched_circuits()
    {
        // F1Game.UDP 26 sends its own Track enum names, which differ from the
        // bacinger/f1-circuits catalog keys for several circuits.
        Assert.True(TrackLayoutCatalog.TryGet("Austria", out var austria));
        Assert.True(austria.PointCount > 10);
        Assert.True(TrackLayoutCatalog.TryGet("F1_Austria", out var prefixed));
        Assert.Same(austria, prefixed);

        Assert.True(TrackLayoutCatalog.TryGet("AbuDhabi", out _));
        Assert.True(TrackLayoutCatalog.TryGet("Texas", out _));
        Assert.True(TrackLayoutCatalog.TryGet("Brazil", out _));
        Assert.True(TrackLayoutCatalog.TryGet("Mexico", out _));
        Assert.True(TrackLayoutCatalog.TryGet("Azerbaijan", out _));
        Assert.True(TrackLayoutCatalog.TryGet("Madrid", out _));

        // Reverse variants reuse the same circuit layout.
        Assert.True(TrackLayoutCatalog.TryGet("AustriaReverse", out var reverse));
        Assert.Same(austria, reverse);
        Assert.True(TrackLayoutCatalog.TryGet("SilverstoneReverse", out _));
        Assert.True(TrackLayoutCatalog.TryGet("ZandvoortReverse", out _));
    }

    [Fact]
    public void TrackLayout_wire_message_roundtrips()
    {
        var layout = TestLayout();
        var fit = new TrackFitResult(
            Scale: 1.5f, Cos: 0.8f, Sin: 0.6f, Tx: 42f, Ty: -7f, RmsMetres: 3f, Coverage: 0.9f);
        var message = OverlayMessageFactory.BuildTrackLayout("F1_Bahrain", 1234, layout, fit, (5f, 7f));

        Assert.NotNull(message);
        var root = JsonDocument.Parse(OverlayMessageFactory.CreateTrackLayout(message!)).RootElement;

        Assert.Equal("tracklayout", root.GetProperty("t").GetString());
        Assert.Equal("F1_Bahrain", root.GetProperty("track").GetString());
        Assert.Equal(1234, root.GetProperty("sessionUid").GetInt64());
        Assert.Equal(layout.PointCount, root.GetProperty("points").GetArrayLength());

        var (expectedX, expectedZ) = fit.ToWorld(layout.X[0], layout.Y[0]);
        var first = root.GetProperty("points")[0];
        Assert.Equal(expectedX, first[0].GetSingle(), 2);
        Assert.Equal(expectedZ, first[1].GetSingle(), 2);

        Assert.Equal(5f, root.GetProperty("startX").GetSingle());
        Assert.Equal(7f, root.GetProperty("startZ").GetSingle());
    }

    [Fact]
    public void TrackLayout_message_omits_absent_start_finish()
    {
        var layout = TestLayout();
        var fit = new TrackFitResult(Scale: 1f, Cos: 1f, Sin: 0f, Tx: 0f, Ty: 0f, RmsMetres: 3f, Coverage: 0.9f);
        var message = OverlayMessageFactory.BuildTrackLayout("Spa", 1, layout, fit, null);

        Assert.NotNull(message);
        var root = JsonDocument.Parse(OverlayMessageFactory.CreateTrackLayout(message!)).RootElement;

        Assert.False(root.TryGetProperty("startX", out _));
        Assert.False(root.TryGetProperty("startZ", out _));
    }
}