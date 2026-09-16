using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ERCTelemetry.App.Dashboard;

/// <summary>History tab host: stacks the session-card list and the session detail view
/// and toggles Visibility between them on DetailOpened/ListOpened. The card grid lives
/// in <see cref="SessionListView"/>, the session detail (results/pace/charts/events) in
/// <see cref="SessionDetailView"/> — including the chart redraw subscription.</summary>
public partial class HistoryTabView : UserControl
{
    private HistoryViewModel? _vm;

    public HistoryTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as HistoryViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsDetailOpen" && _vm is { } vm)
        {
            Detail.Visibility = vm.IsDetailOpen ? Visibility.Visible : Visibility.Collapsed;
            List.Visibility = vm.IsDetailOpen ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}