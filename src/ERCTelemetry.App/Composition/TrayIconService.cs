using System.Windows;
using WinForms = System.Windows.Forms;

namespace ERCTelemetry.App.Composition;

/// <summary>NotifyIcon tray presence: double-click opens the dashboard, the context menu
/// offers open / in-game overlay toggle / exit. Balloons double as the app's "toast"
/// notifications (Windows renders tray balloons as toasts on 10/11).</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly AppSettingsService _settings;

    public TrayIconService(
        AppSettingsService settings,
        Action openDashboard,
        Action toggleOverlay,
        Action positionOverlay,
        Action exitApplication)
    {
        _settings = settings;

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ERCTelemetry",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open dashboard", null,
            (_, _) => Application.Current?.Dispatcher.Invoke(openDashboard));
        menu.Items.Add("In-game overlay (Ctrl+Shift+O)", null,
            (_, _) => Application.Current?.Dispatcher.Invoke(toggleOverlay));
        menu.Items.Add("Overlay position mode", null,
            (_, _) => Application.Current?.Dispatcher.Invoke(positionOverlay));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null,
            (_, _) => Application.Current?.Dispatcher.Invoke(exitApplication));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Application.Current?.Dispatcher.Invoke(openDashboard);
    }

    /// <summary>Shows a balloon ("toast") unless notifications are disabled in settings.
    /// Call from the UI thread (all current call sites are UI-side).</summary>
    public void Notify(string title, string text)
    {
        if (!_settings.Current.ShowNotifications)
        {
            return;
        }

        try
        {
            _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info);
        }
        catch
        {
            // A tray-less session (or race at shutdown) must not crash over a notification.
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>The exe's embedded icon (csproj ApplicationIcon) for the tray; generic
    /// fallback so a missing/iconless binary never blocks startup.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            return exePath is not null
                ? System.Drawing.Icon.ExtractAssociatedIcon(exePath)
                    ?? System.Drawing.SystemIcons.Application
                : System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }
}