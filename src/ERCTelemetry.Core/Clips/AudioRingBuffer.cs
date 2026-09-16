namespace ERCTelemetry.Core.Clips;

/// <summary>Ring buffer of float PCM audio samples (WASAPI loopback format: 32-bit
/// float, interleaved, typically 48 kHz stereo). Each chunk records the UTC time its
/// first sample was captured, so a clip window can be sliced out by wall-clock time and
/// aligned to the video frames. Pure logic — no NAudio dependency, so it is
/// unit-testable. Audio is small (≈1.9 MB for 20 s at 48 kHz stereo) so it stays in
/// RAM; only the video frames go to disk.</summary>
public sealed class AudioRingBuffer
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _maxSamples;
    private readonly object _gate = new();
    private readonly List<Chunk> _chunks = new();
    private int _totalSamples;

    /// <summary>A contiguous run of interleaved samples whose first sample was captured
    /// at <paramref name="Utc"/>.</summary>
    private sealed record Chunk(DateTimeOffset Utc, float[] Samples);

    public AudioRingBuffer(int sampleRate, int channels, int capacitySeconds)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _maxSamples = Math.Max(1, capacitySeconds * sampleRate * channels);
    }

    /// <summary>Sample rate of the buffered audio (e.g. 48000).</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Channel count of the buffered audio (e.g. 2 for stereo).</summary>
    public int Channels => _channels;

    /// <summary>Total interleaved samples currently buffered.</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _totalSamples;
            }
        }
    }

    /// <summary>Maximum seconds of audio the buffer holds (capacity / rate / channels).</summary>
    public int CapacitySeconds => _maxSamples / (_sampleRate * _channels);

    /// <summary>Timestamp of the oldest buffered chunk, or null when empty.</summary>
    public DateTimeOffset? EarliestUtc
    {
        get
        {
            lock (_gate)
            {
                return _chunks.Count > 0 ? _chunks[0].Utc : null;
            }
        }
    }

    /// <summary>Timestamp just past the newest buffered sample, or null when empty.</summary>
    public DateTimeOffset? LatestUtc
    {
        get
        {
            lock (_gate)
            {
                if (_chunks.Count == 0)
                {
                    return null;
                }

                var last = _chunks[^1];
                return last.Utc + TimeSpan.FromSeconds((double)last.Samples.Length / (_channels * _sampleRate));
            }
        }
    }

    /// <summary>Appends a chunk, dropping the oldest chunks once the capacity is
    /// exceeded. The chunk's samples are assumed contiguous at the configured rate,
    /// starting at <paramref name="utc"/>.</summary>
    public void Add(DateTimeOffset utc, float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _chunks.Add(new Chunk(utc, samples));
            _totalSamples += samples.Length;
            while (_totalSamples > _maxSamples && _chunks.Count > 1)
            {
                _totalSamples -= _chunks[0].Samples.Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    /// <summary>Slices the interleaved samples covering [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>), starting at the first sample at or after fromUtc and
    /// ending at the last sample before toUtc. Returns null when no buffered audio
    /// overlaps the window (or the window is empty).</summary>
    public float[]? Take(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        lock (_gate)
        {
            if (toUtc <= fromUtc || _chunks.Count == 0)
            {
                return null;
            }

            var result = new List<float>();
            var found = false;
            foreach (var chunk in _chunks)
            {
                var chunkEnd = chunk.Utc + TimeSpan.FromSeconds((double)chunk.Samples.Length / (_channels * _sampleRate));
                if (chunkEnd <= fromUtc)
                {
                    continue; // entirely before the window
                }

                if (chunk.Utc >= toUtc)
                {
                    break; // this and all later chunks start after the window
                }

                var startIndex = 0;
                if (chunk.Utc < fromUtc)
                {
                    var offset = (fromUtc - chunk.Utc).TotalSeconds * _sampleRate * _channels;
                    startIndex = (int)Math.Ceiling(offset);
                }

                var endIndex = chunk.Samples.Length;
                if (chunkEnd > toUtc)
                {
                    var offset = (toUtc - chunk.Utc).TotalSeconds * _sampleRate * _channels;
                    endIndex = (int)Math.Floor(offset);
                }

                if (startIndex < endIndex)
                {
                    for (var i = startIndex; i < endIndex; i++)
                    {
                        result.Add(chunk.Samples[i]);
                    }

                    found = true;
                }
            }

            return found ? result.ToArray() : null;
        }
    }

    /// <summary>Drops all buffered audio (called when a network session starts so stale
    /// audio from a previous session never leaks into the first clip).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _totalSamples = 0;
        }
    }
}
