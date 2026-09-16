using ERCTelemetry.Core.Networking;
using Xunit;

namespace ERCTelemetry.Core.Tests;

public class LocalIpProviderTests
{
    [Fact]
    public void Select_ordered_prefers_gateway_connected_interfaces()
    {
        // Arrange
        var candidates = new[]
        {
            new LanIpCandidate("192.168.1.50", HasGateway: false), // virtual adapter, no gateway
            new LanIpCandidate("192.168.1.20", HasGateway: true),  // the real LAN adapter
        };

        // Act
        var ordered = LocalIpProvider.SelectOrdered(candidates);

        // Assert
        Assert.Equal(new[] { "192.168.1.20", "192.168.1.50" }, ordered);
    }

    [Fact]
    public void Select_ordered_is_stable_between_same_rank_candidates()
    {
        // Arrange
        var candidates = new[]
        {
            new LanIpCandidate("10.0.0.9", HasGateway: true),
            new LanIpCandidate("10.0.0.2", HasGateway: true),
        };

        // Act
        var ordered = LocalIpProvider.SelectOrdered(candidates);

        // Assert — alphabetical tie-break so the UI text never flickers between ticks
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.9" }, ordered);
    }

    [Theory]
    [InlineData("127.0.0.1")]      // loopback can never reach a console
    [InlineData("127.1.2.3")]
    [InlineData("169.254.10.5")]   // APIPA = no DHCP, not routable
    [InlineData("169.254.255.255")]
    [InlineData("0.0.0.0")]        // unspecified
    [InlineData("::1")]            // IPv6 loopback
    [InlineData("fe80::1")]        // IPv6 link-local
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void Select_ordered_drops_non_routable_candidates(string address)
    {
        // Arrange
        var candidates = new[] { new LanIpCandidate(address, HasGateway: true) };

        // Act
        var ordered = LocalIpProvider.SelectOrdered(candidates);

        // Assert
        Assert.Empty(ordered);
    }

    [Fact]
    public void Select_ordered_keeps_only_routable_addresses_in_mixed_set()
    {
        // Arrange
        var candidates = new[]
        {
            new LanIpCandidate("127.0.0.1", HasGateway: false),
            new LanIpCandidate("169.254.9.9", HasGateway: false),
            new LanIpCandidate("192.168.178.44", HasGateway: true),
        };

        // Act
        var ordered = LocalIpProvider.SelectOrdered(candidates);

        // Assert
        Assert.Equal(new[] { "192.168.178.44" }, ordered);
    }

    [Fact]
    public void Select_ordered_on_empty_input_returns_empty()
    {
        Assert.Empty(LocalIpProvider.SelectOrdered(Array.Empty<LanIpCandidate>()));
    }

    [Fact]
    public void Live_enumeration_returns_only_routable_addresses()
    {
        // Arrange/Act — runs on whatever network the test machine has; may be empty
        // (CI without NICs), but must never contain loopback/APIPA junk.
        var addresses = LocalIpProvider.GetLanIPv4Addresses();

        // Assert
        Assert.All(addresses, ip =>
        {
            Assert.True(LocalIpProvider.IsRoutable(ip), $"{ip} should be routable");
            Assert.DoesNotContain(':', ip); // IPv4 only
        });
    }
}