using ERCTelemetry.Core;
using Xunit;

namespace ERCTelemetry.Core.Tests;

public class TelemetryConstantsTests
{
    [Fact]
    public void Defaults_match_f1_game_udp_spec()
    {
        Assert.Equal(20777, TelemetryConstants.DefaultUdpPort);
        Assert.Equal(22, TelemetryConstants.MaxCars);
        Assert.Equal(2026, TelemetryConstants.ExpectedPacketFormat);
    }
}