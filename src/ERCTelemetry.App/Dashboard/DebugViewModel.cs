using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows.Threading;
using F1Game.UDP.Enums;
using ERCTelemetry.App.Composition;
using ERCTelemetry.App.OverlayServer;
using ERCTelemetry.Core.Networking;
using ERCTelemetry.Core.Telemetry;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Drives the Debug tab: live packet-type counts and per-second rates,
/// format banner, and the recording toggle.</summary>
public sealed class DebugViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppServices _services;
    private readonly AppSettingsService _settings;
    private readonly OverlayWebHost _overlay;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _timer;
    private readonly PacketType[] _types;
    private readonly long[] _lastCounts;
    private readonly CancellationTokenSource _probeStop = new();

    private string _listenerStatus = "Stopped";
    private string? _formatWarning;
    private string _totals = "—";
    private string _overlayStatus = "Overlay: starting…";
    private string _gamePillText = "Waiting for packets…";
    private bool _isReceivingPackets;
    private bool _isRecording;
    private string _recordPath = string.Empty;
    private long _lastPillPacketCount;
    private int _stallTicks;
    private DateTime? _receivingSince;
    private bool _isListening;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DebugViewModel(AppServices services, OverlayWebHost overlay, TrayIconService tray,
        AppSettingsService settings)
    {
        _services = services;
        _overlay = overlay;
        _tray = tray;
        _settings = settings;
        _types = (PacketType[])Enum.GetValues(typeof(PacketType));
        _lastCounts = new long[_types.Length];
        Rows = new ObservableCollection<PacketTypeRow>(_types.Select(t => new PacketTypeRow(t)));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start(); // always on so the header lamp and the setup checklist stay live
        MemoryProbe.Start(_probeStop.Token); // RAM-Diagnose — selbst-begrenzt, beim Shutdown gestoppt
    }

    public ObservableCollection<PacketTypeRow> Rows { get; }

    public string ListenerStatus
    {
        get => _listenerStatus;
        private set => SetField(ref _listenerStatus, value);
    }

    public string? FormatWarning
    {
        get => _formatWarning;
        private set => SetField(ref _formatWarning, value);
    }

    public string Totals
    {
        get => _totals;
        private set => SetField(ref _totals, value);
    }

    /// <summary>Live status of the OBS overlay web server (port + connected clients).</summary>
    public string OverlayStatus
    {
        get => _overlayStatus;
        private set => SetField(ref _overlayStatus, value);
    }

    /// <summary>Text of the prominent game-status pill shown above the tabs.</summary>
    public string GamePillText
    {
        get => _gamePillText;
        private set => SetField(ref _gamePillText, value);
    }

    /// <summary>True while packets flow — drives the pill's green vs. waiting styling.</summary>
    public bool IsReceivingPackets
    {
        get => _isReceivingPackets;
        private set => SetField(ref _isReceivingPackets, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set => SetField(ref _isRecording, value);
    }

    /// <summary>Whether the UDP listener runs — drives the header toggle button and lamp.</summary>
    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (_isListening != value)
            {
                _isListening = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsListening)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListenerButtonText)));
            }
        }
    }

    /// <summary>Header toggle caption: the next action, not the current state.</summary>
    public string ListenerButtonText => IsListening ? "Stop listening" : "Start listening";

    /// <summary>Setup checklist: overlay web server is up.</summary>
    public bool SetupOverlayLive => _overlay.IsRunning;

    /// <summary>Setup checklist: at least one OBS browser source is connected.</summary>
    public bool SetupObsConnected => _overlay.ClientCount > 0;

    /// <summary>Update card: human-readable status ("Version 1.0.0 ist aktuell",
    /// "Update auf 1.0.1 verfügbar", "Server nicht erreichbar"…).</summary>
    public string UpdateStatusText { get; private set; } = "Updates werden geprüft…";

    /// <summary>True when the server manifest offers a newer version — drives the
    /// install button's visibility and the header banner.</summary>
    public bool UpdateAvailable { get; private set; }

    /// <summary>Short headline for the header update banner (e.g. "Version 0.2.1 ist
    /// verfügbar"); empty while nothing is offered.</summary>
    public string UpdateBannerText { get; private set; } = string.Empty;

    /// <summary>Download progress 0–100, only meaningful while a download runs.</summary>
    public int UpdateProgress { get; private set; }

    /// <summary>True while the Setup.exe downloads — drives progress bar and buttons.</summary>
    public bool IsUpdateDownloading { get; private set; }

    /// <summary>Called by MainWindow's update check / download flow. An optional banner
    /// headline fills the header banner; null/empty hides it.</summary>
    public void UpdateUpdateStatus(string text, bool available, string? bannerText = null)
    {
        UpdateStatusText = text;
        UpdateAvailable = available;
        UpdateBannerText = bannerText ?? string.Empty;
        Changed(nameof(UpdateStatusText));
        Changed(nameof(UpdateAvailable));
        Changed(nameof(UpdateBannerText));
    }

    /// <summary>Called from the download loop (Dispatcher-marshalled by IProgress).</summary>
    public void UpdateDownloadProgress(int percent, bool downloading)
    {
        UpdateProgress = Math.Clamp(percent, 0, 100);
        IsUpdateDownloading = downloading;
        Changed(nameof(UpdateProgress));
        Changed(nameof(IsUpdateDownloading));
    }

    /// <summary>Debug-tab replay row: current replay state; empty = nothing to show.</summary>
    public string ReplayStatus { get; private set; } = string.Empty;

    /// <summary>Best LAN IPv4 of this machine — the address to enter in the game's UDP
    /// telemetry settings on a console. "—" when no usable network exists. Refreshed
    /// per tick (DHCP/wifi changes, dock/undock).</summary>
    public string LanIp { get; private set; } = "…";

    /// <summary>Remaining LAN IPv4s (multi-homed machines), comma-separated; empty when
    /// there is only one. Bound to a tooltip on the IP display.</summary>
    public string LanIpAlternatives { get; private set; } = string.Empty;

    /// <summary>Raw preferred LAN IP (null when none) for the copy button in code-behind.</summary>
    public string? PreferredLanIpRaw { get; private set; }

    /// <summary>UDP port to enter in the game: the port the listener actually bound to,
    /// else the configured one (shown before "Start listening" is pressed).</summary>
    public string ConsolePortText => (_services.ListeningPort ?? _settings.Current.UdpPort).ToString();

    /// <summary>Selected .f1rec file (set by the Debug tab's file picker in code-behind).</summary>
    public string? ReplayPath { get; set; }

    public void StartReplay(double speed)
    {
        if (string.IsNullOrWhiteSpace(ReplayPath))
        {
            ReplayStatus = "Wähle zuerst eine .f1rec-Datei";
            Changed(nameof(ReplayStatus));
        }

        try
        {
            _services.StartReplay(ReplayPath!, speed);
            ReplayStatus = "Replay gestartet";
        }
        catch (Exception ex)
        {
            ReplayStatus = $"Replay fehlgeschlagen: {ex.Message}";
            Changed(nameof(ReplayStatus));
        }
    }

    public void StopReplay()
    {
        _services.StopReplay();
        ReplayStatus = "Replay gestoppt";
        Changed(nameof(ReplayStatus));
    }

    private void Changed(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Transient hint after copying an overlay URL (auto-clears on the next refresh).</summary>
    public string SetupCopied
    {
        get => _setupCopied;
        private set => SetField(ref _setupCopied, value);
    }
    private string _setupCopied = string.Empty;

    /// <summary>Called by the setup tab's copy buttons; the hint clears itself on the next tick.</summary>
    public void ShowSetupCopied(string page)
    {
        SetupCopied = $"URL für '{page}' kopiert — in OBS einfügen (Strg+V)";
    }

    /// <summary>Called by the setup tab's copy buttons when the overlay server is down;
    /// the hint clears itself on the next tick.</summary>
    public void ShowOverlayNotRunning(string page)
    {
        SetupCopied = $"Overlay läuft nicht — Port in den Einstellungen starten, dann URL für '{page}' kopieren";
    }

    /// <summary>Transient hint after copying plain text (LAN IP / UDP port) on the setup tab.</summary>
    public void ShowTextCopied(string what)
    {
        SetupCopied = $"'{what}' kopiert — mit Strg+V im Spiel einfügen";
    }

    /// <summary>Setup tab's copy-IP click when the machine has no usable LAN address.</summary>
    public void ShowNoLanIp()
    {
        SetupCopied = "Keine LAN-IP gefunden — ist dieser PC mit dem Netzwerk (WLAN/LAN) verbunden?";
    }

    public string RecordPath
    {
        get => _recordPath;
        set => SetField(ref _recordPath, value);
    }

    public void StartListening(string portText)
    {
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            ListenerStatus = "Invalid port";
            return;
        }

        try
        {
            _services.StartListening(port);
            ListenerStatus = $"Listening on UDP {_services.ListeningPort?.ToString() ?? port.ToString()}";
        }
        catch (InvalidOperationException ex)
        {
            ListenerStatus = ex.Message;
            _tray.Notify("ERCTelemetry — cannot listen", ex.Message);
        }
        finally
        {
            IsListening = _services.IsListening;
        }
    }

    public void StopListening()
    {
        _services.StopListening();
        ListenerStatus = "Stopped";
        IsListening = false;
        Refresh();
    }

    public void ToggleRecording()
    {
        if (_services.IsRecording)
        {
            _services.StopRecording();
            IsRecording = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(RecordPath))
        {
            RecordPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ERCTelemetry", $"recording-{DateTime.Now:yyyyMMdd-HHmmss}.f1rec");
        }

        try
        {
            _services.StartRecording(RecordPath);
            IsRecording = true;
        }
        catch (InvalidOperationException)
        {
            // A recording file was already open; keep previous state.
        }
    }

    private void Refresh()
    {
        UpdateGamePill();
        IsListening = _services.IsListening;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SetupOverlayLive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SetupObsConnected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConsolePortText)));
        SetupCopied = string.Empty; // copy hint is stale one tick later
        RefreshLanIp();

        if (_overlay.IsRunning && _overlay.Port > 0)
        {
            OverlayStatus = $"Overlay: http://127.0.0.1:{_overlay.Port} · " +
                            $"{_overlay.ClientCount} client(s)";
        }
        else
        {
            // Port is only set after the Kestrel bind completes — IsRunning alone
            // would render a "…:0" URL during that bind window.
            OverlayStatus = _overlay.IsRunning ? "Overlay: starting…" : "Overlay: stopped";
        }

        if (!_services.IsListening)
        {
            FormatWarning = null; // don't keep a stale banner while the listener is off

            // Replay row (listener is off during a replay anyway).
            ReplayStatus = _services.IsReplaying
                ? $"Replay läuft · {_services.Replay?.Progress?.Frame ?? 0} Frames"
                : ReplayStatus == "Replay gestartet" ? string.Empty : ReplayStatus;
            Changed(nameof(ReplayStatus));

            Totals = "—";
            return;
        }

        var wasUnsupported = FormatWarning is not null;

        FormatWarning = _services.FormatState switch
        {
            FormatState.Unsupported =>
                "Unsupported telemetry format. Set the game's UDP telemetry format to " +
                "'2026 Season Pack' (F1 25 needs the Season Pack update installed).",
            _ => null,
        };

        // Toast once on the transition into the unsupported state — not on every refresh tick.
        if (!wasUnsupported && FormatWarning is not null)
        {
            _tray.Notify("ERCTelemetry — telemetry format", FormatWarning);
        }

        var stats = _services.Stats;
        for (var i = 0; i < _types.Length; i++)
        {
            var count = stats.GetCount(_types[i]);
            Rows[i].Update(count.ToString("N0"), $"{count - _lastCounts[i]:0}/s");
            _lastCounts[i] = count;
        }

        Totals = $"{stats.TotalPackets:N0} packets · {stats.TotalBytes / 1024.0:0.0} KiB · " +
                 $"{stats.ParseErrors:N0} parse errors · {stats.FormatErrors:N0} format errors";
    }

    /// <summary>Re-reads the machine's LAN IPv4s (cheap enough at 1 Hz) and pushes the
    /// result to the setup-tab binding only on change — a new IP (DHCP renew, wifi
    /// roam) must be visible before the user types it into the console.</summary>
    private void RefreshLanIp()
    {
        var addresses = LocalIpProvider.GetLanIPv4Addresses();
        PreferredLanIpRaw = addresses.Count > 0 ? addresses[0] : null;

        var lanIpText = PreferredLanIpRaw ?? "—";
        if (LanIp != lanIpText)
        {
            LanIp = lanIpText;
            Changed(nameof(LanIp));
        }

        var alternatives = string.Join(", ", addresses.Skip(1));
        if (LanIpAlternatives != alternatives)
        {
            LanIpAlternatives = alternatives;
            Changed(nameof(LanIpAlternatives));
        }
    }

    /// <summary>Tracks the packet-count tick between refreshes: a rising
    /// <see cref="PacketStats.TotalPackets"/> means the game is streaming. A short stall
    /// (≤ a few seconds, e.g. game pause) keeps the pill green; a lasting stall goes
    /// back to the waiting state with the timer reset.</summary>
    private void UpdateGamePill()
    {
        const int stallTicksBeforeWaiting = 3;

        if (!_services.IsListening)
        {
            _receivingSince = null;
            _stallTicks = 0;
            _lastPillPacketCount = _services.Stats.TotalPackets;
            GamePillText = "Waiting for packets… (listener stopped)";
            IsReceivingPackets = false;
            return;
        }

        var total = _services.Stats.TotalPackets;
        if (total > _lastPillPacketCount)
        {
            _stallTicks = 0;
            _receivingSince ??= DateTime.UtcNow;
            GamePillText = $"Receiving packets · {FormatUptime(DateTime.UtcNow - _receivingSince.Value)}";
            IsReceivingPackets = true;
        }
        else
        {
            _stallTicks++;
            if (_stallTicks >= stallTicksBeforeWaiting)
            {
                _receivingSince = null;
                GamePillText = "Waiting for packets… (no data flowing)";
                IsReceivingPackets = false;
            }
        }

        _lastPillPacketCount = total;
    }

    private static string FormatUptime(TimeSpan age) => age.TotalSeconds < 60
        ? $"{(int)age.TotalSeconds}s"
        : $"{(int)(age.TotalMinutes)}m{age.Seconds:00}s";

    private void SetField<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

    public void Dispose()
    {
        _probeStop.Cancel();
        _timer.Stop();
    }

    /// <summary>One row in the per-packet-type table.</summary>
    public sealed class PacketTypeRow(PacketType type) : INotifyPropertyChanged
    {
        private string _count = "0";
        private string _rate = "0/s";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Type { get; } = type.ToString();

        public string Count
        {
            get => _count;
            private set { _count = value; Changed(); }
        }

        public string Rate
        {
            get => _rate;
            private set { _rate = value; Changed(); }
        }

        public void Update(string count, string rate)
        {
            Count = count;
            Rate = rate;
        }

        private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }
}