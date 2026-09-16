namespace ERCTelemetry.Core.Analysis;

/// <summary>One speed-trace sample: distance through the lap (metres), speed (km/h)
/// and the pedal inputs at that point (0..1; 0 in traces recorded before pedal capture).</summary>
public sealed record LapTraceSample(float LapDistance, ushort Speed, float Throttle = 0, float Brake = 0);

/// <summary>The full speed trace of one completed player lap. Samples are ordered by
/// distance; <see cref="TrackLength"/> later ties the distance axis to the session's track.</summary>
public sealed record LapTrace(
    byte LapNumber,
    uint LapTimeMs,
    ushort TrackLength,
    IReadOnlyList<LapTraceSample> Samples);

/// <summary>Buffers the player's (distance, speed) samples per lap and hands out the
/// finished lap's trace when the store detects the lap completion. Samples are taken
/// distance-based (≥20 m apart, ~250 for a 5 km lap) — enough to resolve brake points,
/// cheap in memory and storage. Not thread-safe: lives inside the single-writer store.</summary>
public sealed class LapTraceRecorder
{
    private const float MinSampleGapMetres = 20f;

    // Two buffers: telemetry packets carrying the NEW lap number can arrive before the
    // LapData packet that announces the completion — the old lap's samples must survive
    // that packet-order race. Sample() stashes the finished buffer into _prev when the
    // lap number changes; the handout looks in both.
    private byte _lapNum;
    private List<LapTraceSample> _samples = [];
    private byte _prevLapNum;
    private List<LapTraceSample> _prevSamples = [];
    private float _lastSampledDistance;
    private bool _hasSample;

    /// <summary>Feeds one player-telemetry cycle (speed/pedals from CarTelemetry,
    /// distance from the latest LapData). <paramref name="lapNum"/> = the lap the sample
    /// belongs to (0 = unknown — skipped). A lap-number change stashes the finished buffer.</summary>
    public void Sample(byte lapNum, float lapDistance, ushort speed, float throttle = 0, float brake = 0)
    {
        if (lapNum == 0)
        {
            return;
        }

        if (lapNum != _lapNum)
        {
            _prevLapNum = _lapNum;
            _prevSamples = _samples;
            _lapNum = lapNum;
            _samples = [];
        }

        if (ShouldSample(lapDistance))
        {
            _samples.Add(new LapTraceSample(
                lapDistance, speed, Math.Clamp(throttle, 0f, 1f), Math.Clamp(brake, 0f, 1f)));
            _lastSampledDistance = lapDistance;
            _hasSample = true;
        }
    }

    /// <summary>True when the car moved ≥20 m since the last accepted sample. The very
    /// first sample of a lap is always taken so the trace starts at the line.</summary>
    private bool ShouldSample(float lapDistance) =>
        !_hasSample || lapDistance < _lastSampledDistance ||
        lapDistance - _lastSampledDistance >= MinSampleGapMetres;

    /// <summary>Handout of the finished lap's trace: looks in the stashed previous-lap
    /// buffer first (packet-order race), then in the current buffer. Returns null when
    /// that lap has no samples (e.g. a pit lap whose telemetry never streamed).</summary>
    public IReadOnlyList<LapTraceSample>? TakeTrace(byte lapNumber)
    {
        if (_prevLapNum == lapNumber)
        {
            var prev = _prevSamples;
            _prevLapNum = 0;
            _prevSamples = [];
            return prev.Count > 0 ? prev : null;
        }

        if (_lapNum == lapNumber)
        {
            var current = _samples;
            _samples = [];
            _prevSamples = [];
            _prevLapNum = 0;
            _hasSample = false;
            return current.Count > 0 ? current : null;
        }

        return null;
    }

    /// <summary>Drops all buffers (session change).</summary>
    public void Reset()
    {
        _lapNum = 0;
        _samples = [];
        _prevLapNum = 0;
        _prevSamples = [];
        _lastSampledDistance = 0;
        _hasSample = false;
    }
}