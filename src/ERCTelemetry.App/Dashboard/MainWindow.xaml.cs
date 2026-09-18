using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ERCTelemetry.App.Composition;
using ERCTelemetry.App.Dashboard;
using ERCTelemetry.App.OverlayServer;
using ERCTelemetry.App.Share;
using ERCTelemetry.App.OverlayWindow;
using ERCTelemetry.App.Update;
using ERCTelemetry.Core;
using ERCTelemetry.Core.Llm;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Share;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Update;

namespace ERCTelemetry.App;

public partial class MainWindow : Window
{
    private readonly AppSettingsService _settingsService = new();
    private readonly AppServices _services;
    private readonly VoiceAlertService _voiceAlerts;
    private readonly TwitchChatHost _twitchChat;
    private readonly DiscordLoginHost _discordLogin;
    private readonly ErcRacePromptService _ercPrompts;
    private readonly OverlayWebHost _overlay;
    private readonly DebugViewModel _debug;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly SetupsViewModel _setups;
    private readonly DashboardViewModel _dashboard;
    private readonly HistoryViewModel _history;
    private readonly RaceReportViewModel _report;
    private readonly InGameOverlayWindow _inGameOverlay;
    private readonly TrayIconService _tray;
    private readonly UpdateService _update;
    private bool _updateBusy; // 1 while a check/download runs — blocks button re-entrancy
    private bool _exiting;
    private Task<UpdateService.DeltaResult>? _deltaTask; // background delta download
    private bool _deltaReady; // staging complete — the update applies on exit
    private bool _hotkeysRegistered; // OnSourceInitialized must stay idempotent
    private bool _teardownStarted; // OnClosing deferral is one-shot
    private int _isSaving; // 1 while SaveAsync is in flight — blocks click re-entrancy

    /// <summary>F1-red #E10600 as a DWM COLORREF (0x00BBGGRR) for the window border.</summary>
    private const uint AccentBorderColorRef = 0x000006E1;

    /// <summary>App version from the assembly (matches the published Setup.exe) — shown in
    /// the Setup tab so users can compare against the download page. Shows the full version
    /// when a build number is present (0.6.2.135), else the 3-part feature version (0.6.2).</summary>
    internal static string AppVersion
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return version is { Revision: > 0 } ? version.ToString(4) : version?.ToString(3) ?? "?";
        }
    }

    public MainWindow()
    {
        var bootstrap = _settingsService.Current;
        // The clip services read settings live, so the services get the settings service.
        _services = new AppServices(null, _settingsService);
        _voiceAlerts = new VoiceAlertService(_services, _settingsService);
        _twitchChat = new TwitchChatHost(_services, _settingsService);
        _discordLogin = new DiscordLoginHost(_settingsService);
        _ercPrompts = new ErcRacePromptService(_services);

        InitializeComponent();
        ThemeManager.Apply(bootstrap.Theme);
        RestoreWindowState(); // saved size/position from the last close

        _tray = new TrayIconService(
            _settingsService, OpenDashboard, ToggleInGameOverlay, PositionInGameOverlay, ExitApplication);
        _overlay = new OverlayWebHost(_services);
        // Resolved at send time so OBS pages switch schemes live on the next hello/state.
        _overlay.ColorSchemeProvider = () =>
            _settingsService.Current.ColorScheme == OverlayColorScheme.German ? "de" : null;
        // Resolved at send time so overlay pages pick up block-visibility toggles live.
        _overlay.ConfigProvider = () =>
            ERCTelemetry.Core.Settings.OverlayBlocks.FromSettings(_settingsService.Current);
        _debug = new DebugViewModel(_services, _overlay, _tray, _settingsService);
        _update = new UpdateService(() => _settingsService.Current.UpdateBaseUrl);
        _dashboard = new DashboardViewModel(_services, _settingsService, _tray);
        _history = new HistoryViewModel(_services);
        _report = new RaceReportViewModel(_services);
        _history.OpenReportRequested += row =>
        {
            _report.OpenReportFor(row);
            ShowPanel("report");
        };
        // The Report rail item is gone — the report's own back button is the way back.
        ReportView.BackRequested += () => SelectNavItem("history");
        _settingsViewModel = new SettingsViewModel(_services, _debug, _overlay, _settingsService, _twitchChat, _discordLogin);
        _setups = new SetupsViewModel(_discordLogin, new ErcSetupsClient(
            () => string.IsNullOrWhiteSpace(_settingsViewModel.ErcApiUrl) ? null : _settingsViewModel.ErcApiUrl));
        // Chat-bot status changes (connect, error, login) refresh the Settings tab live.
        _twitchChat.StatusChanged += () => _settingsViewModel.RefreshTwitchStatus();
        // Discord login/logout refresh the Settings + Setups tabs live.
        _discordLogin.StatusChanged += () => _settingsViewModel.RefreshDiscordState();
        _discordLogin.UserChanged += () => _settingsViewModel.RefreshDiscordState();
        // Seed the masked API-key field from the loaded settings. The PasswordChanged
        // handler compares against the VM value, so seeding never marks the tab dirty.
        LlmApiKeyBox.Password = _settingsViewModel.LlmApiKey;
        ErcApiKeyBox.Password = _settingsViewModel.ErcApiKey;
        // Finished league races arrive off the background prompt service — marshal the
        // send dialog to the UI thread (the handler must not touch the window here).
        _ercPrompts.RaceEnded += raceEnded => Dispatcher.Invoke(() => ShowErcSendDialog(raceEnded));
        _inGameOverlay = new InGameOverlayWindow(_services, _settingsService);
        // A preset click must re-layout the live HUD immediately (widget groups + positions),
        // not wait for the next settings Save.
        _settingsViewModel.HudPresetApplied += () =>
            _inGameOverlay.ApplyHudSettings(_settingsService.Current);
        // Row rename menu is built in code: Click attributes inside a Style Setter.Value
        // emit broken BAML connect code (InvalidCastException at startup).
        StandingsGrid.LoadingRow += (_, e) => AttachRowMenu(e.Row);

        DataContext = _debug; // format banner + Debug tab
        DashboardTab.DataContext = _dashboard;
        RaceControlTab.DataContext = _dashboard;
        HistoryTab.DataContext = _history;
        ReportTab.DataContext = _report;
        SettingsTab.DataContext = _settingsViewModel;
        SetupsTab.DataContext = _setups;

        // Select the rail's first entry (Setup is already Visible in XAML) only after
        // every panel exists — a XAML-time SelectedIndex would fire during parsing.
        // Select by Tag: the rail's leading section captions are items too.
        NavList.SelectedItem = NavList.Items.OfType<System.Windows.Controls.ListBoxItem>()
            .First(i => i.Tag as string == "setup");
        // Settings sidebar: start on the first section (VERBINDUNG). Same XAML-time
        // guard as the main rail — select after all cards exist.
        SettingsNav.SelectedItem = SettingsNav.Items.OfType<System.Windows.Controls.ListBoxItem>()
            .First(i => i.Tag as string == "verbindung");
        ApplyDebugNavVisibility(); // hide the Debug rail item unless the power-user toggle is on

        if (bootstrap.AutoStartListening)
        {
            _debug.StartListening(bootstrap.UdpPort.ToString()); // auto-start on launch, saved port
        }

        // Fire-and-forget: a bind failure (port already taken) must never take the
        // dashboard down. Safe — the ctor completes before any await resumes on the UI
        // thread, and _tray already exists here. Last statement so a ctor exception
        // later can never leave a started host without an owner.
        _ = StartOverlayAsync(bootstrap.OverlayPort); // OBS browser-source server
        _ = CheckUpdateAfterStartupAsync(); // silent update check, only surfaces availability
        _ = CheckUpdateLogAfterStartupAsync(); // show UPDATELOG.md after an update
        _ = CheckApplyResultAfterStartupAsync(); // surface a failed delta apply, then clean up
        _ = RefreshFirewallStatusAfterStartupAsync(); // Setup tab: rule present? offer to add it
        _ = ErcDriverDataHost.RunAsync(); // keep erc-drivers.json fresh for the championship overlays
        SetupVersionText.Text = $"ERCTelemetry Version {AppVersion}"; // web-download version match
    }

    private async Task StartOverlayAsync(int port)
    {
        try
        {
            await _overlay.StartAsync(port);
        }
        catch (Exception ex)
        {
            string message = $"Overlay server could not start on port {port}: {ex.Message}";
            App.Log(message);
            _tray.Notify("ERCTelemetry — overlay", message);
        }
    }

    /// <summary>Silent startup update check (5 s budget): an available update surfaces in
    /// the Setup tab + a tray toast; "unreachable"/"invalid" stay quiet so an offline
    /// launch never complains.</summary>
    private async Task CheckUpdateAfterStartupAsync()
    {
        while (!IsLoaded)
        {
            await Task.Delay(100);
        }

        await CheckUpdateAsync(silent: true);
    }

    /// <summary>After an update, shows the release's UPDATELOG.md section once: compares the
    /// stored last-seen version against the running one; a difference means the user just
    /// updated (or first-launches this version), so the local copy of the log — shipped in
    /// the install directory by the installer — is read and the matching section displayed
    /// modally. The version is marked seen even when the file/section is missing, so a bad
    /// release can never loop the prompt.</summary>
    private async Task CheckUpdateLogAfterStartupAsync()
    {
        while (!IsLoaded)
        {
            await Task.Delay(100);
        }

        var version = AppVersion;
        if (_settingsService.Current.LastSeenVersion == version)
        {
            return;
        }

        try
        {
            // Inside the guarded region: a settings-write IO failure must land in the log,
            // not fault the fire-and-forget task unobserved (LOW, 2026-09-16).
            _settingsService.Update(s => s with { LastSeenVersion = version });

            var logPath = Path.Combine(AppContext.BaseDirectory, "UPDATELOG.md");
            if (!File.Exists(logPath))
            {
                return;
            }

            var section = UpdateLogSection.Extract(File.ReadAllText(logPath), version);
            if (section is null)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => new UpdateLogWindow(version, section)
            {
                Owner = this,
            }.ShowDialog());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"update log display failed: {ex.Message}");
        }
    }

    /// <summary>After a delta update, apply-update.ps1 writes apply-result.json. A failed
    /// apply is surfaced once (modal warning); the file is deleted either way so it never
    /// re-prompts.</summary>
    private async Task CheckApplyResultAfterStartupAsync()
    {
        while (!IsLoaded)
        {
            await Task.Delay(100);
        }

        var resultPath = Path.Combine(UpdateService.UpdateRootPath(), "apply-result.json");
        if (!File.Exists(resultPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(resultPath);
            File.Delete(resultPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var error = doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : "unbekannter Fehler";
                await Dispatcher.InvokeAsync(() => MessageBox.Show(
                    this,
                    $"Das Update konnte nicht angewendet werden: {error}",
                    "ERCTelemetry — Update fehlgeschlagen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Log($"apply-result read failed: {ex.Message}");
        }
    }

    /// <summary>Setup tab: checks the firewall rule once after load (netsh runs off the UI
    /// thread) and shows a status line — plus an unobtrusive button when the rule is missing
    /// or points at an old Program-Files path. No startup popup.</summary>
    private async Task RefreshFirewallStatusAfterStartupAsync()
    {
        while (!IsLoaded)
        {
            await Task.Delay(100);
        }

        var ok = await Task.Run(FirewallService.RuleExists);
        FirewallStatusText.Text = ok
            ? "Firewall-Freigabe für UDP-Telemetrie ist aktiv."
            : "Firewall-Freigabe fehlt — ohne sie empfängt die App keine Telemetrie von Konsole/zweitem PC.";
        FirewallAddButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Creates the firewall rule (one UAC prompt) and refreshes the status line.</summary>
    private void FirewallAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FirewallService.AddRule())
            {
                FirewallStatusText.Text = "Firewall-Freigabe für UDP-Telemetrie ist aktiv.";
                FirewallAddButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                FirewallStatusText.Text = "Firewall-Freigabe wurde nicht angelegt (abgebrochen?).";
            }
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = $"Firewall-Freigabe fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>Queries the update server and mirrors the verdict into the Setup tab's
    /// update card. Never throws — the service maps all failure modes to results.</summary>
    private async Task CheckUpdateAsync(bool silent)
    {
        if (_updateBusy)
        {
            return;
        }

        _debug.UpdateUpdateStatus("Suche nach Updates…", false);
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(silent ? 5 : 15));
        var (result, manifest) = await _update.CheckAsync(timeout.Token);
        switch (result)
        {
            case UpdateService.CheckResult.UpdateAvailable:
                if (_deltaTask is { IsCompleted: false })
                {
                    return; // a delta download is already running — leave the UI as-is
                }

                var published = DateTimeOffset.TryParse(
                    manifest!.PublishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? $" — veröffentlicht am {date:dd.MM.yyyy}"
                    : string.Empty;

                if (UpdateService.CanWriteToInstallDir())
                {
                    // Discord-style: download the changed files in the background, apply on exit.
                    _deltaReady = false;
                    _debug.UpdateUpdateStatus(
                        $"Update auf Version {manifest.Version}{published} wird im Hintergrund heruntergeladen…",
                        available: true,
                        bannerText: $"Version {manifest.Version} ist verfügbar");
                    UpdateProgressBar.Visibility = Visibility.Visible;
                    _debug.UpdateDownloadProgress(0, downloading: true);
                    _deltaTask = DownloadDeltaInBackgroundAsync(manifest);
                }
                else
                {
                    // Program-Files install (old installer): the delta cannot apply there,
                    // so the full Setup.exe remains the only path.
                    _debug.UpdateUpdateStatus(
                        $"Update auf Version {manifest.Version}{published} verfügbar — herunterladen und installieren über den Button.",
                        available: true,
                        bannerText: $"Version {manifest.Version} ist verfügbar");
                    UpdateInstallButton.Visibility = Visibility.Visible;
                }

                _tray.Notify("ERCTelemetry — Update",
                    $"Version {manifest.Version} ist verfügbar (Setup-Tab → Updates).");
                break;
            case UpdateService.CheckResult.UpToDate:
                _debug.UpdateUpdateStatus($"Version {AppVersion} ist aktuell.", available: false);
                break;
            case UpdateService.CheckResult.Unreachable:
                if (!silent)
                {
                    _debug.UpdateUpdateStatus(
                        "Update-Server nicht erreichbar — später erneut versuchen.", available: false);
                }
                break;
            case UpdateService.CheckResult.Invalid:
                if (!silent)
                {
                    _debug.UpdateUpdateStatus(
                        "Antwort des Update-Servers war ungültig — später erneut versuchen.", available: false);
                }
                break;
        }
    }

    /// <summary>Background delta download (started fire-and-forget from the update check).
    /// On success the update is staged and applies on exit; on any failure the card falls
    /// back to the manual full-Setup path. Never throws — all failures are caught here.</summary>
    private async Task<UpdateService.DeltaResult> DownloadDeltaInBackgroundAsync(UpdateManifest manifest)
    {
        try
        {
            var progress = new Progress<double>(p =>
                _debug.UpdateDownloadProgress((int)Math.Round(p), downloading: true));
            var result = await _update.DownloadDeltaAsync(manifest, progress);
            if (result == UpdateService.DeltaResult.Ready)
            {
                _deltaReady = true;
                _debug.UpdateDownloadProgress(100, downloading: false);
                _debug.UpdateUpdateStatus(
                    "Update bereit — wird beim Beenden installiert.", available: true);
                UpdateInstallButton.Visibility = Visibility.Visible;
                return UpdateService.DeltaResult.Ready;
            }
        }
        catch (Exception ex)
        {
            App.Log($"delta download failed: {ex.Message}");
        }

        _deltaReady = false;
        _debug.UpdateDownloadProgress(0, downloading: false);
        _debug.UpdateUpdateStatus(
            $"Update auf Version {manifest.Version} verfügbar — herunterladen und installieren über den Button.",
            available: true,
            bannerText: $"Version {manifest.Version} ist verfügbar");
        UpdateInstallButton.Visibility = Visibility.Visible;
        return UpdateService.DeltaResult.Fallback;
    }

    /// <summary>Manual check from the update card.</summary>
    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdateAsync(silent: false);

    /// <summary>Opens the user-visible update log (UPDATELOG.md on the server) in the
    /// default browser.</summary>
    private void OpenUpdateLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _update.UpdateLogUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _debug.UpdateUpdateStatus($"Update-Log konnte nicht geöffnet werden: {ex.Message}", _debug.UpdateAvailable);
        }
    }

    /// <summary>Download (with progress) → SHA256 verify → launch the installer via the
    /// shell (UAC prompt) → the app shuts itself down so Inno can replace its files.
    /// Settings/history in %LOCALAPPDATA% survive the reinstall.</summary>
    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
        {
            return;
        }

        _updateBusy = true;
        UpdateCheckButton.IsEnabled = false;
        UpdateInstallButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        _debug.UpdateDownloadProgress(0, downloading: true);
        try
        {
            // Delta already staged → apply immediately (no download, no UAC).
            if (_deltaReady)
            {
                _deltaReady = false;
                _update.LaunchDeltaUpdater(UpdateService.ApplyScriptPath());
                return;
            }

            // Delta still downloading → wait for it, then apply if it succeeded.
            if (_deltaTask is not null)
            {
                var delta = await _deltaTask;
                if (delta == UpdateService.DeltaResult.Ready)
                {
                    _deltaReady = false;
                    _update.LaunchDeltaUpdater(UpdateService.ApplyScriptPath());
                    return;
                }
            }

            // Fallback: full Setup.exe (no file manifest, delta failed, or Program-Files
            // install). Re-check right before downloading: the manifest may have changed
            // since the startup check, and DownloadAsync needs its SHA256 for verification.
            var (result, manifest) = await _update.CheckAsync();
            if (result != UpdateService.CheckResult.UpdateAvailable || manifest is null)
            {
                _debug.UpdateUpdateStatus(
                    "Kein Update mehr verfügbar — bitte erneut nach Updates suchen.", available: false);
                return;
            }

            // Progress<T> marshals the callback onto the UI thread.
            var progress = new Progress<double>(p =>
                _debug.UpdateDownloadProgress((int)Math.Round(p), downloading: true));
            var path = await Task.Run(() => _update.DownloadAsync(manifest, progress));

            _debug.UpdateDownloadProgress(100, downloading: false);
            _debug.UpdateUpdateStatus(
                $"Version {manifest.Version} heruntergeladen und geprüft — der Installer startet jetzt.",
                available: false);
            _update.LaunchInstaller(path);
        }
        catch (Exception ex)
        {
            _debug.UpdateDownloadProgress(0, downloading: false);
            _debug.UpdateUpdateStatus($"Update fehlgeschlagen: {ex.Message}", available: false);
        }
        finally
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateCheckButton.IsEnabled = true;
            UpdateInstallButton.IsEnabled = true;
            _updateBusy = false;
        }
    }

    private void OpenDashboard()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Exit via the tray menu — bypasses close-to-tray.</summary>
    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Registers the global Ctrl+Shift+O / Ctrl+Shift+R hotkeys (work while the
    /// game has focus) and applies DWM chrome polish (dark mode, rounded corners, red
    /// border) — both idempotent, both best-effort.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var source = (HwndSource)HwndSource.FromHwnd(hwnd)!;
        source.AddHook(WndProc);

        try
        {
            Win32Interop.ApplyWindowChrome(
                hwnd,
                ThemeManager.IsDark(_settingsService.Current.Theme),
                AccentBorderColorRef);
        }
        catch (Exception ex)
        {
            App.Log($"DWM chrome polish failed: {ex.Message}");
        }

        if (_hotkeysRegistered)
        {
            return;
        }

        _hotkeysRegistered = true;
        if (!Win32Interop.RegisterHotKey(
            hwnd,
            Win32Interop.OverlayHotKeyId,
            Win32Interop.ModControl | Win32Interop.ModShift | Win32Interop.ModNoRepeat,
            Win32Interop.VkO))
        {
            _tray.Notify("ERCTelemetry — hotkey",
                "Ctrl+Shift+O is already used by another program. The in-game overlay can still be toggled via the tray menu.");
        }

        if (!Win32Interop.RegisterHotKey(
            hwnd,
            Win32Interop.RivalHotKeyId,
            Win32Interop.ModControl | Win32Interop.ModShift | Win32Interop.ModNoRepeat,
            Win32Interop.VkR))
        {
            _tray.Notify("ERCTelemetry — hotkey",
                "Ctrl+Shift+R is already used by another program. The rival panel still auto-shows on its cycle.");
        }

        if (!Win32Interop.RegisterHotKey(
            hwnd,
            Win32Interop.DebugHotKeyId,
            Win32Interop.ModControl | Win32Interop.ModShift | Win32Interop.ModNoRepeat,
            Win32Interop.VkD))
        {
            _tray.Notify("ERCTelemetry — hotkey",
                "Ctrl+Shift+D is already used by another program. The Debug tab can still be toggled in Settings → Verhalten.");
        }

        if (!Win32Interop.RegisterHotKey(
            hwnd,
            Win32Interop.MapHotKeyId,
            Win32Interop.ModControl | Win32Interop.ModShift | Win32Interop.ModNoRepeat,
            Win32Interop.VkM))
        {
            _tray.Notify("ERCTelemetry — hotkey",
                "Ctrl+Shift+M is already used by another program. The HUD minimap can still be toggled in Settings → HUD.");
        }

        // Ctrl+1..6 → nav rail (best-effort, no toast on conflict — a single busy key
        // must not nag on every launch).
        for (var i = 0; i < NavKeys.Length; i++)
        {
            Win32Interop.RegisterHotKey(
                hwnd,
                Win32Interop.NavHotKeyBase + i,
                Win32Interop.ModControl | Win32Interop.ModNoRepeat,
                (uint)(Win32Interop.Vk1 + i));
        }
    }

    /// <summary>Shows or hides the in-game overlay HUD.</summary>
    private void ToggleInGameOverlay()
    {
        if (_inGameOverlay.IsVisible)
        {
            _inGameOverlay.Stop();
            _inGameOverlay.Hide();
            return;
        }

        _inGameOverlay.Show();
        _inGameOverlay.Start();
    }

    /// <summary>Tray menu: enter HUD position mode (drag + Esc), persists on drag end.</summary>
    private void PositionInGameOverlay()
    {
        if (!_inGameOverlay.IsVisible)
        {
            _inGameOverlay.Show();
            _inGameOverlay.Start();
        }

        _inGameOverlay.BeginPositionMode();
        _tray.Notify("ERCTelemetry — overlay",
            "Position mode: drag any widget freely, Esc keeps the layout (also saved in settings).");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WmHotkey && wParam.ToInt32() == Win32Interop.OverlayHotKeyId)
        {
            ToggleInGameOverlay();
            handled = true;
        }
        else if (msg == Win32Interop.WmHotkey && wParam.ToInt32() == Win32Interop.RivalHotKeyId)
        {
            _inGameOverlay.ShowRivalNow();
            handled = true;
        }
        else if (msg == Win32Interop.WmHotkey && wParam.ToInt32() == Win32Interop.DebugHotKeyId)
        {
            ToggleDebugTab();
            handled = true;
        }
        else if (msg == Win32Interop.WmHotkey && wParam.ToInt32() == Win32Interop.MapHotKeyId)
        {
            _inGameOverlay.ToggleMapWidget();
            handled = true;
        }
        else if (msg == Win32Interop.WmHotkey)
        {
            var navIndex = wParam.ToInt32() - Win32Interop.NavHotKeyBase;
            if (navIndex >= 0 && navIndex < NavKeys.Length)
            {
                SelectNavItem(NavKeys[navIndex]);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Header toggle — starts with the saved port; the port box lives only in
    /// Settings → Verbindung so the game port can not be changed by accident.</summary>
    private void ListenerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_debug.IsListening)
        {
            _debug.StopListening();
        }
        else
        {
            _debug.StartListening(_settingsService.Current.UdpPort.ToString());
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e) =>
        _debug.ToggleRecording();

    /// <summary>Replay row: picks a .f1rec file; Play/Stop hand it to the view model.</summary>
    private void ReplayFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ERCTelemetry recording (*.f1rec)|*.f1rec|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            _debug.ReplayPath = dlg.FileName;
        }
    }

    private void ReplayPlay_Click(object sender, RoutedEventArgs e)
    {
        var speed = 1.0;
        if (ReplaySpeedBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Content: string s }
            && double.TryParse(s.TrimEnd('×'), out var parsed))
        {
            speed = parsed;
        }

        _debug.StartReplay(speed);
    }

    private void ReplayStop_Click(object sender, RoutedEventArgs e) => _debug.StopReplay();

    /// <summary>Setup tab: copies an overlay page URL (Tag = page name) for pasting into
    /// an OBS browser source.</summary>
    private void CopyOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string page })

        {
            return;
        }

        if (!_overlay.IsRunning || _overlay.Port == 0)
        {
            // Copying a port-0 URL would just give OBS a broken address (Port is only
            // set after the Kestrel bind completes).
            _debug.ShowOverlayNotRunning(page);
            return;
        }

        Clipboard.SetText($"http://127.0.0.1:{_overlay.Port}/overlay/{page}.html");
        _debug.ShowSetupCopied(page);
    }

    /// <summary>Setup tab: copies an overlay page URL with an explicit canvas size
    /// (?w=&h=) — Tag = "page:width,height" (e.g. "map:480,300"). OBS renders the
    /// widget pixel-exact instead of CSS-scaled.</summary>
    private void CopyOverlaySizedUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string sized })
        {
            return;
        }

        var parts = sized.Split(':');
        var wh = parts.Length == 2 ? parts[1].Split(',') : [];
        if (parts.Length != 2 || wh.Length != 2 ||
            !int.TryParse(wh[0], out var width) || !int.TryParse(wh[1], out var height))
        {
            return; // malformed Tag — a typo must not copy a broken URL
        }

        if (!_overlay.IsRunning || _overlay.Port == 0)
        {
            _debug.ShowOverlayNotRunning(parts[0]);
            return;
        }

        Clipboard.SetText(
            $"http://127.0.0.1:{_overlay.Port}/overlay/{parts[0]}.html?w={width}&h={height}");
        _debug.ShowSetupCopied(parts[0]);
    }

    /// <summary>Setup tab: copies this PC's LAN IP for the game's UDP telemetry settings
    /// on a console (the game cannot use 127.0.0.1 when it runs on another device).</summary>
    private void CopyLanIp_Click(object sender, RoutedEventArgs e)
    {
        if (_debug.PreferredLanIpRaw is not { Length: > 0 } ip)
        {
            _debug.ShowNoLanIp();
            return;
        }

        Clipboard.SetText(ip);
        _debug.ShowTextCopied($"LAN-IP {ip}");
    }

    /// <summary>Setup tab: copies the UDP port the game should send telemetry to.</summary>
    private void CopyUdpPort_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_debug.ConsolePortText);
        _debug.ShowTextCopied($"UDP-Port {_debug.ConsolePortText}");
    }

    /// <summary>Sidebar navigation — swaps the visible content panel by the selected
    /// rail item's Tag (setup | dashboard | racecontrol | history | settings | debug).</summary>
    private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from inner ListBox/DataGrid selections — only
        // react to the nav rail itself (also guards the XAML-time SelectedIndex fire).
        if (!ReferenceEquals(e.OriginalSource, NavList) || NavList.SelectedItem is not ListBoxItem item
            || item.Tag is not string key)
        {
            return;
        }

        ShowPanel(key);
        if (key == "history")
        {
            _history.Refresh();
        }
        else if (key == "report")
        {
            _report.Refresh();
        }
    }

    private void ShowPanel(string key)
    {
        SetupTab.Visibility = key == "setup" ? Visibility.Visible : Visibility.Collapsed;
        DashboardTab.Visibility = key == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        RaceControlTab.Visibility = key == "racecontrol" ? Visibility.Visible : Visibility.Collapsed;
        HistoryTab.Visibility = key == "history" ? Visibility.Visible : Visibility.Collapsed;
        ReportTab.Visibility = key == "report" ? Visibility.Visible : Visibility.Collapsed;
        SetupsTab.Visibility = key == "setups" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTab.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
        DebugTab.Visibility = key == "debug" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rail keys in Ctrl+1..7 order (Ctrl+7 = Debug, only when the tab is shown).</summary>
    private static readonly string[] NavKeys = ["setup", "dashboard", "racecontrol", "history", "setups", "settings", "debug"];

    /// <summary>Shows/hides the Debug rail item from the power-user toggle (Settings →
    /// Verhalten → Erweiterte Ansicht, or Ctrl+Shift+D). The panel itself stays in XAML.</summary>
    private void ApplyDebugNavVisibility()
    {
        DebugNavItem.Visibility = _settingsService.Current.ShowDebugTab
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Ctrl+Shift+D: flips the power-user toggle live and jumps to the Debug tab
    /// (or back to Setup when turning it off while it is open). Persists on the next Save.</summary>
    private void ToggleDebugTab()
    {
        var show = !_settingsService.Current.ShowDebugTab;
        _settingsService.Update(s => s with { ShowDebugTab = show });
        _settingsViewModel.ShowDebugTab = show; // keep the Settings checkbox in sync
        ApplyDebugNavVisibility();
        if (show)
        {
            SelectNavItem("debug");
        }
        else if (DebugTab.Visibility == Visibility.Visible)
        {
            SelectNavItem("setup");
        }
    }

    /// <summary>Selects a rail item by Tag, routing through NavList_SelectionChanged so the
    /// panel switch + refresh side effects run. Hidden tabs are not reachable.</summary>
    private void SelectNavItem(string key)
    {
        if (key == "debug" && !_settingsService.Current.ShowDebugTab)
        {
            return;
        }

        var item = NavList.Items.OfType<System.Windows.Controls.ListBoxItem>()
            .FirstOrDefault(i => i.Tag as string == key);
        if (item is not null)
        {
            NavList.SelectedItem = item;
        }
    }

    /// <summary>Settings sidebar — swaps the visible settings card by the selected item's
    /// Tag (verbindung | ai | weiterleitung | darstellung | fahrer | verhalten | twitch |
    /// clips | hud | overlays). Same guard pattern as the main rail.</summary>
    private void SettingsNav_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SettingsNav) || SettingsNav.SelectedItem is not ListBoxItem item
            || item.Tag is not string key)
        {
            return;
        }

        ShowSettingsSection(key);
    }

    private void ShowSettingsSection(string key)
    {
        SettingsCardVerbindung.Visibility = key == "verbindung" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardAi.Visibility = key == "ai" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardWeiterleitung.Visibility = key == "weiterleitung" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardDarstellung.Visibility = key == "darstellung" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardFahrer.Visibility = key == "fahrer" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardVerhalten.Visibility = key == "verhalten" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardTwitch.Visibility = key == "twitch" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardClips.Visibility = key == "clips" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardHud.Visibility = key == "hud" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardOverlays.Visibility = key == "overlays" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCardErc.Visibility = key == "erc" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Report back button (the Report rail item was removed — History is the only
    /// way in, so it is the only way back).</summary>
    private void ReportBack_Click(object sender, RoutedEventArgs e) => SelectNavItem("history");

    /// <summary>Applies the saved window bounds; -1 left/top means "center on first launch".</summary>
    private void RestoreWindowState()
    {
        var s = _settingsService.Current;
        if (s.WindowLeft >= 0 && s.WindowTop >= 0)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }

        Width = s.WindowWidth;
        Height = s.WindowHeight;
    }

    /// <summary>Persists the current window bounds so the next launch restores them. Called
    /// from OnClosing before the close-to-tray branch, so hiding to tray also remembers.</summary>
    private void SaveWindowState()
    {
        _settingsService.Update(s => s with
        {
            WindowLeft = Left,
            WindowTop = Top,
            WindowWidth = Width,
            WindowHeight = Height,
        });
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : ""; // restore / maximize glyph
    }

    /// <summary>Custom-chrome close — routes through OnClosing so close-to-tray keeps
    /// working exactly like the native button did.</summary>
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // Re-entrancy guard: WPF raises Click for every physical click, and two concurrent
        // SaveAsync runs would both read "previous" settings and race the overlay restart.
        if (Interlocked.CompareExchange(ref _isSaving, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _settingsViewModel.SaveAsync();
            // HUD preferences (widget groups, size preset) apply without a restart —
            // the position is kept from settings so a live save never snaps it back.
            _inGameOverlay.ApplyHudSettings(_settingsService.Current);
            ApplyDebugNavVisibility(); // the "Erweiterte Ansicht" toggle takes effect on Save
            _twitchChat.ApplySettings(); // chat-bot toggle/channel/token apply on Save
        }
        catch (Exception ex)
        {
            // SaveAsync only throws on true bugs; log and keep the button safe.
            App.Log($"Saving settings failed: {ex.Message}");
            _tray.Notify("ERCTelemetry — settings", $"Saving settings failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isSaving, 0);
        }
    }

    private void ClearNameOverrides_Click(object sender, RoutedEventArgs e) =>
        _settingsViewModel.ClearNameOverrides();

    /// <summary>Settings "Test" button for the voice alerts — speaks a sample phrase so
    /// the user can verify the TTS voice works (no-op while the feature is off).</summary>
    private void VoiceTest_Click(object sender, RoutedEventArgs e) => _voiceAlerts.SpeakTest();

    /// <summary>Settings "Mit Twitch verbinden" button — runs the OAuth login flow and
    /// shows the result in the status text next to the button. On success the host has
    /// already enabled the bot in settings; the toggle follows so the user sees it on.</summary>
    private async void TwitchLogin_Click(object sender, RoutedEventArgs e)
    {
        var message = await _twitchChat.StartLoginAsync();
        _settingsViewModel.SetTwitchStatus(message);
        if (message.StartsWith("Login erfolgreich"))
        {
            _settingsViewModel.TwitchEnabled = true;
        }
    }

    /// <summary>Settings "Verbindung testen" button for the LLM layer — validates the API
    /// key against the fixed cost-efficient model. A missing key is rejected up front, so
    /// a connection can never be reported "OK" without one. The model is locked, so the
    /// button only ever verifies <see cref="LlmConstants.FixedModel"/>.</summary>
    private async void LlmTest_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Llm is not { } llm)
        {
            _settingsViewModel.SetLlmStatus("LLM-Schicht nicht verfügbar");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsViewModel.LlmApiKey))
        {
            _settingsViewModel.SetLlmStatus("API-Key fehlt — zuerst eingeben");
            return;
        }

        _settingsViewModel.SetLlmStatus("Teste Verbindung …");
        try
        {
            var answer = await llm.TestAsync(
                _settingsViewModel.LlmApiKey,
                _settingsViewModel.LlmBaseUrl,
                CancellationToken.None);
            _settingsViewModel.SetLlmStatus(answer is null
                ? "Verbindung fehlgeschlagen — Key/URL prüfen"
                : $"Verbindung OK — {LlmConstants.FixedModel} antwortet");
        }
        catch (Exception ex)
        {
            // async void: an unhandled exception would crash the dispatcher. A timeout
            // (TaskCanceledException) or transport error must land in the status line.
            App.Log($"LLM connection test failed: {ex.Message}");
            _settingsViewModel.SetLlmStatus("Verbindung fehlgeschlagen — Timeout oder Netzwerkfehler");
        }
    }

    private void AddForwardTarget_Click(object sender, RoutedEventArgs e) =>
        _settingsViewModel.AddForwardTarget();

    private void RemoveForwardTarget_Click(object sender, RoutedEventArgs e)
    {
        // The row is the DataContext of the button inside the ItemsControl item template.
        if (sender is FrameworkElement { DataContext: ForwardTargetRow row })
        {
            _settingsViewModel.RemoveForwardTarget(row);
        }
    }

    /// <summary>Settings "Auf Standard zurücksetzen" button — reloads the defaults into the
    /// form WITHOUT saving, so the user can review before pressing SPEICHERN. The masked
    /// secret fields are re-seeded from the reset VM (the PasswordBoxes are not bound).</summary>
    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.ResetToDefaults();
        LlmApiKeyBox.Password = _settingsViewModel.LlmApiKey;
        ErcApiKeyBox.Password = _settingsViewModel.ErcApiKey;
    }

    /// <summary>Settings "Verbindung testen" button for the ERC upload — validates the key
    /// by listing the leagues it may send for (GET /api/telemetry/leagues) and resolves the
    /// key owner (GET /api/telemetry/me). When a Discord user is logged in, the owner is
    /// compared against that user so a wrong key is caught before any race is sent. Like the
    /// LLM test, it evaluates the values IN the form (unsaved edits included), not the last
    /// saved settings the configured sender reads.</summary>
    private async void ErcTest_Click(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.SetErcStatus("Teste Verbindung …");
        var testSender = new ErcRaceSender(
            () => string.IsNullOrWhiteSpace(_settingsViewModel.ErcApiUrl) ? null : _settingsViewModel.ErcApiUrl,
            () => string.IsNullOrWhiteSpace(_settingsViewModel.ErcApiKey) ? null : _settingsViewModel.ErcApiKey);
        var leagues = await testSender.GetLeaguesAsync();
        var owner = await testSender.GetOwnerAsync();
        var discordUser = _discordLogin.User;

        string message;
        if (leagues.Count == 0 && owner is null)
        {
            message = "Verbindung fehlgeschlagen oder keine Liga-Rechte — Key/URL prüfen";
        }
        else if (owner is null)
        {
            message = leagues.Count == 1
                ? "Verbindung OK — 1 Liga verfügbar"
                : $"Verbindung OK — {leagues.Count} Ligen verfügbar";
        }
        else if (discordUser is null)
        {
            message = $"Key gehört zu {OwnerName(owner)} — zum Abgleich mit Discord einloggen";
        }
        else if (string.Equals(owner.DiscordId, discordUser.Id, StringComparison.Ordinal))
        {
            message = $"Key gehört zu dir ({OwnerName(owner)}) ✓";
        }
        else
        {
            message = $"Key gehört zu {OwnerName(owner)} — NICHT zu deinem Discord-Account";
        }

        _settingsViewModel.SetErcStatus(message);
    }

    /// <summary>Best display name of a key owner (display name → name → Discord id).</summary>
    private static string OwnerName(ErcKeyOwner owner) =>
        owner.DisplayName ?? owner.Name ?? owner.DiscordId;

    /// <summary>Settings "Mit Discord verbinden" button — runs the OAuth login flow via the
    /// ShareServer handshake and shows the result in the status text. On success the token +
    /// user are kept in RAM (the Setups tab and the key-owner check use them).</summary>
    private async void DiscordLogin_Click(object sender, RoutedEventArgs e)
    {
        var message = await _discordLogin.StartLoginAsync();
        _settingsViewModel.SetDiscordStatus(message);
        _settingsViewModel.RefreshDiscordState();
    }

    /// <summary>Settings "Abmelden" button — clears the in-RAM Discord token + user.</summary>
    private void DiscordLogout_Click(object sender, RoutedEventArgs e)
    {
        _discordLogin.Logout();
        _settingsViewModel.SetDiscordStatus("Abgemeldet.");
        _settingsViewModel.RefreshDiscordState();
    }

    /// <summary>Setups tab "Setups laden" button — fetches the setups for the logged-in
    /// Discord user from the website.</summary>
    private async void SetupsLoad_Click(object sender, RoutedEventArgs e)
    {
        await _setups.LoadAsync();
    }

    /// <summary>Setups tab "Code kopieren" button — copies the selected setup's ERC1 share
    /// code to the clipboard so the player can paste it into F1 26.</summary>
    private void CopyErc1Code_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_setups.Erc1Code))
        {
            return;
        }

        Clipboard.SetText(_setups.Erc1Code);
    }

    /// <summary>Shows the „Ergebnis an erdi-erc.de senden?" dialog for a finished league
    /// race. Called on the UI thread (Dispatcher-marshaled from ErcRacePromptService); the
    /// dialog is modal over this window.</summary>
    private void ShowErcSendDialog(ErcRaceEndedEvent raceEnded)
    {
        // A race can end while the app sits in the tray — bring the dashboard up first so
        // the user sees the prompt and the dialog has an owner.
        if (!IsVisible)
        {
            OpenDashboard();
        }

        _tray.Notify("Liga-Rennen beendet", "Ergebnis an erdi-erc.de senden?");
        new ErcSendRaceDialog(raceEnded, _services.ErcRace) { Owner = this }.ShowDialog();
    }

    /// <summary>Writes the masked API key into the VM as the user types. The equality guard
    /// keeps seeding (constructor, reset) from marking the tab dirty with a no-op write.</summary>
    private void LlmApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (LlmApiKeyBox.Password != _settingsViewModel.LlmApiKey)
        {
            _settingsViewModel.LlmApiKey = LlmApiKeyBox.Password;
        }
    }

    private void LlmApiKeyShow_Changed(object sender, RoutedEventArgs e) =>
        ToggleSecret(LlmApiKeyBox, LlmApiKeyText, LlmApiKeyShow);

    /// <summary>Writes the masked ERC key into the VM as the user types (same equality guard
    /// as the LLM key: seeding must not mark the tab dirty).</summary>
    private void ErcApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ErcApiKeyBox.Password != _settingsViewModel.ErcApiKey)
        {
            _settingsViewModel.ErcApiKey = ErcApiKeyBox.Password;
        }
    }

    private void ErcApiKeyShow_Changed(object sender, RoutedEventArgs e) =>
        ToggleSecret(ErcApiKeyBox, ErcApiKeyText, ErcApiKeyShow);

    /// <summary>Swaps a masked PasswordBox and its bound TextBox when the "Zeigen" checkbox
    /// toggles, copying the value between them so the VM stays the single source of truth.
    /// The copy is guarded so a reveal/hide with an unchanged value never marks the tab dirty.</summary>
    private static void ToggleSecret(PasswordBox box, TextBox text, CheckBox show)
    {
        if (show.IsChecked == true)
        {
            if (text.Text != box.Password)
            {
                text.Text = box.Password; // copy into the bound field, then reveal
            }
            box.Visibility = Visibility.Collapsed;
            text.Visibility = Visibility.Visible;
            text.Focus();
        }
        else
        {
            box.Password = text.Text; // copy back (fires PasswordChanged → VM)
            text.Visibility = Visibility.Collapsed;
            box.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Attaches the shared rename ContextMenu to a standings row. Built in code
    /// because Click attributes inside a Style Setter.Value generate broken BAML connect
    /// code (InvalidCastException at startup). Rows are recycled — the first LoadingRow
    /// call assigns the menu, later calls keep it.</summary>
    private void AttachRowMenu(System.Windows.Controls.DataGridRow row)
    {
        if (row.ContextMenu is not null)
        {
            return;
        }

        var menu = new ContextMenu();
        var rename = new MenuItem
        {
            Header = "Rename driver…",
            ToolTip = "Replaces a placeholder name like 'Car 22' everywhere (HUD, overlays, history)",
        };
        rename.Click += RenameDriver_Click;
        var clear = new MenuItem { Header = "Clear rename" };
        clear.Click += ClearRenameDriver_Click;
        menu.Items.Add(rename);
        menu.Items.Add(clear);
        row.ContextMenu = menu;
    }

    private void RenameDriver_Click(object sender, RoutedEventArgs e)
    {
        var row = ResolveClickedRow(sender);
        if (row is not null)
        {
            EditDriverOverride(row.CarIndex, clearOverride: false);
        }
    }

    private void ClearRenameDriver_Click(object sender, RoutedEventArgs e)
    {
        var row = ResolveClickedRow(sender);
        if (row is not null)
        {
            EditDriverOverride(row.CarIndex, clearOverride: true);
        }
    }

    /// <summary>The standings rows carry only the display name — the store's driver list
    /// also has the original game name that the override map is keyed on.</summary>
    private static StandingsRow? ResolveClickedRow(object sender)
    {
        return sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } }
            ? target.DataContext as StandingsRow
            : null;
    }

    private void EditDriverOverride(byte carIndex, bool clearOverride)
    {
        var driver = _services.SessionStore.BuildSnapshot()
            .Drivers.FirstOrDefault(d => d.CarIndex == carIndex);
        var gameName = driver?.GameName ?? driver?.Name;
        if (string.IsNullOrEmpty(gameName))
        {
            return;
        }

        var current = _settingsService.Current.DriverNameOverrides;
        string newName = clearOverride
            ? string.Empty
            : PromptNewName(gameName, FindOverride(current, gameName) ?? string.Empty);
        if (clearOverride || newName.Length > 0)
        {
            StoreOverride(current, gameName, newName);
        }
    }

    /// <summary>Dialog round-trip; empty input means "drop the override".</summary>
    private string PromptNewName(string gameName, string currentName)
    {
        var dialog = new RenameDriverDialog(gameName, currentName);
        if (dialog.ShowDialog() != true)
        {
            return string.Empty;
        }

        // A blank OK is the documented "back to the game name" path.
        return dialog.DriverName.Length > AppSettings.MaxNameOverrideLength
            ? dialog.DriverName[..AppSettings.MaxNameOverrideLength]
            : dialog.DriverName;
    }

    private void StoreOverride(
        IReadOnlyDictionary<string, string>? current, string gameName, string newName)
    {
        var map = current is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        if (newName.Length == 0)
        {
            map.Remove(gameName);
        }
        else
        {
            map[gameName] = newName;
        }

        _settingsService.Update(s => s with
        {
            DriverNameOverrides = map.Count == 0 ? null : map,
        });
        _services.SessionStore.SetNameOverrides(map.Count == 0 ? null : map);
        _settingsViewModel.RefreshNameOverrideCount();
    }

    private static string? FindOverride(IReadOnlyDictionary<string, string>? overrides, string gameName)
    {
        if (overrides is null)
        {
            return null;
        }

        foreach (var (key, value) in overrides)
        {
            if (string.Equals(key, gameName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowState(); // remember size/position even when hiding to tray
        if (!_exiting && _settingsService.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Discord-style apply-on-exit: a staged delta update is applied by the hidden
        // script, which waits for this process to end, replaces the files and restarts
        // the app. No Shutdown() here — the window is already closing.
        if (_deltaReady)
        {
            _deltaReady = false;
            _update.StartDeltaUpdater(UpdateService.ApplyScriptPath());
        }

        if (_teardownStarted)
        {
            // Second pass — Application.Shutdown() (below) closes this window again after
            // the background teardown finished. Everything is down; let the window go.
            base.OnClosing(e);
            return;
        }

        // First pass: defer the real close so the serial service teardown runs off the
        // UI thread. Each Dispose can wait up to seconds (Kestrel stop, channel drains,
        // clip-encode kill, DB flush); done inline, closing froze the app briefly
        // (MEDIUM, 2026-09-16). Window- and hwnd-affine teardown stays on the UI thread.
        _teardownStarted = true;
        e.Cancel = true;
        Hide();

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.UnregisterHotKey(hwnd, Win32Interop.OverlayHotKeyId);
        Win32Interop.UnregisterHotKey(hwnd, Win32Interop.RivalHotKeyId);
        Win32Interop.UnregisterHotKey(hwnd, Win32Interop.DebugHotKeyId);
        Win32Interop.UnregisterHotKey(hwnd, Win32Interop.MapHotKeyId);
        for (var i = 0; i < NavKeys.Length; i++)
        {
            Win32Interop.UnregisterHotKey(hwnd, Win32Interop.NavHotKeyBase + i);
        }
        _inGameOverlay.Close();
        _dashboard.Dispose();
        _debug.Dispose();

        _ = TeardownAndExitAsync();
    }

    /// <summary>Finishes the shutdown after <see cref="OnClosing"/> deferred it: the
    /// service teardown (none of it touches the UI) runs on a background thread, then
    /// the tray icon and the final Shutdown are marshalled back to the UI thread. Running
    /// this chain inline kept the UI frozen for the whole multi-second teardown.</summary>
    private async Task TeardownAndExitAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                _overlay.Dispose();
                _voiceAlerts.Dispose(); // before _services.Dispose() — it reads the VoiceEvents channel
                _twitchChat.Dispose(); // before _services.Dispose() — it reads the TwitchSnapshots channel
                _discordLogin.Dispose(); // cancels the login poll loop
                _ercPrompts.Dispose(); // before _services.Dispose() — it reads the ErcRaceEnded channel
                _services.Dispose();
            });
        }
        catch (Exception ex)
        {
            // A partially torn-down service must not leave the process running headless —
            // log and shut down regardless.
            App.Log($"Shutdown teardown failed: {ex}");
        }

        await Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _tray.Dispose();
            }
            catch (Exception ex)
            {
                // A tray dispose failure (NotifyIcon in a tray-less session / shell quirk)
                // must not strand the hidden headless process holding the UDP port and the
                // single-instance mutex — Shutdown still runs (MEDIUM, 2026-09-16).
                App.Log($"Tray dispose failed: {ex.Message}");
            }
            finally
            {
                Application.Current.Shutdown();
            }
        });
    }
}
