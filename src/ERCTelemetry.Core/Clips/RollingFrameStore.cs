using System.IO;

namespace ERCTelemetry.Core.Clips;

/// <summary>Rolling disk buffer of captured JPEG frames, keyed by capture time. Frames are
/// appended to segment files (one per ~1s of video) so RAM stays flat no matter how long a
/// session runs — only the last <see cref="Window"/> of video is kept on disk. TakeSince
/// reads the window back for a clip. Thread-safe: Add/TakeSince are guarded by a single
/// lock (one capture loop, one saver).</summary>
public sealed class RollingFrameStore : IDisposable
{
    private const int FramesPerSegment = 60; // ~1s at 60fps

    private readonly string _directory;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly List<Segment> _segments = new();
    private FileStream? _current;
    private string _currentPath = "";
    private DateTimeOffset _currentStart;
    private DateTimeOffset _currentEnd;
    private int _currentFrames;
    private int _disposed;

    /// <summary>A closed segment file covering [StartUtc, EndUtc].</summary>
    private sealed record Segment(string Path, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    /// <summary>How much video the buffer keeps (the clip window).</summary>
    public TimeSpan Window => _window;

    public RollingFrameStore(string directory, TimeSpan window)
    {
        _directory = directory;
        _window = window;
        Directory.CreateDirectory(directory);
        // Segment files from a previous run are stale — drop them.
        foreach (var file in Directory.EnumerateFiles(directory, "seg-*.bin"))
        {
            TryDelete(file);
        }
    }

    /// <summary>Appends one frame, closing the current segment when it is full and
    /// deleting segments older than the window.</summary>
    public void Add(DateTimeOffset utc, byte[] jpeg)
    {
        lock (_gate)
        {
            if (_current is null || _currentFrames >= FramesPerSegment)
            {
                CloseCurrent();
                _currentStart = utc;
                _currentFrames = 0;
                _currentPath = Path.Combine(_directory, $"seg-{utc.UtcTicks}.bin");
                _current = new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }

            try
            {
                WriteFrame(_current, utc, jpeg);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk failure (full / locked / unplugged): drop this frame and discard
                // the broken stream so the NEXT Add opens a fresh segment. Without this
                // every later Add throws against the same dead FileStream and the capture
                // loop silently drops video for the rest of the session.
                _current.Dispose();
                _current = null;
                TryDelete(_currentPath);
                return;
            }

            _currentEnd = utc;
            _currentFrames++;
            Prune(utc);
        }
    }

    /// <summary>All frames captured at or after <paramref name="sinceUtc"/>, oldest first.
    /// Returns an empty list when nothing matches.</summary>
    public IReadOnlyList<(DateTimeOffset Utc, byte[] Jpeg)> TakeSince(DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            FlushCurrent();
            var result = new List<(DateTimeOffset, byte[])>();
            foreach (var segment in _segments)
            {
                if (segment.EndUtc < sinceUtc)
                {
                    continue;
                }

                ReadSegment(segment.Path, sinceUtc, result);
            }

            // The current (open) segment is not in _segments yet — read it too, or the
            // last <60 frames (up to ~1s of video, including the collision moment) would
            // be missing from every clip.
            if (_current is not null && _currentEnd >= sinceUtc)
            {
                ReadSegment(_currentPath, sinceUtc, result);
            }

            return result;
        }
    }

    /// <summary>Drops all buffered frames (called when a network session starts so stale
    /// video from a previous session never leaks into the first clip).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            CloseCurrent();
            foreach (var segment in _segments)
            {
                TryDelete(segment.Path);
            }

            _segments.Clear();
        }
    }

    private void CloseCurrent()
    {
        if (_current is null)
        {
            return;
        }

        _current.Flush();
        _current.Dispose();
        _current = null;
        _segments.Add(new Segment(_currentPath, _currentStart, _currentEnd));
    }

    private void FlushCurrent()
    {
        // Make the current segment's data visible to readers without closing it — the
        // capture loop keeps appending after the clip save has read its window.
        _current?.Flush();
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_segments.Count > 0 && _segments[0].EndUtc < cutoff)
        {
            TryDelete(_segments[0].Path);
            _segments.RemoveAt(0);
        }
    }

    private static void WriteFrame(FileStream stream, DateTimeOffset utc, byte[] jpeg)
    {
        Span<byte> header = stackalloc byte[12];
        BitConverter.TryWriteBytes(header[..8], utc.UtcTicks);
        BitConverter.TryWriteBytes(header[8..], jpeg.Length);
        stream.Write(header);
        stream.Write(jpeg);
    }

    private static void ReadSegment(string path, DateTimeOffset sinceUtc, List<(DateTimeOffset, byte[])> result)
    {
        // ReadWrite share: the current segment is still open for writing by the capture
        // loop when TakeSince reads it — a plain Read share would clash with that handle.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[12];
        while (fs.Position < fs.Length)
        {
            fs.ReadExactly(header);
            var utc = new DateTimeOffset(BitConverter.ToInt64(header), TimeSpan.Zero);
            var length = BitConverter.ToInt32(header[8..]);
            if (utc >= sinceUtc)
            {
                var jpeg = new byte[length];
                fs.ReadExactly(jpeg);
                result.Add((utc, jpeg));
            }
            else
            {
                fs.Position += length;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            CloseCurrent();
            foreach (var segment in _segments)
            {
                TryDelete(segment.Path);
            }

            _segments.Clear();
        }
    }
}
