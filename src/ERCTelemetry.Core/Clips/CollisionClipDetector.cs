using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Clips;

/// <summary>Pure decision logic for whether a collision event should trigger a clip.
/// Kept in Core so it is headless-testable; the App service applies it to live events.</summary>
public static class CollisionClipDetector
{
    /// <summary>True when the event is a collision that meets the settings: severity at
    /// or above the minimum, and (when OnlyPlayerCollisions) the player is one of the
    /// two cars involved. AI-vs-AI collisions are filtered out by the player check.</summary>
    public static bool ShouldRecord(RaceEventEntry entry, byte playerCarIndex, ClipSettings settings)
    {
        if (entry.Type != "Collision" || entry.CarIndex is not { } carIndex)
        {
            return false;
        }

        if (entry.DetailValue < settings.MinSeverity)
        {
            return false;
        }

        if (settings.OnlyPlayerCollisions &&
            carIndex != playerCarIndex && entry.SecondCarIndex != playerCarIndex)
        {
            return false;
        }

        return true;
    }
}
