using System.Runtime.InteropServices;
using ERCTelemetry.Core.Clips;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ERCTelemetry.App.Clips;

/// <summary>WASAPI loopback capture of the system audio (NAudio 3.x WasapiRecorder).
/// Converts the captured byte PCM into float samples and feeds the rolling
/// <see cref="AudioRingBuffer"/>, so a clip's audio can be sliced out by wall-clock time
/// and aligned to the video frames. The ring buffer is recreated when the device or
/// sample format changes. Pure capture: no WAV writing — the mux step reads the ring.</summary>
public sealed class AudioLoopbackSource : IDisposable
{
    private readonly Func<ClipSettings> _settings;
    private readonly object _gate = new();
    private WasapiRecorder? _capture;
    private MMDevice? _device;
    private AudioRingBuffer? _ring;
    private WaveFormat? _format;
    private bool _started;
    private int _disposed;

    public AudioLoopbackSource(Func<ClipSettings> settings)
    {
        _settings = settings;
    }

    /// <summary>The rolling PCM buffer (null until <see cref="Start"/> succeeds).</summary>
    public AudioRingBuffer? Ring
    {
        get
        {
            lock (_gate)
            {
                return _ring;
            }
        }
    }

    /// <summary>Starts capturing the configured (or default) render device. Safe to call
    /// again after <see cref="Stop"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            _device = ResolveDevice(_settings().AudioDeviceName);
            var builder = new WasapiRecorderBuilder();
            if (_device is not null)
            {
                builder = builder.WithDevice(_device);
            }

            _capture = builder.WithLoopbackCapture().Build();
            var format = _capture.WaveFormat;
            var settings = _settings();
            var ringSeconds = Math.Max(RingSeconds, settings.PreRollSeconds + settings.PostRollSeconds);
            lock (_gate)
            {
                _ring = new AudioRingBuffer(format.SampleRate, format.Channels, ringSeconds);
                // Fixed for the lifetime of this capture — decoded per chunk below.
                _format = format;
            }

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            App.Log($"Audio capture init failed: {ex.Message}");
            Stop();
        }
    }

    /// <summary>Stops capturing and releases the capture object and device. The ring
    /// buffer is kept so audio already captured stays available for clips.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            _device?.Dispose();
            _device = null;
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            var samples = ConvertToFloat(buffer, _format);
            AudioRingBuffer? ring;
            lock (_gate)
            {
                ring = _ring;
            }

            ring?.Add(DateTimeOffset.UtcNow, samples);
        }
        catch (Exception ex)
        {
            // Device unplugged / format change — skip the chunk, keep going.
            App.Log($"Audio capture failed: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Device unplugged or StopRecording — release the capture object so the next
        // Start recreates it.
        Stop();
    }

    /// <summary>Decodes a captured loopback chunk to float samples according to the
    /// capture's actual WaveFormat. WASAPI loopback hands back the device's mix format,
    /// which is NOT guaranteed to be float32 — a device configured for 16-bit PCM yields
    /// raw int16 bytes that, read as float, decode to pure noise. IEEE float (the common
    /// mix format) is copied as-is; unknown encodings fall back to that assumption.</summary>
    private static float[] ConvertToFloat(ReadOnlySpan<byte> buffer, WaveFormat? format)
    {
        if (format is not null && format.Encoding == WaveFormatEncoding.Pcm)
        {
            return format.BitsPerSample switch
            {
                16 => Pcm16ToFloat(buffer),
                24 => Pcm24ToFloat(buffer),
                32 => Pcm32ToFloat(buffer),
                _ => FloatAsBytes(buffer),
            };
        }

        return FloatAsBytes(buffer);
    }

    private static float[] Pcm16ToFloat(ReadOnlySpan<byte> buffer)
    {
        var raw = MemoryMarshal.Cast<byte, short>(buffer);
        var samples = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            samples[i] = raw[i] / 32768f;
        }

        return samples;
    }

    private static float[] Pcm24ToFloat(ReadOnlySpan<byte> buffer)
    {
        var sampleCount = buffer.Length / 3;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            // 24-bit little-endian two's complement, sign-extended into an int32.
            var raw = buffer[i * 3] | (buffer[i * 3 + 1] << 8) | (sbyte)buffer[i * 3 + 2] << 16;
            samples[i] = raw / 8388608f;
        }

        return samples;
    }

    private static float[] Pcm32ToFloat(ReadOnlySpan<byte> buffer)
    {
        var raw = MemoryMarshal.Cast<byte, int>(buffer);
        var samples = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            samples[i] = raw[i] / 2147483648f;
        }

        return samples;
    }

    private static float[] FloatAsBytes(ReadOnlySpan<byte> buffer)
    {
        var sampleCount = buffer.Length / sizeof(float);
        var samples = new float[sampleCount];
        buffer.CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
        return samples;
    }

    private static MMDevice? ResolveDevice(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null; // default render device
        }

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (device.FriendlyName == deviceName)
            {
                return device; // ownership passes to the caller (Stop disposes it)
            }

            // Every MMDevice wraps its own COM reference — release the ones we checked
            // but don't return, or each Start/Stop cycle leaks them all.
            device.Dispose();
        }

        return null; // not found — fall back to default
    }

    /// <summary>Friendly names of all active render (output) devices, for the settings
    /// dropdown. Empty on failure (no audio stack) — the caller falls back to default.</summary>
    public static IReadOnlyList<string> EnumerateDeviceNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d =>
                {
                    var name = d.FriendlyName;
                    d.Dispose();
                    return name;
                })
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private const int RingSeconds = 30;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
    }
}
