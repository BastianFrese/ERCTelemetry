using Xunit;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.Core.Tests.Settings;

public sealed class HudPresetsTests
{
    [Fact]
    public void Apply_race_keeps_driving_and_race_info_widgets()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        var applied = HudPresets.Apply(settings, HudPresets.Race);

        // Assert
        Assert.True(applied.HudShowTiming);
        Assert.True(applied.HudShowDrive);
        Assert.True(applied.HudShowStatus);
        Assert.True(applied.HudShowRival);
        Assert.False(applied.HudShowMap);
        Assert.True(applied.HudShowWeather);
        Assert.True(applied.HudShowRace);
        Assert.Equal(HudPresets.Race, applied.HudPreset);
    }

    [Fact]
    public void Apply_practice_turns_off_race_day_widgets_and_turns_on_the_map()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        var applied = HudPresets.Apply(settings, HudPresets.Practice);

        // Assert
        Assert.False(applied.HudShowRace);   // no pit window/flags on a practice lap
        Assert.False(applied.HudShowRival);  // teammate panel less relevant
        Assert.True(applied.HudShowMap);     // track learning
        Assert.Equal(HudPresets.Practice, applied.HudPreset);
    }

    [Fact]
    public void Apply_compact_keeps_only_timing_and_drive()
    {
        // Arrange
        var settings = new AppSettings() with { HudShowRace = true, HudShowStatus = true };

        // Act
        var applied = HudPresets.Apply(settings, HudPresets.Compact);

        // Assert
        Assert.True(applied.HudShowTiming);
        Assert.True(applied.HudShowDrive);
        Assert.False(applied.HudShowStatus);
        Assert.False(applied.HudShowRace);
        Assert.Equal(HudPresets.Compact, applied.HudPreset);
    }

    [Fact]
    public void Apply_resets_widget_positions_to_the_auto_stack()
    {
        // Arrange
        var positions = new Dictionary<string, double[]> { ["timing"] = [100, 200] };
        var settings = new AppSettings() with { HudWidgetPositions = positions };

        // Act
        var applied = HudPresets.Apply(settings, HudPresets.Race);

        // Assert
        Assert.Null(applied.HudWidgetPositions);
    }

    [Fact]
    public void Unknown_preset_names_return_the_settings_unchanged()
    {
        // Arrange
        var settings = new AppSettings() with { HudShowMap = true };

        // Act
        var applied = HudPresets.Apply(settings, "Turbo");

        // Assert
        Assert.True(applied.HudShowMap);
        Assert.Null(applied.HudPreset);
    }
}