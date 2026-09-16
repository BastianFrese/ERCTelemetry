using F1Game.UDP;
using F1Game.UDP.Packets;

namespace ERCTelemetry.Core.Telemetry;

/// <summary>One replayed packet with its recorded UTC capture time.</summary>
public readonly record struct ReplayedPacket(long UtcTicks, UnionPacket Packet);

/// <summary>Result of replaying a recording: what parsed, plus counts of frames that were
/// skipped so callers (and tests) can tell a healthy fixture from a damaged one.</summary>
public sealed record PacketReplay(
    IReadOnlyList<ReplayedPacket> Packets,
    int UnsupportedFormatFrames,
    int CorruptFrames);

/// <summary>Replays recorded <c>.f1rec</c> datagrams through the same bytes →
/// <see cref="UnionPacket"/> parse the live <see cref="UdpListener"/> performs — used by
/// tests on real fixtures and available for a future "replay a recording" ingestion.</summary>
public static class PacketReplayer
{
    private const ushort SupportedFormat = 2026;

    /// <summary>Parses recorded frames in capture order via the same parse path the
    /// listener uses. Frames with a non-2026 header format are counted as unsupported
    /// (mirrors the listener's format gate), frames the parser rejects are counted as
    /// corrupt and skipped — a truncated recording degrades like a dropped datagram
    /// instead of failing the whole replay.</summary>
    public static PacketReplay Parse(IReadOnlyList<RecordedFrame> frames)
    {
        var packets = new List<ReplayedPacket>(frames.Count);
        var unsupported = 0;
        var corrupt = 0;

        foreach (var frame in frames)
        {
            if (frame.Data.Length < 2 || (ushort)(frame.Data[0] | frame.Data[1] << 8) != SupportedFormat)
            {
                unsupported++;
                continue;
            }

            try
            {
                packets.Add(new ReplayedPacket(frame.UtcTicks, frame.Data.AsSpan().ToPacket()));
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or F1Game.UDP.NotEnoughBytesException)
            {
                corrupt++;
            }
        }

        return new PacketReplay(packets, unsupported, corrupt);
    }

    /// <summary>Loads a <c>.f1rec</c> file and parses every frame. Throws when the file is
    /// not a recording (bad magic) — callers decide whether that is fatal.</summary>
    public static PacketReplay Load(string path) => Parse(PacketRecorder.ReadFrames(path));
}