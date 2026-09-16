using System.Net;
using System.Net.Sockets;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Telemetry;
using Xunit;

namespace ERCTelemetry.Core.Tests.Telemetry;

/// <summary>UdpForwarder: re-sends raw datagrams to enabled loopback targets, skips
/// disabled/unresolvable ones and never throws into the caller.</summary>
public sealed class UdpForwarderTests
{
    [Fact]
    public async Task Forwards_datagram_to_enabled_target()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var settings = new AppSettings(
            udpPort: 20777,
            overlayPort: 8090,
            theme: AppTheme.System,
            rivalDriverName: null,
            closeToTray: false,
            showNotifications: true,
            autoStartListening: true,
            forwardingEnabled: true,
            forwardTargets: [new ForwardTarget { Address = "127.0.0.1", Port = port, Enabled = true }]);
        using var forwarder = new UdpForwarder(() => settings);

        var datagram = new byte[] { 1, 2, 3, 4, 5 };
        forwarder.Forward(datagram);

        var received = await ReceiveWithTimeoutAsync(receiver, TimeSpan.FromSeconds(5));
        Assert.Equal(datagram, received);
        Assert.Equal(1, forwarder.ForwardedPackets);
        Assert.Equal(0, forwarder.FailedSends);
    }

    [Fact]
    public async Task Disabled_target_is_skipped()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var settings = new AppSettings(
            udpPort: 20777,
            overlayPort: 8090,
            theme: AppTheme.System,
            rivalDriverName: null,
            closeToTray: false,
            showNotifications: true,
            autoStartListening: true,
            forwardingEnabled: true,
            forwardTargets: [new ForwardTarget { Address = "127.0.0.1", Port = port, Enabled = false }]);
        using var forwarder = new UdpForwarder(() => settings);

        forwarder.Forward(new byte[] { 1, 2, 3 });

        await Task.Delay(200); // allow any (wrong) send to land
        Assert.False(receiver.Available > 0);
        Assert.Equal(0, forwarder.ForwardedPackets);
    }

    [Fact]
    public void Forwarding_disabled_sends_nothing()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var settings = new AppSettings(
            udpPort: 20777,
            overlayPort: 8090,
            theme: AppTheme.System,
            rivalDriverName: null,
            closeToTray: false,
            showNotifications: true,
            autoStartListening: true,
            forwardingEnabled: false,
            forwardTargets: [new ForwardTarget { Address = "127.0.0.1", Port = port, Enabled = true }]);
        using var forwarder = new UdpForwarder(() => settings);

        forwarder.Forward(new byte[] { 1, 2, 3 });

        Assert.Equal(0, forwarder.ForwardedPackets);
        Assert.Equal(0, forwarder.FailedSends);
    }

    [Fact]
    public void Unresolvable_address_does_not_throw()
    {
        var settings = new AppSettings(
            udpPort: 20777,
            overlayPort: 8090,
            theme: AppTheme.System,
            rivalDriverName: null,
            closeToTray: false,
            showNotifications: true,
            autoStartListening: true,
            forwardingEnabled: true,
            forwardTargets: [new ForwardTarget { Address = "no-such-host.invalid", Port = 20779, Enabled = true }]);
        using var forwarder = new UdpForwarder(() => settings);

        // Must not throw — the unresolvable hostname is skipped, not raised.
        forwarder.Forward(new byte[] { 1, 2, 3 });
        Assert.Equal(0, forwarder.ForwardedPackets);
    }

    [Fact]
    public async Task Target_list_change_is_picked_up_without_restart()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var settings = new AppSettings(
            udpPort: 20777,
            overlayPort: 8090,
            theme: AppTheme.System,
            rivalDriverName: null,
            closeToTray: false,
            showNotifications: true,
            autoStartListening: true,
            forwardingEnabled: true,
            forwardTargets: [new ForwardTarget { Address = "127.0.0.1", Port = port, Enabled = true }]);
        using var forwarder = new UdpForwarder(() => settings);

        forwarder.Forward(new byte[] { 1, 2, 3 });
        var first = await ReceiveWithTimeoutAsync(receiver, TimeSpan.FromSeconds(5));

        // A settings save produces a new AppSettings with a new target list reference.
        settings = settings with
        {
            ForwardTargets = [new ForwardTarget { Address = "127.0.0.1", Port = port, Enabled = true }],
        };
        forwarder.Forward(new byte[] { 4, 5, 6 });
        var second = await ReceiveWithTimeoutAsync(receiver, TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 1, 2, 3 }, first);
        Assert.Equal(new byte[] { 4, 5, 6 }, second);
        Assert.Equal(2, forwarder.ForwardedPackets);
    }

    private static async Task<byte[]> ReceiveWithTimeoutAsync(UdpClient receiver, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var result = await receiver.ReceiveAsync(cts.Token);
        return result.Buffer;
    }
}
