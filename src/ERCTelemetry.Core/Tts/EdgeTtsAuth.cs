using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace ERCTelemetry.Core.Tts;

/// <summary>Edge-TTS DRM token + websocket endpoint. Mirrors the canonical edge-tts client
/// (rany2/edge-tts): since 2025 the readaloud endpoint requires a <c>Sec-MS-GEC</c> SHA-256
/// over the Windows file time (seconds since 1601, floored to 5 minutes, in 100 ns ticks)
/// concatenated with the trusted client token. Static + pure so the token logic is
/// unit-testable without a network; only the clock-skew probe needs the network.</summary>
public static class EdgeTtsAuth
{
    /// <summary>Well-known readaloud trusted client token — all community clients share it.</summary>
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    /// <summary>Chromium version the GEC token is pinned to (from edge-tts 7.x). An outdated
    /// version makes the endpoint answer 403 — the token is only accepted for a recent
    /// Chromium build.</summary>
    public const string SecMsGecVersion = "1-143.0.3650.75";

    private const long WindowsEpochOffset = 11644473600; // seconds between 1601-01-01 and 1970-01-01
    private const long FloorSeconds = 300;               // the token is stable for 5 minutes

    /// <summary>Seconds the local clock is ahead of the Edge-TTS server (0 = in sync).
    /// Adjusted once on a 403 via <see cref="TryAdjustClockSkew"/>; a drifted system clock
    /// would otherwise reject every token. Internal setter so tests can pin a skew.</summary>
    public static double ClockSkewSeconds { get; internal set; }

    /// <summary>Builds the readaloud websocket endpoint with all required query parameters.</summary>
    public static Uri BuildEndpoint(long unixSeconds, string connectionId) => new(
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
        $"?TrustedClientToken={TrustedClientToken}" +
        $"&Sec-MS-GEC={GenerateSecMsGec(unixSeconds)}" +
        $"&Sec-MS-GEC-Version={SecMsGecVersion}" +
        $"&ConnectionId={connectionId}");

    /// <summary>Stable 5-minute DRM token: uppercase SHA-256 hex over
    /// <c>"{(unixSeconds + skew + 11644473600) floored to 300 s × 10,000,000}{TrustedClientToken}"</c>.</summary>
    public static string GenerateSecMsGec(long unixSeconds)
    {
        long ticks = unixSeconds + (long)ClockSkewSeconds + WindowsEpochOffset;
        ticks -= ticks % FloorSeconds;
        ticks *= 10_000_000;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{ticks}{TrustedClientToken}")));
    }

    /// <summary>Probes the endpoint's <c>Date</c> header and stores the clock skew. Returns
    /// true when a meaningful skew (&gt; 1 s) was found and applied — the caller retries the
    /// synthesis with the corrected token. Never throws: a failed probe just reports false.</summary>
    public static bool TryAdjustClockSkew()
    {
        try
        {
            using var client = new HttpClient();
            using var response = client.GetAsync(
                "https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1").Result;
            if (response.Headers.Date is not { } serverDate)
            {
                return false;
            }

            var skew = (serverDate.UtcDateTime - DateTimeOffset.UtcNow).TotalSeconds;
            if (Math.Abs(skew) <= 1)
            {
                return false;
            }

            ClockSkewSeconds = skew;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
