using ERCTelemetry.Core.Clips;
using Xunit;

namespace ERCTelemetry.Core.Tests.Clips;

/// <summary>RollingFrameStore: disk-backed rolling buffer of JPEG frames — round-trips
/// frames across segment files, filters by capture time, prunes old segments and cleans
/// up its files on dispose.</summary>
public class RollingFrameStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Jpeg(int seed) => new byte[] { 0xFF, 0xD8, (byte)seed, 0x00 };

    [Fact]
    public void TakeSince_returns_empty_when_nothing_added()
    {
        var dir = NewTempDir();
        try
        {
            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(30));

            Assert.Empty(store.TakeSince(T0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TakeSince_round_trips_frames_across_segments()
    {
        var dir = NewTempDir();
        try
        {
            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(30));
            // 65 frames → crosses the 60-frame segment boundary into a second segment.
            for (var i = 0; i < 65; i++)
            {
                store.Add(T0 + TimeSpan.FromSeconds(i), Jpeg(i));
            }

            var frames = store.TakeSince(T0);

            Assert.Equal(65, frames.Count);
            Assert.Equal(T0, frames[0].Utc);
            Assert.Equal(Jpeg(0), frames[0].Jpeg);
            Assert.Equal(T0 + TimeSpan.FromSeconds(64), frames[^1].Utc);
            Assert.Equal(Jpeg(64), frames[^1].Jpeg);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TakeSince_filters_frames_before_sinceUtc()
    {
        var dir = NewTempDir();
        try
        {
            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(30));
            store.Add(T0, Jpeg(0));
            store.Add(T0 + TimeSpan.FromSeconds(1), Jpeg(1));
            store.Add(T0 + TimeSpan.FromSeconds(2), Jpeg(2));

            var frames = store.TakeSince(T0 + TimeSpan.FromSeconds(1));

            Assert.Equal(2, frames.Count);
            Assert.Equal(T0 + TimeSpan.FromSeconds(1), frames[0].Utc);
            Assert.Equal(T0 + TimeSpan.FromSeconds(2), frames[^1].Utc);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Add_prunes_segments_older_than_the_window()
    {
        var dir = NewTempDir();
        try
        {
            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(10));
            // Fill segment 1 with frames spanning 60s, then jump far ahead so the
            // segment's EndUtc falls outside the 10s window.
            for (var i = 0; i < 60; i++)
            {
                store.Add(T0 + TimeSpan.FromSeconds(i), Jpeg(i));
            }

            store.Add(T0 + TimeSpan.FromSeconds(120), Jpeg(120));

            var frames = store.TakeSince(T0);
            Assert.Single(frames);
            Assert.Equal(T0 + TimeSpan.FromSeconds(120), frames[0].Utc);
            // The pruned segment file is gone; only the current (open) segment remains.
            Assert.Single(Directory.EnumerateFiles(dir, "seg-*.bin"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clear_drops_all_buffered_frames()
    {
        var dir = NewTempDir();
        try
        {
            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(30));
            store.Add(T0, Jpeg(0));
            store.Add(T0 + TimeSpan.FromSeconds(1), Jpeg(1));

            store.Clear();

            Assert.Empty(store.TakeSince(T0));
            Assert.Empty(Directory.EnumerateFiles(dir, "seg-*.bin"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Dispose_deletes_segment_files()
    {
        var dir = NewTempDir();
        var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(30));
        store.Add(T0, Jpeg(0));
        store.Add(T0 + TimeSpan.FromSeconds(1), Jpeg(1));

        store.Dispose();

        Assert.Empty(Directory.EnumerateFiles(dir, "seg-*.bin"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Constructor_cleans_stale_segments_from_a_previous_run()
    {
        var dir = NewTempDir();
        try
        {
            // Simulate a crashed run: a segment file left behind.
            File.WriteAllBytes(Path.Combine(dir, "seg-123.bin"), new byte[] { 1, 2, 3 });

            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(30));

            Assert.Empty(Directory.EnumerateFiles(dir, "seg-*.bin"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Window_returns_the_configured_window()
    {
        var dir = NewTempDir();
        try
        {
            using var store = new RollingFrameStore(dir, TimeSpan.FromSeconds(42));

            Assert.Equal(TimeSpan.FromSeconds(42), store.Window);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
