using System.Net.Sockets;
using System.Threading.Channels;
using F1Game.UDP.Enums;
using F1Game.UDP.Packets;
using ERCTelemetry.Core.Telemetry;
using Xunit;

namespace ERCTelemetry.Core.Tests.Telemetry;

public class UdpListenerTests
{
    private const int MotionPacketSize = 1349;

    [Fact]
    public async Task Valid_motion_datagram_is_parsed_and_forwarded()
    {
        var channel = Channel.CreateBounded<UnionPacket>(16);
        var stats = new PacketStats();
        using var listener = new UdpListener(0, channel.Writer, stats);
        var runTask = listener.StartAsync();

        using var sender = new UdpClient();
        var datagram = BuildMotionDatagram();
        await sender.SendAsync(datagram, datagram.Length, "127.0.0.1", listener.LocalPort);

        var packet = await ReadWithTimeoutAsync(channel.Reader, TimeSpan.FromSeconds(5));

        Assert.Equal(PacketType.Motion, packet.PacketType);
        Assert.Equal((ushort)2026, packet.Header.PacketFormat);
        Assert.Equal(1, stats.TotalPackets);
        Assert.Equal(0, stats.ParseErrors + stats.FormatErrors);
        Assert.Equal(FormatState.Supported, listener.CurrentFormatState);

        listener.Dispose();
        await runTask;
    }

    [Fact]
    public async Task Datagram_with_wrong_format_is_rejected_without_parse()
    {
        var channel = Channel.CreateBounded<UnionPacket>(16);
        var stats = new PacketStats();
        using var listener = new UdpListener(0, channel.Writer, stats);
        var runTask = listener.StartAsync();

        var oldFormat = BuildMotionDatagram();
        oldFormat[0] = 0xE7; // 2023 little-endian
        oldFormat[1] = 0x07;

        using var sender = new UdpClient();
        await sender.SendAsync(oldFormat, oldFormat.Length, "127.0.0.1", listener.LocalPort);

        await Task.Delay(300); // allow the receive loop to process the datagram
        Assert.False(channel.Reader.TryRead(out _));
        Assert.Equal(0, stats.TotalPackets);
        Assert.Equal(1, stats.FormatErrors);
        Assert.Equal(FormatState.Unsupported, listener.CurrentFormatState);

        listener.Dispose();
        await runTask;
    }

    private static async Task<UnionPacket> ReadWithTimeoutAsync(ChannelReader<UnionPacket> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    /// <summary>Builds a synthetic but wire-valid Motion datagram: correct 29-byte header
    /// (2026 format, game year 26, type Motion) followed by zero padding to 1349 bytes.</summary>
    private static byte[] BuildMotionDatagram() => TestDatagrams.Motion();
}