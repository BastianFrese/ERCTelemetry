using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Analysis;

/// <summary>One spoken tyre/brake temperature warning.</summary>
public sealed record TyreTempAlert(string Text);

/// <summary>Warns when the front tyres or the brakes overheat — the classic beginner
/// mistakes (blocking too much, braking too late and hard). Deterministic (Layer 1), no
/// LLM. Stateful (single consumer): a time-based cooldown stops it from repeating every
/// second while the temperature stays high. The clock is injectable for headless tests.</summary>
public sealed class TyreTempMonitor
{
    /// <summary>Front-tyre surface temperature (°C) above which the tyres are overheating.</summary>
    public const float FrontSurfaceTempThresholdC = 105f;

    /// <summary>Brake temperature (°C) above which the brakes are overheating.</summary>
    public const float BrakeTempThresholdC = 800f;

    /// <summary>Seconds to stay silent after an alert, even while the temperature stays high.</summary>
    public const int CooldownSeconds = 20;

    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _lastAlertAt;

    public TyreTempMonitor(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Returns a warning to speak, or null to stay silent.</summary>
    public TyreTempAlert? Evaluate(TelemetrySnapshot snapshot)
    {
        var wheels = snapshot.Player?.Wheels;
        if (wheels is null || wheels.SurfaceTemp.Length < 2 || wheels.BrakeTemp.Length == 0)
        {
            return null;
        }

        // Front wheels are indices 0 (FL) and 1 (FR).
        var frontSurface = Math.Max(wheels.SurfaceTemp[0], wheels.SurfaceTemp[1]);
        var maxBrake = wheels.BrakeTemp.Max();

        if (frontSurface <= FrontSurfaceTempThresholdC && maxBrake <= BrakeTempThresholdC)
        {
            return null;
        }

        var now = _clock();
        if (now - _lastAlertAt < TimeSpan.FromSeconds(CooldownSeconds))
        {
            return null;
        }

        _lastAlertAt = now;

        if (frontSurface > FrontSurfaceTempThresholdC)
        {
            return new TyreTempAlert(
                $"Vorderreifen überhitzt ({frontSurface:0} Grad) — du blockierst zu viel.");
        }

        return new TyreTempAlert(
            $"Bremsen überhitzt ({maxBrake:0} Grad) — du bremst zu spät und zu hart.");
    }
}
