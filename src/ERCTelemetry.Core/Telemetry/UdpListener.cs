using System.Net.Sockets;
using System.Threading.Channels;
using F1Game.UDP;
using F1Game.UDP.Packets;

namespace ERCTelemetry.Core.Telemetry;

/// <summary>Result of the datagram format check performed on every received packet.</summary>
public enum FormatState
{
    Unknown = 0,
    Supported = 1,
    Unsupported = 2,
}

/// <summary>Receives the game's UDP telemetry stream, parses datagrams into
/// <see cref="UnionPacket"/>s and forwards them into a channel. Also exposes every raw
/// datagram through an optional tap so recordings capture the original bytes.</summary>
public sealed class UdpListener : IDisposable
{
    private readonly UdpClient _client;
    private readonly ChannelWriter<UnionPacket> _output;
    private readonly PacketStats _stats;
    private readonly Action<ReadOnlyMemory<byte>>? _rawTap;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;
    private int _formatState;

    /// <summary>Raised on every failed receive loop iteration; the listener keeps running.</summary>
    public event Action<Exception>? ReceiveError;

    public UdpListener(int port, ChannelWriter<UnionPacket> output, PacketStats stats,
        Action<ReadOnlyMemory<byte>>? rawTap = null)
    {
        _client = new UdpClient(port); // binds immediately; throws SocketException when taken
        _output = output;
        _stats = stats;
        _rawTap = rawTap;
    }

    /// <summary>Local UDP port actually bound (useful when constructed with port 0).</summary>
    public int LocalPort => ((System.Net.IPEndPoint)_client.Client.LocalEndPoint!).Port;

    /// <summary>Current verdict on the incoming packet layout (2026 Season Pack vs older).</summary>
    public FormatState CurrentFormatState => (FormatState)Volatile.Read(ref _formatState);

    /// <summary>Starts the receive loop. The returned task completes when <see cref="Dispose"/>
    /// is called or a fatal socket error occurs.</summary>
    public Task StartAsync()
    {
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        return _runTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _client.ReceiveAsync(ct);
                HandleDatagram(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // client closed by Dispose
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                // Raw-tap or unexpected socket failures must not silently kill the receive
                // loop — the user would lose telemetry while the UI still shows "Listening…".
                _stats.RecordParseError();
                ReceiveError?.Invoke(ex);
            }
        }
    }

    private void HandleDatagram(byte[] buffer)
    {
        _rawTap?.Invoke(buffer);

        // The packet format is the first ushort of every datagram (little-endian).
        if (buffer.Length >= 2 && BitConverter.ToUInt16(buffer, 0) == TelemetryConstants.ExpectedPacketFormat)
        {
            Volatile.Write(ref _formatState, (int)FormatState.Supported);
        }
        else
        {
            Volatile.Write(ref _formatState, (int)FormatState.Unsupported);
            _stats.RecordFormatError();
            return;
        }

        try
        {
            UnionPacket packet = buffer.AsSpan().ToPacket();
            _stats.RecordPacket(packet.PacketType, buffer.Length);
            _output.TryWrite(packet);
        }
        catch (InvalidPacketTypeException)
        {
            _stats.RecordParseError();
        }
        catch (NotEnoughBytesException)
        {
            _stats.RecordParseError();
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _client.Dispose();
        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Receive loop already stopped; nothing to surface.
        }
        _cts.Dispose();
        // Note: the output channel is intentionally NOT completed here —
        // its owner decides when the pipeline shuts down.
    }
}