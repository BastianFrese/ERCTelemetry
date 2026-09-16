using Xunit;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Recorder unit tests: distance-gated sampling (with reversal acceptance),
/// lap-change stash, unknown-lap handout, repeat-handout null.</summary>
public sealed class LapTraceRecorderTests
{
    [Fact]
    public void Samples_are_distance_gated_20m()
    {
        var recorder = new LapTraceRecorder();
        var lap = (byte)5;

        recorder.Sample(lap, 0f, 280);   // first sample always taken
        recorder.Sample(lap, 10f, 281);  // <20 m since last -> dropped
        recorder.Sample(lap, 15f, 282);  // <20 m - dropped
        recorder.Sample(lap, 20f, 283);  // >=20 m -> taken
        recorder.Sample(lap, 25f, 284);  // <20 m - dropped
        recorder.Sample(lap, 41f, 285);  // >=20 m -> taken
        recorder.Sample(lap, 61f, 286);  // >=20 m -> taken
        recorder.Sample(lap, 62f, 287);  // <20 m - dropped

        var trace = recorder.TakeTrace(5);
        Assert.NotNull(trace);
        Assert.Equal(4, trace!.Count); // 0, 20, 41, 61 m
        Assert.Equal(0f, trace[0].LapDistance);
        Assert.Equal(283, trace[1].Speed); // the 10/15 m samples were dropped
        Assert.Equal(20f, trace[1].LapDistance);
        Assert.Equal(285, trace[2].Speed);
        Assert.Equal(41f, trace[2].LapDistance);
        Assert.Equal(286, trace[3].Speed);
        Assert.Equal(61f, trace[3].LapDistance);
    }

    [Fact]
    public void Reversal_samples_are_taken_and_reset_the_gap_measure()
    {
        var recorder = new LapTraceRecorder();
        var lap = (byte)5;

        recorder.Sample(lap, 0f, 280);  // first sample always taken
        recorder.Sample(lap, 20f, 283); // accepted (gap 20)
        recorder.Sample(lap, 15f, 270); // d < last -> taken, gap measure resets to 15 m
        recorder.Sample(lap, 25f, 275); // 10 m from last accepted (15 m) -> dropped
        recorder.Sample(lap, 40f, 278); // 25 m from last accepted -> taken
        recorder.Sample(lap, 45f, 279); // <20 m - dropped

        var trace = recorder.TakeTrace(5);
        Assert.NotNull(trace);
        Assert.Equal(4, trace!.Count); // 0, 20, 15, 40 m (non-monotone by design)
        Assert.Equal(270, trace[2].Speed);
        Assert.Equal(15f, trace[2].LapDistance);
        Assert.Equal(278, trace[3].Speed);
        Assert.Equal(40f, trace[3].LapDistance);
    }

    [Fact]
    public void Lap_change_stashes_previous_lap()
    {
        var recorder = new LapTraceRecorder();
        recorder.Sample(5, 0f, 280);
        recorder.Sample(5, 20f, 275);
        recorder.Sample(6, 0f, 300); // lap change -> lap 5's buffer moves to prev
        recorder.Sample(6, 20f, 283);

        var trace5 = recorder.TakeTrace(5);
        Assert.NotNull(trace5);
        Assert.Equal(2, trace5!.Count);
        Assert.Equal(280, trace5[0].Speed);

        var trace6 = recorder.TakeTrace(6);
        Assert.NotNull(trace6);
        Assert.Equal(2, trace6!.Count);
        Assert.Equal(300, trace6[0].Speed);
    }

    [Fact]
    public void TakeTrace_of_unknown_lap_returns_null_and_keeps_buffers()
    {
        var recorder = new LapTraceRecorder();
        recorder.Sample(5, 0f, 280);
        recorder.Sample(5, 20f, 283);

        var trace9 = recorder.TakeTrace(9);
        Assert.Null(trace9);

        // buffers intact after a miss
        var trace5 = recorder.TakeTrace(5);
        Assert.NotNull(trace5);
        Assert.Equal(2, trace5!.Count);
    }

    [Fact]
    public void TakeTrace_clears_buffers_so_repeat_handout_is_null()
    {
        var recorder = new LapTraceRecorder();
        recorder.Sample(5, 0f, 280);
        recorder.Sample(5, 20f, 283);

        var first = recorder.TakeTrace(5);
        Assert.NotNull(first);
        Assert.Equal(2, first!.Count);

        var second = recorder.TakeTrace(5);
        Assert.Null(second);
    }
}
