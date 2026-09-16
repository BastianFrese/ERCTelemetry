using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>WeatherRadar: turns Session-packet forecast samples into the radar model —
/// ordered segments, rain summary, risk class and minutes-until-rain.</summary>
public sealed class WeatherRadarTests
{
    private static ForecastSample Sample(int minutes, byte rain, Weather weather = Weather.Clear) =>
        new(minutes, weather, rain, 20, 18);

    [Fact]
    public void Null_or_empty_forecast_yields_empty_model()
    {
        var empty = WeatherRadar.Build(null);
        Assert.Empty(empty.Segments);
        Assert.Equal(string.Empty, empty.Summary);
        Assert.Equal(RainRisk.Dry, empty.Risk);
        Assert.Equal(-1, empty.RainInMinutes);

        Assert.Equal(WeatherRadarModel.Empty, WeatherRadar.Build([]));
    }

    [Fact]
    public void Segments_are_ordered_by_time_offset()
    {
        var model = WeatherRadar.Build(
            [Sample(20, 0), Sample(5, 0), Sample(10, 0), Sample(0, 0)]);

        Assert.Equal([0, 5, 10, 20], model.Segments.Select(s => (int)s.TimeOffsetMinutes));
    }

    [Fact]
    public void Dry_forecast_reports_dry_summary_and_risk()
    {
        var model = WeatherRadar.Build(
            [Sample(0, 0), Sample(10, 0), Sample(20, 0), Sample(30, 0)]);

        Assert.Equal("Dry · ~30 min", model.Summary);
        Assert.Equal(RainRisk.Dry, model.Risk);
        Assert.Equal(-1, model.RainInMinutes);
        Assert.All(model.Segments, s => Assert.False(s.IsRain));
    }

    [Fact]
    public void Rain_summary_uses_first_rainy_sample()
    {
        var model = WeatherRadar.Build(
            [Sample(0, 0), Sample(10, 0), Sample(15, 65, Weather.LightRain), Sample(25, 90, Weather.HeavyRain)]);

        Assert.Equal("Rain 65% in ~15 min", model.Summary);
        Assert.Equal(15, model.RainInMinutes);
    }

    [Fact]
    public void Risk_classifies_by_max_rain_percent()
    {
        Assert.Equal(RainRisk.Dry, WeatherRadar.Build([Sample(0, 10)]).Risk);
        Assert.Equal(RainRisk.Light, WeatherRadar.Build([Sample(0, 30)]).Risk);
        Assert.Equal(RainRisk.Moderate, WeatherRadar.Build([Sample(0, 60)]).Risk);
        Assert.Equal(RainRisk.Heavy, WeatherRadar.Build([Sample(0, 85)]).Risk);
    }

    [Fact]
    public void IsRain_flags_samples_at_or_above_threshold()
    {
        var model = WeatherRadar.Build([Sample(0, 49), Sample(5, 50), Sample(10, 51)]);

        Assert.Equal([false, true, true], model.Segments.Select(s => s.IsRain));
    }

    [Fact]
    public void Segment_carries_weather_name()
    {
        var model = WeatherRadar.Build([Sample(5, 80, Weather.HeavyRain)]);

        Assert.Equal("HeavyRain", model.Segments[0].Weather);
    }
}
