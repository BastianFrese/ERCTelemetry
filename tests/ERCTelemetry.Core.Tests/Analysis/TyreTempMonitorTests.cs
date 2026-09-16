using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>TyreTempMonitor: warns when the front tyres or the brakes overheat, with a
/// time-based cooldown so it cannot repeat every second. Deterministic (Layer 1) — no LLM.
/// The clock is injected so the cooldown is testable.</summary>
public sealed class TyreTempMonitorTests
{
    [Fact]
    public void Front_tyre_overheating_fires()
    {
        var monitor = new TyreTempMonitor();
        var snapshot = Snapshot(surfaceTemp: [110f, 100f, 90f, 90f], brakeTemp: [700f, 700f, 600f, 600f]);

        var alert = monitor.Evaluate(snapshot);

        Assert.NotNull(alert);
        Assert.Contains("Vorderreifen überhitzt", alert.Text);
        Assert.Contains("110", alert.Text);
    }

    [Fact]
    public void Brake_overheating_fires()
    {
        var monitor = new TyreTempMonitor();
        var snapshot = Snapshot(surfaceTemp: [100f, 100f, 90f, 90f], brakeTemp: [850f, 800f, 700f, 700f]);

        var alert = monitor.Evaluate(snapshot);

        Assert.NotNull(alert);
        Assert.Contains("Bremsen überhitzt", alert.Text);
    }

    [Fact]
    public void No_alert_at_normal_temps()
    {
        var monitor = new TyreTempMonitor();
        var snapshot = Snapshot(surfaceTemp: [100f, 100f, 90f, 90f], brakeTemp: [700f, 700f, 600f, 600f]);

        var alert = monitor.Evaluate(snapshot);

        Assert.Null(alert);
    }

    [Fact]
    public void Cooldown_suppresses_repeat()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new TyreTempMonitor(() => now);
        var snapshot = Snapshot(surfaceTemp: [110f, 100f, 90f, 90f], brakeTemp: [700f, 700f, 600f, 600f]);

        var first = monitor.Evaluate(snapshot);
        now = now.AddSeconds(5);
        var duringCooldown = monitor.Evaluate(snapshot);
        now = now.AddSeconds(20);
        var afterCooldown = monitor.Evaluate(snapshot);

        Assert.NotNull(first);
        Assert.Null(duringCooldown);
        Assert.NotNull(afterCooldown);
    }

    [Fact]
    public void No_alert_without_wheels()
    {
        var monitor = new TyreTempMonitor();
        var snapshot = new TelemetrySnapshot(Meta(), [], [], [], [], null, null, 1, DateTimeOffset.UtcNow);

        var alert = monitor.Evaluate(snapshot);

        Assert.Null(alert);
    }

    private static SessionMeta Meta() =>
        new(1, SessionType.Race, Track.Bahrain, 30, 5412, true, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);

    private static TelemetrySnapshot Snapshot(float[] surfaceTemp, float[] brakeTemp)
    {
        var wheels = new WheelState([30f, 30f, 30f, 30f], surfaceTemp, brakeTemp);
        var player = new PlayerFrame(0, "Alice", 300, 8, 0.5f, 0f, 12_000, 3_000_000,
            ErsDeployMode.Medium, 50f, 20f, ActualCompound.F1C3, 1, true, false, Wheels: wheels);
        return new TelemetrySnapshot(Meta(), [], [], [], [], player, null, 1, DateTimeOffset.UtcNow);
    }
}
