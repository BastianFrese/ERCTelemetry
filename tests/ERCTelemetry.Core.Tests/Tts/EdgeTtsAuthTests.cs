using ERCTelemetry.Core.Tts;
using Xunit;

namespace ERCTelemetry.Core.Tests.Tts;

/// <summary>Sec-MS-GEC token + endpoint-URL construction for the Edge-TTS readaloud
/// websocket. The two fixed vectors were independently computed from the documented
/// algorithm (Windows file time in 100 ns, floored to 5 min, SHA-256 hex uppercase)
/// and match drm.py from the canonical edge-tts client.</summary>
public sealed class EdgeTtsAuthTests
{
    [Theory]
    [InlineData(1759999999, "70AED27457006C255086B4F079B9FE44A4D10827C4AFDFC3C45583B6F40D7DDF")]
    [InlineData(1759999000, "60821120384569959523D3B29A263C8E176A723DE98EBC661B916871D93A83C7")]
    public void GenerateSecMsGec_matches_reference_vectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, EdgeTtsAuth.GenerateSecMsGec(unixSeconds));
    }

    [Fact]
    public void GenerateSecMsGec_is_stable_within_the_five_minute_window()
    {
        // 1_000_000_000 and 1_000_000_100 floor to the same 5-minute boundary.
        Assert.Equal(
            EdgeTtsAuth.GenerateSecMsGec(1_000_000_000),
            EdgeTtsAuth.GenerateSecMsGec(1_000_000_100));
    }

    [Fact]
    public void BuildEndpoint_contains_all_query_parameters()
    {
        const long unixSeconds = 1_759_999_999;
        const string connectionId = "abcdef0123456789abcdef0123456789";

        var uri = EdgeTtsAuth.BuildEndpoint(unixSeconds, connectionId);

        Assert.StartsWith("wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?", uri.ToString());
        var query = uri.Query;
        Assert.Contains($"TrustedClientToken={EdgeTtsAuth.TrustedClientToken}", query);
        Assert.Contains($"Sec-MS-GEC-Version={EdgeTtsAuth.SecMsGecVersion}", query);
        Assert.Contains($"Sec-MS-GEC={EdgeTtsAuth.GenerateSecMsGec(unixSeconds)}", query);
        Assert.Contains($"ConnectionId={connectionId}", query);
    }

    [Fact]
    public void GenerateSecMsGec_applies_clock_skew()
    {
        // A +60 s skew must produce the same token as an unskewed clock 60 s later —
        // the retry after a 403 rebuilds the token with the corrected time.
        const long unixSeconds = 1_759_999_999;
        var expected = EdgeTtsAuth.GenerateSecMsGec(unixSeconds + 60);

        EdgeTtsAuth.ClockSkewSeconds = 60;
        try
        {
            Assert.Equal(expected, EdgeTtsAuth.GenerateSecMsGec(unixSeconds));
        }
        finally
        {
            EdgeTtsAuth.ClockSkewSeconds = 0;
        }
    }
}
