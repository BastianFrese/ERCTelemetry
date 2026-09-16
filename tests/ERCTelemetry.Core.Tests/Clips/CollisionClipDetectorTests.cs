using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Clips;

/// <summary>CollisionClipDetector: which collision events trigger a clip, given the
/// player index and the clip settings.</summary>
public class CollisionClipDetectorTests
{
    private static readonly ClipSettings Defaults = new();

    private static RaceEventEntry Collision(byte car, byte? second, int severity) =>
        new(DateTimeOffset.UtcNow, "Collision", car, "collision",
            SecondCarIndex: second, DetailValue: severity);

    [Fact]
    public void Player_as_first_car_records()
    {
        var entry = Collision(car: 3, second: 7, severity: 1);

        Assert.True(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, Defaults));
    }

    [Fact]
    public void Player_as_second_car_records()
    {
        var entry = Collision(car: 7, second: 3, severity: 2);

        Assert.True(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, Defaults));
    }

    [Fact]
    public void Player_collision_with_environment_records()
    {
        var entry = Collision(car: 3, second: null, severity: 1);

        Assert.True(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, Defaults));
    }

    [Fact]
    public void Ai_vs_ai_is_filtered_when_only_player_collisions()
    {
        var entry = Collision(car: 5, second: 9, severity: 2);

        Assert.False(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, Defaults));
    }

    [Fact]
    public void Ai_vs_ai_records_when_only_player_collisions_is_off()
    {
        var entry = Collision(car: 5, second: 9, severity: 2);
        var settings = Defaults with { OnlyPlayerCollisions = false };

        Assert.True(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, settings));
    }

    [Fact]
    public void Severity_below_minimum_is_filtered()
    {
        var entry = Collision(car: 3, second: 7, severity: 0);
        var settings = Defaults with { MinSeverity = 1 };

        Assert.False(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, settings));
    }

    [Fact]
    public void Non_collision_event_is_never_recorded()
    {
        var entry = new RaceEventEntry(DateTimeOffset.UtcNow, "Penalty", 3, "penalty");

        Assert.False(CollisionClipDetector.ShouldRecord(entry, playerCarIndex: 3, Defaults));
    }
}
