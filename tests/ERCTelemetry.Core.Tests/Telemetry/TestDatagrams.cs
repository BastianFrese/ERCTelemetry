using System.Text;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Tests.Telemetry;

/// <summary>Synthetic but wire-valid UDP datagrams shared by the listener and replayer
/// tests. Header layout (29 bytes, 2026 Season Pack): format u16, game year u8,
/// major/minor u8, packet version u8, packet type u8, session UID u64, session time f32,
/// frame identifier u32, overall frame identifier u32, player car index u8,
/// secondary player car index u8.</summary>
public static class TestDatagrams
{
    public const int MotionPacketSize = 1349;

    public static byte[] Motion() => MotionWith();

    public static byte[] MotionWith(ulong sessionUid = 0x0123456789ABCDEF, uint frameId = 100)
    {
        var datagram = new byte[MotionPacketSize];

        using var writer = new BinaryWriter(new MemoryStream(datagram), Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)2026);          // packet format
        writer.Write((byte)26);              // game year
        writer.Write((byte)1);               // game major version
        writer.Write((byte)0);               // game minor version
        writer.Write((byte)1);               // packet version
        writer.Write((byte)PacketType.Motion);
        writer.Write(sessionUid);
        writer.Write(12.5f);                 // session time
        writer.Write(frameId);
        writer.Write(frameId);               // overall frame identifier
        writer.Write((byte)0);               // player car index
        writer.Write((byte)255);             // secondary player car index

        return datagram;
    }
}