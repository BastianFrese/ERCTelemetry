using System.Globalization;
using System.Windows;
using System.Windows.Data;
using F1Game.UDP.Enums;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Null → Collapsed, non-null → Visible (for the format warning banner).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Recording state → button label.</summary>
public sealed class BoolToRecordingTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Stop recording" : "Record";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Lap time in milliseconds → "m:ss.fff"; 0 → "—".</summary>
public sealed class LapTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not uint milliseconds || milliseconds == 0)
        {
            return "—";
        }

        return $"{milliseconds / 60000}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sector time in milliseconds → "ss.mmm" below 60 s, otherwise "m:ss.mmm";
/// 0 → "—". Separate from <see cref="LapTimeConverter"/> because sectors are ushort.</summary>
public sealed class SectorTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ushort milliseconds || milliseconds == 0)
        {
            return "—";
        }

        return milliseconds < 60000
            ? $"{milliseconds / 1000.0:0.000}"
            : $"{milliseconds / 60000}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Gap in milliseconds → "+s.fff" / "-s.fff". Zero renders as "+0.000" (the
/// converter has no position context, so it never claims "Leader").</summary>
public sealed class GapConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int milliseconds)
        {
            return "—";
        }

        return $"{(milliseconds > 0 ? "+" : "")}{milliseconds / 1000.0:0.000}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>PitStatus enum → short column text ("", "Pit", "Pit lane"); unknown → raw name.</summary>
public sealed class PitStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PitStatus.None => string.Empty,
            PitStatus.Pitting => "Pit",
            PitStatus.InPitArea => "Pit lane",
            var other => other?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ActualCompound enum → display text ("C5", "Inter", "Wet", "F2 Soft"…).</summary>
public sealed class TyreCompoundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ActualCompound.F1C0 => "C0",
            ActualCompound.F1C1 => "C1",
            ActualCompound.F1C2 => "C2",
            ActualCompound.F1C3 => "C3",
            ActualCompound.F1C4 => "C4",
            ActualCompound.F1C5 => "C5",
            ActualCompound.F1C6 => "C6",
            ActualCompound.F1Inter => "Inter",
            ActualCompound.F1Wet => "Wet",
            ActualCompound.F1ClassicDry => "Classic Dry",
            ActualCompound.F1ClassicWet => "Classic Wet",
            ActualCompound.F2SuperSoft => "F2 SuperSoft",
            ActualCompound.F2Soft => "F2 Soft",
            ActualCompound.F2Medium => "F2 Medium",
            ActualCompound.F2Hard => "F2 Hard",
            ActualCompound.F2Wet => "F2 Wet",
            var other => other?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Progress 0..1 → pixel width for the gauge bars. ConverterParameter is the
/// track width in pixels. Values above 1 are treated as percent (0..100) so the same
/// converter serves both the 0..1 throttle/brake and the 0..100 ERS readout; the
/// result is clamped so a transient out-of-range frame can never overflow the track.
/// Stateless — runs at telemetry rate.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double progress || parameter is not string trackText
            || !double.TryParse(trackText, NumberStyles.Float, CultureInfo.InvariantCulture, out var trackWidth))
        {
            return 0d;
        }

        var fraction = progress > 1 ? progress / 100.0 : progress;
        return Math.Clamp(fraction, 0, 1) * trackWidth;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Session seconds (ushort) → "m:ss"; 0 → "–" (no session / race laps mode).</summary>
public sealed class SessionSecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ushort seconds && seconds > 0
            ? $"{seconds / 60}:{seconds % 60:00}"
            : "–";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Tyre surface temperature (°C) → quadrant fill: below 90 cool blue,
/// 90–110 optimal green, 110–130 warn yellow, above 130 critical red; null/unknown →
/// the idle surface color. Reads the theme brushes at convert time so a live retint
/// is picked up without restart.</summary>
public sealed class TyreTempToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            float temp when temp < 90 => Brush("AppLink"),
            float temp when temp <= 110 => Brush("AppStatusGood"),
            float temp when temp <= 130 => Brush("AppStatusWarn"),
            float => Brush("AppStatusCritical"),
            _ => Brush("AppSurfaceAlt"),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static System.Windows.Media.Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Transparent;
}

/// <summary>F1 team enum → brand brush (see <see cref="TeamColors"/>); unknown teams
/// fall back to the neutral gray. Cached brushes — the map never changes at runtime.</summary>
public sealed class TeamToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Team team ? TeamColors.For(team) : TeamColors.Fallback;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → its inverse (e.g. disable the card list while a detail is open).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : false;
}

/// <summary>bool → Visibility, inverted: true → Collapsed, false → Visible (hides the
/// session-card list while the detail view is open).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}