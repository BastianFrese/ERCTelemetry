using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Shared Canvas chart helpers for the race report tab: lap-time line, stint
/// bars and the trace duel. Theme brushes resolve at draw time (ThemeBrush) so charts
/// repaint correctly after a Light/Dark switch.</summary>
public static class ReportCharts
{
    public static Brush ThemeBrush(string key, Color fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>Draws one driver's lap times as a polyline. X = lap number, Y = lap time
    /// (ms); laps with 0 time (in-lap without timing) are skipped.</summary>
    public static void DrawLapTimes(Canvas canvas, LapPointSeries series, Brush accent)
    {
        canvas.Children.Clear();
        var points = series.Points.Where(p => p.LapTimeMs > 0).ToList();
        if (points.Count == 0)
        {
            return;
        }

        var maxLap = points.Max(p => p.LapNumber);
        var minMs = points.Min(p => (double)p.LapTimeMs);
        var maxMs = points.Max(p => (double)p.LapTimeMs);
        var w = Math.Max(canvas.ActualWidth - 40, 10);
        var h = Math.Max(canvas.ActualHeight - 30, 10);
        double X(int lap) => 30 + (lap - 1) * w / Math.Max(maxLap - 1, 1);
        double Y(uint ms) => 15 + (1 - (ms - minMs) / Math.Max(maxMs - minMs, 1)) * h;

        canvas.Children.Add(new Polyline
        {
            Points = new PointCollection(points.Select(p => new Point(X(p.LapNumber), Y(p.LapTimeMs)))),
            Stroke = accent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    /// <summary>Draws two speed traces over the shared distance axis (0..max track length).
    /// Sampled distance-based, so the X axis is lap distance in metres.</summary>
    public static void DrawTraceDuel(Canvas canvas, LapTrace a, LapTrace b, Brush brushA, Brush brushB)
    {
        canvas.Children.Clear();
        var maxDistance = Math.Max(
            a.Samples.Count > 0 ? a.Samples[^1].LapDistance : 0f,
            b.Samples.Count > 0 ? b.Samples[^1].LapDistance : 0f);
        var maxSpeed = Math.Max(
            a.Samples.Count > 0 ? a.Samples.Max(s => s.Speed) : 0,
            b.Samples.Count > 0 ? b.Samples.Max(s => s.Speed) : 0);
        if (maxDistance <= 0 || maxSpeed <= 0)
        {
            return;
        }

        PlotTrace(canvas, a.Samples, maxDistance, maxSpeed, brushA);
        PlotTrace(canvas, b.Samples, maxDistance, maxSpeed, brushB);
    }

    private static void PlotTrace(
        Canvas canvas,
        IReadOnlyList<ERCTelemetry.Core.Analysis.LapTraceSample> samples,
        float maxDistance,
        int maxSpeed,
        Brush brush)
    {
        var w = Math.Max(canvas.ActualWidth - 30, 10);
        var h = Math.Max(canvas.ActualHeight - 20, 10);
        var points = new PointCollection(samples.Count);
        foreach (var s in samples)
        {
            points.Add(new Point(10 + s.LapDistance / maxDistance * w, 10 + (1 - s.Speed / (float)maxSpeed) * h));
        }

        canvas.Children.Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = 1.5 });
    }
}