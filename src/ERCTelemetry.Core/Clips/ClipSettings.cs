namespace ERCTelemetry.Core.Clips;

/// <summary>Collision-clip recording settings (screen capture + FFmpeg encode).
/// Immutable record — change values via <c>with</c> and hand the new instance to the
/// settings store. MinSeverity mirrors F1Game.UDP's collision severity byte
/// (0 = gering, 1 = mittel, 2 = hoch).</summary>
public sealed record ClipSettings(
    bool Enabled = false,
    bool OnlyPlayerCollisions = true,
    int MinSeverity = 1,        // 0=gering, 1=mittel, 2=hoch
    int PreRollSeconds = 10,
    int PostRollSeconds = 10,
    int Fps = 60,
    string? AudioDeviceName = null,
    int MaxWidth = 1280)        // clip max width in px; aspect ratio is preserved
{
    /// <summary>Returns a copy with out-of-range values clamped to their valid ranges
    /// (a hand-edited settings.json can contain anything).</summary>
    public ClipSettings Sanitized() => this with
    {
        MinSeverity = Math.Clamp(MinSeverity, 0, 2),
        PreRollSeconds = Math.Clamp(PreRollSeconds, 1, 60),
        PostRollSeconds = Math.Clamp(PostRollSeconds, 1, 60),
        Fps = Math.Clamp(Fps, 1, 60),
        MaxWidth = Math.Clamp(MaxWidth, 640, 7680),
    };
}
