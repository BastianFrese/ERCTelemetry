using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.VoiceAlerts;
using Xunit;

namespace ERCTelemetry.Core.Tests.VoiceAlerts;

/// <summary>ProximitySpotter: AC-style directional calls (left/right/behind/ahead),
/// counts, distances and "clear" when a zone empties. The spotter is stateful with a
/// 2-check debounce, so tests that expect an announcement feed the same frame twice.</summary>
public sealed class ProximitySpotterTests
{
    private const byte Player = 0;

    [Fact]
    public void Car_left_announces_direction()
    {
        var spotter = new ProximitySpotter();

        var alerts = UpdateTwice(spotter, FrameWithCar(carX: 8f, carZ: 4f));

        Assert.Contains(alerts, a => a == "Auto links");
    }

    [Fact]
    public void Car_right_announces_direction()
    {
        var spotter = new ProximitySpotter();

        var alerts = UpdateTwice(spotter, FrameWithCar(carX: -8f, carZ: -4f));

        Assert.Contains(alerts, a => a == "Auto rechts");
    }

    [Fact]
    public void Car_behind_announces_distance()
    {
        var spotter = new ProximitySpotter();

        var alerts = UpdateTwice(spotter, FrameWithCar(carX: 0f, carZ: -18f));

        Assert.Contains(alerts, a => a == "Auto hinter dir, 20 Meter");
    }

    [Fact]
    public void Car_ahead_announces_distance()
    {
        var spotter = new ProximitySpotter();

        var alerts = UpdateTwice(spotter, FrameWithCar(carX: 0f, carZ: 18f));

        Assert.Contains(alerts, a => a == "Auto vor dir, 20 Meter");
    }

    [Fact]
    public void Two_cars_behind_announces_count_and_distance()
    {
        var spotter = new ProximitySpotter();
        var frame = FrameWithCar(carX: 0f, carZ: -18f);
        frame.X[2] = 3f; frame.Z[2] = -15f;

        var alerts = UpdateTwice(spotter, frame);

        Assert.Contains(alerts, a => a == "2 Autos hinter dir, 15 Meter");
    }

    [Fact]
    public void Car_beyond_range_is_silent()
    {
        var spotter = new ProximitySpotter();

        var alerts = UpdateTwice(spotter, FrameWithCar(carX: 0f, carZ: -30f));

        Assert.Empty(alerts);
    }

    [Fact]
    public void Zone_empties_announces_clear()
    {
        var spotter = new ProximitySpotter();
        var withCar = FrameWithCar(carX: 8f, carZ: 4f);
        UpdateTwice(spotter, withCar);

        var alerts = spotter.Update(FrameWithCar(carX: 0f, carZ: 0f), Player, []);

        Assert.Contains(alerts, a => a == "Links frei");
    }

    [Fact]
    public void Reset_clears_zone_state_so_no_clear_is_announced()
    {
        // The App resets the spotter when the race ends — a finished race must not
        // announce stale "clear" calls for zones that were confirmed before the reset.
        var spotter = new ProximitySpotter();
        UpdateTwice(spotter, FrameWithCar(carX: 8f, carZ: 4f)); // "Auto links" confirmed

        spotter.Reset();

        var alerts = spotter.Update(FrameWithCar(carX: 0f, carZ: 0f), Player, []);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Debounce_requires_two_consecutive_checks()
    {
        var spotter = new ProximitySpotter();
        var frame = FrameWithCar(carX: 8f, carZ: 4f);

        var first = spotter.Update(frame, Player, []);
        var second = spotter.Update(frame, Player, []);

        Assert.Empty(first);
        Assert.Contains(second, a => a == "Auto links");
    }

    [Fact]
    public void Closing_car_reannounces_at_distance_steps()
    {
        var spotter = new ProximitySpotter();
        UpdateTwice(spotter, FrameWithCar(carX: 0f, carZ: -18f)); // "20 Meter", arms 10

        var alerts = spotter.Update(FrameWithCar(carX: 0f, carZ: -8f), Player, []);

        Assert.Contains(alerts, a => a == "Auto hinter dir, 10 Meter");
    }

    [Fact]
    public void English_language_announces_english_texts()
    {
        var left = UpdateTwice(new ProximitySpotter { Language = VoiceLanguage.English }, FrameWithCar(carX: 8f, carZ: 4f));
        var behind = UpdateTwice(new ProximitySpotter { Language = VoiceLanguage.English }, FrameWithCar(carX: 0f, carZ: -18f));

        var clearSpotter = new ProximitySpotter { Language = VoiceLanguage.English };
        UpdateTwice(clearSpotter, FrameWithCar(carX: 8f, carZ: 4f));
        var clear = clearSpotter.Update(FrameWithCar(carX: 0f, carZ: 0f), Player, []);

        Assert.Contains(left, a => a == "Car left");
        Assert.Contains(behind, a => a == "Car behind, 20 metres");
        Assert.Contains(clear, a => a == "Clear left");
    }

    [Fact]
    public void No_positions_returns_empty()
    {
        var spotter = new ProximitySpotter();

        var alerts = spotter.Update(null, Player, []);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Player_car_is_skipped()
    {
        var spotter = new ProximitySpotter();
        var frame = FrameWithCar(carX: 8f, carZ: 4f, carIndex: Player);

        var alerts = UpdateTwice(spotter, frame);

        Assert.Empty(alerts);
    }

    /// <summary>Player at (0,0) facing +Z; world +X = left (lateral −), world −Z = behind
    /// (longitudinal −). One car at the given world position.</summary>
    private static MotionFrame FrameWithCar(float carX, float carZ, byte carIndex = 1)
    {
        var x = new float[22];
        var z = new float[22];
        var fx = new float[22];
        var fz = new float[22];
        fx[Player] = 0f;
        fz[Player] = 1f;
        x[carIndex] = carX;
        z[carIndex] = carZ;
        return new MotionFrame(22, x, z, fx, fz);
    }

    private static IReadOnlyList<string> UpdateTwice(ProximitySpotter spotter, MotionFrame frame)
    {
        spotter.Update(frame, Player, []);
        return spotter.Update(frame, Player, []);
    }
}
