namespace ERCTelemetry.Core.Clips;

/// <summary>Human-readable labels for the F1Game.UDP collision severity byte
/// (0 = gering, 1 = mittel, 2 = hoch). Shared by the race-control event text and the
/// History clips table.</summary>
public static class CollisionSeverity
{
    public static string Label(int severity) => severity switch
    {
        0 => "gering",
        1 => "mittel",
        _ => "hoch",
    };
}
