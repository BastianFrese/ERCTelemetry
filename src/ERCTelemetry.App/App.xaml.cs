using System.IO;
using System.Windows;
using System.Windows.Threading;
using ERCTelemetry.App.Composition;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.App;

public partial class App : Application
{
    /// <summary>Last-resort error log — startup crashes otherwise die silently for a
    /// double-clicked WPF exe.</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERCTelemetry", "error.log");

    private static Mutex? _instanceMutex;

    internal static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch
        {
            // Never crash because logging failed.
        }
    }

    /// <summary>Named-mutex single-instance guard: a second launch shows a message and
    /// shuts down instead of fighting over the UDP port / DB / overlay server.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\ERCTelemetry.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log("Second launch blocked by single-instance guard.");
            MessageBox.Show(
                "ERCTelemetry is already running. Use the tray icon to open it.",
                "ERCTelemetry", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // The persisted theme applies before the first window renders (StartupUri runs
        // after OnStartup returns).
        try
        {
            ThemeManager.Apply(new AppSettingsService().Current.Theme);
        }
        catch (Exception ex)
        {
            Log($"Theme application failed ({ex.Message}) — staying on the XAML default.");
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Abandoned/foreign mutex at shutdown — nothing to release.
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        e.Handled = false;
    }
}