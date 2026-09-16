using System.Windows;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Modal input for one driver-name override. <see cref="DriverName"/> is the trimmed
/// display name — an empty string means "drop the override and show the game name again".</summary>
public partial class RenameDriverDialog : Window
{
    public RenameDriverDialog(string gameName, string currentName)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        GameNameText.Text = gameName;
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    /// <summary>Trimmed new name; empty string removes the override (OK with blank input).</summary>
    public string DriverName { get; private set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DriverName = NameBox.Text.Trim();
        DialogResult = true;
    }
}