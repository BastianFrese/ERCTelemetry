using F1Game.UDP.Enums;
using ERCTelemetry.Core.OverlayProtocol;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.OverlayProtocol;

public class OverlayThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 29, 14, 0, 0, TimeSpan.Zero);

    private static readonly StandingsRow Tail = new(1, 0, "A", Team.McLaren, 1, 1, 100, 100, 0, 0,
        PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 0, true);

    [Fact]
    public void Player_stream_is_capped_at_30_hz()
    {
        var throttle = new OverlayThrottle();

        Assert.True(throttle.ShouldSendPlayer(T0));
        Assert.False(throttle.ShouldSendPlayer(T0.AddMilliseconds(20))); // < 33.3 ms window
        Assert.True(throttle.ShouldSendPlayer(T0.AddMilliseconds(40)));
    }

    [Fact]
    public void State_only_sends_when_overlay_content_changed()
    {
        var throttle = new OverlayThrottle();
        var unchanged = Snapshot(1, standing: null);

        Assert.True(throttle.ShouldSendState(unchanged, T0));
        // Same slow content (only FrameVersion differs) must never pass again,
        // no matter how much time elapses.
        Assert.False(throttle.ShouldSendState(Snapshot(2, standing: null), T0.AddSeconds(10)));
        Assert.False(throttle.ShouldSendState(Snapshot(3, standing: null), T0.AddMinutes(5)));
    }

    [Fact]
    public void State_is_capped_at_5_hz_on_change()
    {
        var throttle = new OverlayThrottle();

        Assert.True(throttle.ShouldSendState(Snapshot(1, standing: null), T0));
        // A real change arrives 100 ms later — inside the 200 ms window.
        Assert.False(throttle.ShouldSendState(Snapshot(2, standing: Tail), T0.AddMilliseconds(100)));
        // Same changed content once the window has passed — goes out.
        Assert.True(throttle.ShouldSendState(Snapshot(2, standing: Tail), T0.AddMilliseconds(210)));
        // And now it is the last accepted state — a fresh FrameVersion does not resend.
        Assert.False(throttle.ShouldSendState(Snapshot(9, standing: Tail), T0.AddMinutes(1)));
    }

    [Fact]
    public void ForceState_bypasses_window_and_change_check_once()
    {
        var throttle = new OverlayThrottle();
        var unchanged = Snapshot(1, standing: null);

        Assert.True(throttle.ShouldSendState(unchanged, T0));
        throttle.ForceState();
        Assert.True(throttle.ShouldSendState(Snapshot(1, standing: null), T0.AddMilliseconds(10)));
    }

    [Fact]
    public void Null_previous_snapshot_counts_as_changed()
    {
        var throttle = new OverlayThrottle();

        Assert.True(throttle.ShouldSendState(Snapshot(1, standing: null), T0.AddSeconds(30)));
    }

    [Fact]
    public void Map_stream_is_capped_at_10_hz()
    {
        var throttle = new OverlayThrottle();

        Assert.True(throttle.ShouldSendMap(T0));
        Assert.False(throttle.ShouldSendMap(T0.AddMilliseconds(50))); // < 100 ms window
        Assert.True(throttle.ShouldSendMap(T0.AddMilliseconds(110)));
    }

    [Fact]
    public void H2h_tally_change_triggers_state_send()
    {
        var throttle = new OverlayThrottle();
        var baseline = Snapshot(1, standing: null) with { H2h = new[] { new H2hTally(0, 1, 1, 0, 0, 0) } };

        Assert.True(throttle.ShouldSendState(baseline, T0));
        // Only the tally moved (car 0 won a second lap) — must still be a change.
        var changed = Snapshot(2, standing: null) with { H2h = new[] { new H2hTally(0, 1, 2, 0, 0, 0) } };
        Assert.True(throttle.ShouldSendState(changed, T0.AddSeconds(1)));
        // And once accepted, the same content stays quiet.
        Assert.False(throttle.ShouldSendState(
            Snapshot(3, standing: null) with { H2h = new[] { new H2hTally(0, 1, 2, 0, 0, 0) } }, T0.AddMinutes(1)));
    }

    [Fact]
    public void Tyre_only_change_triggers_state_send()
    {
        var throttle = new OverlayThrottle();
        var baseline = Snapshot(1, standing: null) with { Tyres = new[] { new TyreStatus(0, 10f, 0f) } };

        Assert.True(throttle.ShouldSendState(baseline, T0));
        // Only the wear number moved — without the Tyres clause this would never resend.
        var changed = Snapshot(2, standing: null) with { Tyres = new[] { new TyreStatus(0, 11f, 0f) } };
        Assert.True(throttle.ShouldSendState(changed, T0.AddSeconds(1)));
    }

    private static TelemetrySnapshot Snapshot(long frameVersion, StandingsRow? standing) => new(
        Meta: null,
        Drivers: Array.Empty<DriverEntry>(),
        Standings: standing is null ? Array.Empty<StandingsRow>() : new[] { standing },
        FinalResults: Array.Empty<FinalResultRow>(),
        RecentEvents: Array.Empty<RaceEventEntry>(),
        Player: null,
        Rival: null,
        FrameVersion: frameVersion,
        BuiltAtUtc: T0.AddMilliseconds(frameVersion));
}