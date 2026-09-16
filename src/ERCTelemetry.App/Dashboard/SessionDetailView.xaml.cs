using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Session detail UI: back navigation, exports, results/pace tables and the
/// position + speed-trace charts drawn from <see cref="HistoryViewModel"/> state on
/// Canvas. The card list lives in the SessionListView partial view.</summary>
public partial class SessionDetailView : UserControl
{
    private const double ChartWidth = 800;
    private const double ChartHeight = 114;
    private const double MarginX = 30;
    private const double MarginY = 10;
    private const double PedalStripHeight = 26;
    private HistoryViewModel? _vm;

    public SessionDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelChanged;
            }

            _vm = DataContext as HistoryViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "LapCount" or "SelectedSession")
        {
            Dispatcher.BeginInvoke(RedrawChart);
        }

        if (e.PropertyName is "TraceCurveA" or "TraceCurveB")
        {
            Dispatcher.BeginInvoke(RedrawTraceChart);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Vm?.CloseDetail();

    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    private void ExportJson_Click(object sender, RoutedEventArgs e) => Vm?.ExportJson();

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => Vm?.ExportCsv();

    private void OpenReport_Click(object sender, RoutedEventArgs e) => Vm?.RequestOpenReport();

    private async void ShareSession_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.ShareSessionAsync();
        }
    }

    private void ExportLapsCsv_Click(object sender, RoutedEventArgs e) => Vm?.ExportLapsCsv();

    private void Compare_Click(object sender, RoutedEventArgs e) => Vm?.ComputeComparison();

    private void PlayClip_Click(object sender, RoutedEventArgs e) =>
        Vm?.PlayClip((HistoryViewModel.ClipRow)((FrameworkElement)sender).DataContext);

    private void OpenClipFolder_Click(object sender, RoutedEventArgs e) => Vm?.OpenClipFolder();

    // ---- speed-trace comparison chart -------------------------------------------

    /// <summary>Speed-trace comparison chart: both laps' speed-over-distance curves,
    /// x = % of the lap (shared axis), y = speed. Lap A in accent red, lap B in gold.</summary>
    private void RedrawTraceChart()
    {
        TraceCanvas.Children.Clear();
        if (_vm is null)
        {
            return;
        }

        var traceA = _vm.TraceCurveA;
        var traceB = _vm.TraceCurveB;
        if (traceA is null || traceB is null)
        {
            return;
        }

        var maxSpeed = Math.Max(traceA.Samples.Max(s => s.Speed), traceB.Samples.Max(s => s.Speed));
        var yMax = (int)Math.Ceiling(maxSpeed / 50.0) * 50;
        var refLenA = ReferenceLength(traceA);
        var refLenB = ReferenceLength(traceB);

        PlotTrace(traceA, refLenA, yMax, ThemeBrush("AppAccent", Color.FromRgb(0xE1, 0x06, 0x00)));
        PlotTrace(traceB, refLenB, yMax, ThemeBrush("AppStatusWarn", Color.FromRgb(0xF3, 0xD0, 0x2F)));

        PlotPedals(traceA, refLenA, 1.0);
        PlotPedals(traceB, refLenB, 0.7);

        DrawTraceAxis(yMax);
        DrawTraceLegend();
    }

    /// <summary>Plots one lap's speed curve: x = % of the lap (shared axis so different
    /// track lengths still line up), y = speed (0..yMax).</summary>
    private void PlotTrace(LapTrace trace, float refLength, int yMax, Brush stroke)
    {
        var plotWidth = ChartWidth - 2 * MarginX;
        var plotHeight = ChartHeight - 2 * MarginY - PedalStripHeight;
        var points = new PointCollection(trace.Samples.Count);
        foreach (var sample in trace.Samples)
        {
            var pct = refLength > 0 ? Math.Clamp(sample.LapDistance / refLength * 100, 0, 100) : 0;
            points.Add(new Point(
                MarginX + pct / 100 * plotWidth,
                MarginY + (1 - Math.Clamp((float)sample.Speed / yMax, 0, 1)) * plotHeight));
        }

        TraceCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }
    /// <summary>Distance axis labels (0/25/50/75/100 %) + speed gridlines every 100 km/h.</summary>
    /// <summary>Pedal strip under the speed curve: throttle (green) rises from the
    /// strip's midline, brake (red) falls from it — 0..100% mapped to half the strip.
    /// Traces recorded before pedal capture (all-zero) are skipped. <paramref
    /// name="opacity"/> dims the reference lap's pedals.</summary>
    private void PlotPedals(LapTrace trace, float refLength, double opacity)
    {
        if (!trace.Samples.Any(s => s.Throttle > 0 || s.Brake > 0))
        {
            return;
        }

        var plotWidth = ChartWidth - 2 * MarginX;
        var stripTop = MarginY + (ChartHeight - 2 * MarginY - PedalStripHeight);
        var mid = stripTop + PedalStripHeight / 2;
        var half = PedalStripHeight / 2;

        TraceCanvas.Children.Add(new Line
        {
            X1 = MarginX,
            Y1 = mid,
            X2 = ChartWidth - MarginX,
            Y2 = mid,
            Stroke = ThemeBrush("AppChartGrid", Color.FromRgb(0xE0, 0xE0, 0xE0)),
            StrokeThickness = 1,
            Opacity = 1.0,
        });

        var throttle = new PointCollection(trace.Samples.Count);
        var brake = new PointCollection(trace.Samples.Count);
        foreach (var sample in trace.Samples)
        {
            var pct = refLength > 0 ? Math.Clamp(sample.LapDistance / refLength * 100, 0, 100) : 0;
            var x = MarginX + pct / 100 * plotWidth;
            throttle.Add(new Point(x, mid - Math.Clamp(sample.Throttle, 0, 1) * half));
            brake.Add(new Point(x, mid + Math.Clamp(sample.Brake, 0, 1) * half));
        }

        TraceCanvas.Children.Add(new Polyline
        {
            Points = brake,
            Stroke = ThemeBrush("AppAccent", Color.FromRgb(0xE1, 0x06, 0x00)),
            StrokeThickness = 1.5,
            Opacity = opacity,
        });
        TraceCanvas.Children.Add(new Polyline
        {
            Points = throttle,
            Stroke = ThemeBrush("AppStatusGood", Color.FromRgb(0x2E, 0xB8, 0x72)),
            StrokeThickness = 1.5,
            Opacity = opacity,
        });
    }

    private void DrawTraceAxis(int yMax)
    {
        var plotWidth = ChartWidth - 2 * MarginX;
        var plotHeight = ChartHeight - 2 * MarginY - PedalStripHeight;
        for (var speed = 0; speed <= yMax; speed += 100)
        {
            var y = MarginY + (1 - speed / (double)yMax) * plotHeight;
            TraceCanvas.Children.Add(new Line
            {
                X1 = MarginX - 6,
                Y1 = y,
                X2 = ChartWidth - MarginX + 6,
                Y2 = y,
                Stroke = ThemeBrush("AppChartGrid", Color.FromRgb(0xE0, 0xE0, 0xE0)),
                StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                Text = $"{speed}",
                FontSize = 9,
                Foreground = ThemeBrush("AppMutedForeground", Color.FromRgb(0x88, 0x88, 0x88)),
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 6);
            TraceCanvas.Children.Add(label);
        }

        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            var label = new TextBlock
            {
                Text = $"{pct}%",
                FontSize = 9,
                Foreground = ThemeBrush("AppMutedForeground", Color.FromRgb(0x88, 0x88, 0x88)),
            };
            Canvas.SetLeft(label, MarginX + pct / 100.0 * plotWidth - 8);
            Canvas.SetTop(label, ChartHeight - MarginY + 2);
            TraceCanvas.Children.Add(label);
        }
    }

    /// <summary>Which curve is which lap: "Runde 7" (A, red) vs. "Runde 9" (B, gold).</summary>
    private void DrawTraceLegend()
    {
        var vm = _vm;
        if (vm?.SelectedTraceA is not { } a || vm.SelectedTraceB is not { } b)
        {
            return;
        }

        var legend = new TextBlock
        {
            FontSize = 9,
            Foreground = ThemeBrush("AppMutedForeground", Color.FromRgb(0x88, 0x88, 0x88)),
            Text = $"— Runde {a.LapNumber} (A)   — Runde {b.LapNumber} (B)   ·   unterer Streifen: Gas (grün) / Bremse (rot)",
        };
        Canvas.SetRight(legend, 4);
        Canvas.SetTop(legend, 2);
        TraceCanvas.Children.Add(legend);
    }

    /// <summary>% axis reference: track length when known, else the trace's max distance.</summary>
    private static float ReferenceLength(LapTrace trace) => trace.TrackLength > 0
        ? trace.TrackLength
        : trace.Samples.Count > 0 ? trace.Samples.Max(s => s.LapDistance) : 0;

    /// <summary>Draws the position series: x = lap, y = position (P1 top), plus a
    /// subtle gridline per position.</summary>
    private void RedrawChart()
    {
        ChartCanvas.Children.Clear();
        TraceCanvas.Children.Clear();
        if (_vm is null)
        {
            return;
        }

        var laps = _vm.LapRows;
        if (laps.Count == 0)
        {
            return;
        }

        var maxPos = laps.Max(r => r.Position);
        if (maxPos == 0)
        {
            return;
        }

        var maxLap = _vm.LapCount > 0 ? _vm.LapCount : laps.Max(r => r.Lap);
        var stepX = (ChartWidth - 2 * MarginX) / Math.Max(1, maxLap - 1);
        var stepY = (ChartHeight - 2 * MarginY) / Math.Max(1, maxPos - 1);

        DrawGrid(maxPos, stepY);

        var points = new PointCollection(laps.Select(r => new Point(
            MarginX + (r.Lap - 1) * stepX,
            MarginY + (r.Position - 1) * stepY)));

        ChartCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = ThemeBrush("AppAccent", Color.FromRgb(0xE1, 0x06, 0x00)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    /// <summary>Theme brushes resolve at draw time so the chart repaints correctly
    /// after a Light/Dark switch (DynamicResource doesn't apply to code-built shapes).</summary>
    private static Brush ThemeBrush(string key, Color fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>Draws one faint line per position from P1 down to <paramref name="maxPos"/>.</summary>
    private void DrawGrid(int maxPos, double stepY)
    {
        var gridBrush = ThemeBrush("AppChartGrid", Color.FromRgb(0xE0, 0xE0, 0xE0));
        for (var pos = 1; pos <= maxPos; pos++)
        {
            ChartCanvas.Children.Add(new Line
            {
                X1 = MarginX - 6,
                Y1 = MarginY + (pos - 1) * stepY,
                X2 = ChartWidth - MarginX + 6,
                Y2 = MarginY + (pos - 1) * stepY,
                Stroke = gridBrush,
                StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                Text = $"P{pos}",
                FontSize = 9,
                Foreground = ThemeBrush("AppMutedForeground", Color.FromRgb(0x88, 0x88, 0x88)),
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, MarginY + (pos - 1) * stepY - 6);
            ChartCanvas.Children.Add(label);
        }
    }
}
