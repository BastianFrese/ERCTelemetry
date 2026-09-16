using ERCTelemetry.Core.Share;
using Xunit;

namespace ERCTelemetry.Core.Tests.Share;

/// <summary>SharePageRenderer: the public session page contains the header facts, the
/// results table and one &lt;video&gt; per clip — and escapes every user-supplied string.</summary>
public sealed class SharePageRendererTests
{
    private static ShareManifest Sample() => new(
        SessionUid: 42_000,
        Track: "Spa",
        SessionType: "Race",
        StartUtc: new DateTimeOffset(2026, 9, 7, 18, 30, 0, TimeSpan.Zero),
        DurationSeconds: 3720,
        PlayerCarIndex: 3,
        Results:
        [
            new ShareResultRow(1, "Player One", "McLaren", 44, 90_123, 3600.5, "Finished", 25f),
            new ShareResultRow(2, "Rival", "Red Bull Racing", 44, 90_500, 3601, "Finished", 18f),
        ],
        Clips:
        [
            new ShareClip("clip-1.mp4", 7, 3, 5, "Player One", "Rival", 2, 12.5),
        ]);

    [Fact]
    public void Render_contains_track_results_and_video()
    {
        var html = SharePageRenderer.Render(Sample(), "https://telemetrie.erdi-erc.de");

        Assert.Contains("Spa", html);
        Assert.Contains("Player One", html);
        Assert.Contains("McLaren", html);
        Assert.Contains("<video", html);
        Assert.Contains("https://telemetrie.erdi-erc.de/s/42000/clips/clip-1.mp4", html);
    }

    [Fact]
    public void Render_escapes_user_data()
    {
        var manifest = Sample() with
        {
            Track = "<script>alert(1)</script>",
            Results =
            [
                new ShareResultRow(1, "<img src=x onerror=alert(1)>", "Team\"X", 44, 90_123, 3600.5, "Finished", 25f),
            ],
        };

        var html = SharePageRenderer.Render(manifest, "https://telemetrie.erdi-erc.de");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("Team&quot;X", html);
    }
}
