using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Rain-forecast model for the Wetter-Radar: turns the Session packet's forecast
/// samples into a compact visual model (segments + risk + summary). Pure and testable —
/// the HUD and dashboard render the model, they don't re-derive it.</summary>
public static class WeatherRadar
{
    /// <summary>Rain probability (%) at which a sample counts as "rain" for summary/risk.</summary>
    public const byte RainThreshold = 50;

    public static WeatherRadarModel Build(IReadOnlyList<ForecastSample>? forecast)
    {
        if (forecast is null || forecast.Count == 0)
        {
            return WeatherRadarModel.Empty;
        }

        var segments = forecast
            .OrderBy(f => f.TimeOffsetMinutes)
            .Select(f => new RadarSegment(
                (byte)Math.Min(f.TimeOffsetMinutes, 255),
                f.RainPercent,
                f.Weather.ToString(),
                f.RainPercent >= RainThreshold))
            .ToList();

        var firstRain = segments.FirstOrDefault(s => s.IsRain);
        var maxRain = segments.Count > 0 ? segments.Max(s => s.RainPercent) : 0;
        var risk = maxRain switch
        {
            >= 80 => RainRisk.Heavy,
            >= RainThreshold => RainRisk.Moderate,
            >= 20 => RainRisk.Light,
            _ => RainRisk.Dry,
        };

        var summary = firstRain is not null
            ? $"Rain {firstRain.RainPercent}% in ~{firstRain.TimeOffsetMinutes} min"
            : $"Dry · ~{segments[^1].TimeOffsetMinutes} min";

        return new WeatherRadarModel(segments, summary, risk, firstRain?.TimeOffsetMinutes ?? -1);
    }
}

/// <summary>How much rain the forecast horizon expects (drives the radar bar's accent).</summary>
public enum RainRisk
{
    Dry,
    Light,
    Moderate,
    Heavy,
}

/// <summary>One forecast sample as a radar-bar segment (time offset + rain probability).</summary>
public sealed record RadarSegment(byte TimeOffsetMinutes, byte RainPercent, string Weather, bool IsRain);

/// <summary>The Wetter-Radar model: ordered segments plus a one-line summary and risk class.</summary>
public sealed record WeatherRadarModel(
    IReadOnlyList<RadarSegment> Segments,
    string Summary,
    RainRisk Risk,
    int RainInMinutes)
{
    public static readonly WeatherRadarModel Empty = new([], string.Empty, RainRisk.Dry, -1);
}
