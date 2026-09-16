using System.Collections.Concurrent;
using System.Threading;
using ERCTelemetry.Core.Clips;
using Xunit;

namespace ERCTelemetry.Core.Tests.Clips;

/// <summary>AudioRingBuffer: slices float PCM audio out of a rolling buffer by
/// wall-clock time, so a clip's audio can be aligned to its video frames.</summary>
public class AudioRingBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Take_returns_null_when_empty()
    {
        var buffer = new AudioRingBuffer(sampleRate: 48000, channels: 2, capacitySeconds: 20);

        Assert.Null(buffer.Take(T0, T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Take_slices_the_overlapping_window()
    {
        var buffer = new AudioRingBuffer(48000, 2, 20);
        var chunk = new float[96000]; // 1 second of interleaved stereo
        for (var i = 0; i < chunk.Length; i++)
        {
            chunk[i] = i;
        }

        buffer.Add(T0, chunk);

        // [T0 + 0.25s, T0 + 0.75s) → 0.5s = 48000 samples, starting at index 24000.
        var slice = buffer.Take(T0 + TimeSpan.FromSeconds(0.25), T0 + TimeSpan.FromSeconds(0.75));

        Assert.NotNull(slice);
        Assert.Equal(48000, slice!.Length);
        Assert.Equal(24000f, slice[0]);
        Assert.Equal(71999f, slice[^1]);
    }

    [Fact]
    public void Take_returns_null_when_window_is_before_all_audio()
    {
        var buffer = new AudioRingBuffer(48000, 2, 20);
        buffer.Add(T0, new float[48000]);

        Assert.Null(buffer.Take(T0 - TimeSpan.FromSeconds(5), T0 - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Take_returns_null_when_window_is_after_all_audio()
    {
        var buffer = new AudioRingBuffer(48000, 2, 20);
        buffer.Add(T0, new float[48000]);

        Assert.Null(buffer.Take(T0 + TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void Take_returns_null_for_an_empty_window()
    {
        var buffer = new AudioRingBuffer(48000, 2, 20);
        buffer.Add(T0, new float[48000]);

        Assert.Null(buffer.Take(T0, T0));
    }

    [Fact]
    public void Take_spans_multiple_chunks()
    {
        var buffer = new AudioRingBuffer(48000, 2, 20);
        buffer.Add(T0, new float[48000]);                              // [0, 0.5)
        buffer.Add(T0 + TimeSpan.FromSeconds(0.5), new float[48000]);  // [0.5, 1.0)
        buffer.Add(T0 + TimeSpan.FromSeconds(1.0), new float[48000]); // [1.0, 1.5)

        var slice = buffer.Take(T0 + TimeSpan.FromSeconds(0.25), T0 + TimeSpan.FromSeconds(1.25));

        Assert.NotNull(slice);
        Assert.Equal(96000, slice!.Length); // 1.0s of interleaved stereo across three chunks
    }

    [Fact]
    public void Add_drops_oldest_chunks_when_capacity_exceeded()
    {
        var buffer = new AudioRingBuffer(48000, 2, 1); // 1 second capacity
        buffer.Add(T0, new float[48000]);                              // 0.5s
        buffer.Add(T0 + TimeSpan.FromSeconds(0.5), new float[48000]);   // 1.0s total
        buffer.Add(T0 + TimeSpan.FromSeconds(1.0), new float[48000]);  // 1.5s → drop first

        Assert.Equal(96000, buffer.SampleCount);
        Assert.Equal(T0 + TimeSpan.FromSeconds(0.5), buffer.EarliestUtc);
    }

    [Fact]
    public void Clear_empties_the_buffer()
    {
        var buffer = new AudioRingBuffer(48000, 2, 20);
        buffer.Add(T0, new float[48000]);

        buffer.Clear();

        Assert.Equal(0, buffer.SampleCount);
        Assert.Null(buffer.EarliestUtc);
        Assert.Null(buffer.LatestUtc);
    }

    [Fact]
    public async Task Concurrent_add_and_take_do_not_throw()
    {
        // The WASAPI capture thread calls Add while the clip-save thread calls Take.
        // Without a lock, Take's foreach over _chunks races with Add's Add/RemoveAt and
        // throws "Collection was modified; enumeration operation may not execute".
        // Hammer both to prove the lock holds.
        var buffer = new AudioRingBuffer(48000, 2, 20);
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();

        var adder = Task.Run(() =>
        {
            var t = T0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    buffer.Add(t, new float[4800]); // 0.05s of interleaved stereo
                    t += TimeSpan.FromSeconds(0.05);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
                stop.Cancel();
            }
        });

        var taker = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    buffer.Take(T0, T0 + TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
                stop.Cancel();
            }
        });

        await Task.Delay(400);
        stop.Cancel();
        await Task.WhenAll(adder, taker);

        Assert.Empty(errors);
    }
}
