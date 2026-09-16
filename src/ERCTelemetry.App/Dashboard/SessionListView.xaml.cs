using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ERCTelemetry.App.Dashboard;

/// <summary>"Deine Sessions" card grid: filters, 2-column cards, pagination.
/// Pure interaction glue — all state lives in <see cref="HistoryViewModel"/>.</summary>
public partial class SessionListView : UserControl
{
    public SessionListView()
    {
        InitializeComponent();
    }

    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.Refresh();

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryViewModel.SessionCardRow card })
        {
            Vm?.OpenSession(card);
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Vm?.PreviousPage();

    private void Next_Click(object sender, RoutedEventArgs e) => Vm?.NextPage();

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int page })
        {
            Vm?.GoToPage(page);
        }
    }
}