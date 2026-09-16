using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Race report tab: card layout over the report view model; canvases are
/// redrawn whenever the VM rebuilds the report (session / duel pickers).</summary>
public partial class RaceReportView : UserControl
{
    private RaceReportViewModel? _vm;

    public RaceReportView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RedrawCharts();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DataContextProperty)
        {
            WireViewModel();
        }
    }

    private void WireViewModel()
    {
        if (_vm is not null)
        {
            _vm.ReportRebuilt -= RedrawCharts;
        }

        _vm = DataContext as RaceReportViewModel;
        if (_vm is not null)
        {
            _vm.ReportRebuilt += RedrawCharts;
            RedrawCharts();
        }
    }

    private void OnExportHtml(object sender, RoutedEventArgs e) => _vm?.ExportHtml();

    private void OnCopySetupCode(object sender, RoutedEventArgs e) => _vm?.CopySetupCode();

    private void OnImportSetupCode(object sender, RoutedEventArgs e) =>
        _vm?.ImportSetupCode(SetupImportBox.Text);

    /// <summary>Raised when the user clicks "‹ Zurück zur History" — the host (MainWindow)
    /// switches back to the History panel, since the Report rail item was removed.</summary>
    public event Action? BackRequested;

    private void OnBackToHistory(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void RedrawCharts()
    {
        if (_vm?.Report is not { } report)
        {
            return;
        }

        var accent = ThemeBrush("AppAccent", Color.FromRgb(0xE1, 0x06, 0x00));
        if (report.LapSeries.Count > 0)
        {
            ReportCharts.DrawLapTimes(LapTimesCanvas, report.LapSeries[0], accent);
        }

        var traceA = report.Traces.Count > 0 ? report.Traces[0].Trace : null;
        var traceB = report.Traces.Count > 1 ? report.Traces[1].Trace : null;
        if (traceA is not null && traceB is not null)
        {
            ReportCharts.DrawTraceDuel(
                TraceDuelCanvas, traceA, traceB,
                ThemeBrush("AppStatusGood", Color.FromRgb(0x2E, 0xB8, 0x72)),
                ThemeBrush("AppStatusWarn", Color.FromRgb(0xF3, 0xD0, 0x2F)));
        }
    }

    /// <summary>Theme brushes resolve at draw time so the charts repaint correctly after
    /// a Light/Dark switch (same pattern as HistoryTabView).</summary>
    private static Brush ThemeBrush(string key, Color fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
