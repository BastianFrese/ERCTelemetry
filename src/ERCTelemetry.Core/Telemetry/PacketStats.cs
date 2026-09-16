using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Telemetry;

/// <summary>Thread-safe counters describing the incoming telemetry stream. Written on the
/// UDP receive thread, read by UI samplers at low frequency.</summary>
public sealed class PacketStats
{
    private const int PacketTypeCount = 17; // PacketType values 0..16

    private readonly long[] _countsByType = new long[PacketTypeCount];
    private long _totalPackets;
    private long _totalBytes;
    private long _parseErrors;
    private long _formatErrors;

    /// <summary>Total packets successfully parsed and forwarded.</summary>
    public long TotalPackets => Interlocked.Read(ref _totalPackets);

    /// <summary>Total UDP payload bytes received.</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Datagrams that could not be parsed (unknown type or too short).</summary>
    public long ParseErrors => Interlocked.Read(ref _parseErrors);

    /// <summary>Datagrams from an unsupported packet format (e.g. pre-2026 layout).</summary>
    public long FormatErrors => Interlocked.Read(ref _formatErrors);

    public void RecordPacket(PacketType type, int byteCount)
    {
        Interlocked.Increment(ref _totalPackets);
        Interlocked.Add(ref _totalBytes, byteCount);
        Interlocked.Increment(ref _countsByType[(int)type]);
    }

    public void RecordParseError() => Interlocked.Increment(ref _parseErrors);

    public void RecordFormatError() => Interlocked.Increment(ref _formatErrors);

    public long GetCount(PacketType type) => Interlocked.Read(ref _countsByType[(int)type]);
}