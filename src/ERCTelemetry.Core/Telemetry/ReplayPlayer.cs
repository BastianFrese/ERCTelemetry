using System.Diagnostics;
using System.Threading.Channels;
using System.IO.Compression;
using System.Text;
using F1Game.UDP;
using F1Game.UDP.Packets;

namespace ERCTelemetry.Core.Telemetry;

/// <summary>State of a running replay, polled by the UI.</summary>
public sealed record ReplayProgress(string Path, long Frame, long Total);

/// <summary>Plays recorded .f1rec data back into the packet channel at (roughly) the
/// original capture cadence using the recorded UTC-tick gaps — the same data path a live
/// game produces. Streams frame by frame (no full materialization), parses via the
/// listener's parse path and paces with a stopwatch. Corrupt frames are skipped like
/// dropped datagrams (parse-error counter; the replay continues).</summary>
public sealed class ReplayPlayer
{
    private const string Magic = "F1REC";
    private const byte FormatVersion = 1;
    private const int MaxFrameBytes = 1_000_000;

    private readonly ChannelWriter<UnionPacket> _output;
    private readonly PacketStats _stats;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public ReplayPlayer(ChannelWriter<UnionPacket> output, PacketStats stats)
    {
        _output = output;
        _stats = stats;
    }

    public bool IsPlaying { get; private set; }

    public ReplayProgress? Progress { get; private set; }

    /// <summary>Raised on a fatal playback error (missing file, not a recording).</summary>
    public event Action<Exception>? Failed;
    public event Action? Completed;

    public void Play(string path, double speed = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsPlaying)
        {
            throw new InvalidOperationException("Replay is already running.");
        }

        var clamped = Math.Clamp(speed, 0.25, 8.0);
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(path, clamped, _cts.Token));
    }

    public void Stop()
    {
        if (!IsPlaying)
        {
            return;
        }

        _cts!.Cancel();
        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e =>
            e is OperationCanceledException or TaskCanceledException))
        {
            // cancelled by Stop — the expected way out
        }
    }

    private async Task RunAsync(string path, double speed, CancellationToken ct)
    {
        lock (_gate)
        {
            IsPlaying = true;
            Progress = new ReplayProgress(path, 0, 0);
        }

        try
        {
            await PlayLoopAsync(path, speed, ct);
            Completed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Stop() pressed — no completion notification
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            lock (_gate)
            {
                IsPlaying = false;
            }
        }
    }

    private static async Task DelayOrCancel(int milliseconds, CancellationToken ct)
    {
        await Task.Delay(milliseconds, ct);
    }

    private async Task PlayLoopAsync(string path, double speed, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);

        if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
        {
            throw new InvalidDataException($"Not an ERCTelemetry recording: {path}");
        }

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported recording version {version}: {path}");
        }

        long firstTicks = 0;
        var haveFirst = false;
        var clock = new Stopwatch();
        long frame = 0;

        while (true)
        {
            long ticks;
            byte[] data;
            try
            {
                ticks = reader.ReadInt64();
                var length = reader.ReadInt32();
                if (length < 0 || length > MaxFrameBytes)
                {
                    break; // torn tail — treated like the recorder's trailing-frame rule
                }

                data = reader.ReadBytes(length);
                if (data.Length != length)
                {
                    break;
                }
            }
            catch (EndOfStreamException)
            {
                break;
            }

            if (!haveFirst)
            {
                haveFirst = true;
                firstTicks = ticks;
                clock.Start();
            }

            // Pace against the recorded tick gaps, scaled by speed. If the loop falls
            // behind, it catches up instead of buffering the whole gap.
            var dueMs = (ticks - firstTicks) / TimeSpan.TicksPerMillisecond / speed;
            var wait = dueMs - clock.ElapsedMilliseconds;
            if (wait > 15)
            {
                await DelayOrCancel((int)wait, ct);
            }

            UnionPacket packet;
            try
            {
                packet = data.AsSpan().ToPacket();
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or NotEnoughBytesException
                or InvalidPacketTypeException)
            {
                _stats.RecordParseError();
                continue;
            }

            _stats.RecordPacket(packet.PacketType, data.Length);
            Interlocked.Increment(ref frame);
            Progress = new ReplayProgress(path, frame, -1);
            while (!_output.TryWrite(packet))
            {
                await DelayOrCancel(10, ct);
            }
        }
    }
}