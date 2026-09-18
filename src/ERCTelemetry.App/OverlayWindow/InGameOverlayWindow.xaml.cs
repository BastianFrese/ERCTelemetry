using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using F1Game.UDP.Enums;
using ERCTelemetry.App.Composition;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Tracks;

namespace ERCTelemetry.App.OverlayWindow;

/// <summary>Transparent, click-through HUD rendered over the (borderless-windowed) game:
/// position/name/gap, speed/gear, RPM, pedals, ERS %, DRS/tyre/fuel, optional minimap and
/// weather row, auto-cycling rival panel. Reads the overlay window's own snapshot channel
/// on a 30 fps DispatcherTimer. Widget groups, size preset, color scheme and position
/// persist in settings.json; drag-positioning via position mode.</summary>
public partial class InGameOverlayWindow : Window
{
    private const double TargetFps = 30;
    private const ushort RpmRedline = 12000;
    private const double LargeScale = 1.3;

    /// <summary>How long the rival panel stays up once shown (auto cycle or hotkey).</summary>
    private const long RivalShowMs = 5_000;

    /// <summary>How often Topmost is re-asserted — games running fullscreen-optimized can
    /// demote TOPMOST windows.</summary>
    private const long ReassertTopmostMs = 2_000;

    /// <summary>Padding around the minimap track bounds, in canvas pixels.</summary>
    private const double MapPadding = 20;

    /// <summary>Max rows the timing tower shows (top of the field, like the broadcast).</summary>
    private const int TowerMaxRows = 12;

    private readonly AppServices _services;
    private readonly AppSettingsService? _settings; // null in designer-only scenarios
    private readonly DispatcherTimer _timer;
    private TelemetrySnapshot? _latest;
    private long _lastFrameVersion;
    private bool _positionMode;

    // Rival-panel phase state (TickCount64-based, no extra timer).
    private long _forcedWindowUntil;
    private long _rivalPhaseBase;
    private bool _rivalPhaseBaseSet;

    // Free widget layout: id → widget Border; positions persist in settings.
    private readonly Dictionary<string, Border> _widgets = new();
    private bool _dragging;
    private System.Windows.Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;
    private Border? _dragTarget;
    private List<StoreEvent> _feed = new();
    private bool _layoutDone; // auto stack layout runs once, then saved positions win

    // Minimap working state — expand-only bounds, reset when the session changes.
    private ulong _mapSessionUid;
    private double _mapMinX;
    private double _mapMaxX;
    private double _mapMinZ;
    private double _mapMaxZ;
    private bool _mapHasBounds;
    private Ellipse[]? _mapDots;

    // Fitted-layout minimap state: this window's own TrackFitter (one instance per
    // consumer — the overlay pump owns a separate one), the session's layout and the
    // one-time drawn outline/start-finish shapes. Bounds cache the layout's world
    // extent so outline and dots project through the SAME fixed mapping after the lock.
    private TrackFitter? _mapFitter;
    private TrackLayout? _mapLayout;
    private Polyline? _mapTrackLine;
    private System.Windows.Shapes.Line? _mapStartFinish;
    private (double X, double Z)[]? _mapLayoutWorld;
    private double _mapFitMinX;
    private double _mapFitMaxX;
    private double _mapFitMinZ;
    private double _mapFitMaxZ;

    // Timing-tower change detection: the tower is rebuilt only when the visible state
    // (positions, names, sector marks, gaps) actually changes, not on every 30 fps tick.
    private string _towerSignature = string.Empty;

    public InGameOverlayWindow(AppServices services, AppSettingsService? settings = null)
    {
        InitializeComponent();
        _services = services;
        _settings = settings;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps),
        };
        _timer.Tick += (_, _) => ConsumeSnapshots();

        if (_settings is not null)
        {
            ApplyHudSettings(_settings.Current);
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (_positionMode && _dragTarget is null)
            {
                // empty canvas area: whole-HUD fallback drag (moves every widget)
                DragMove();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _positionMode)
            {
                EndPositionMode(save: true);
            }
        };

        RegisterWidgets();
    }

    /// <summary>Collects the widget Borders by id and hooks per-widget drag handlers.</summary>
    private void RegisterWidgets()
    {
        AddWidget("timing", WdgTiming);
        AddWidget("drive", WdgDrive);
        AddWidget("status", WdgStatus);
        AddWidget("map", WdgMap);
        AddWidget("rival", WdgRival);
        AddWidget("battles", WdgBattles);
        AddWidget("feed", WdgFeed);
        AddWidget("race", WdgRace);
        AddWidget("radar", WdgRadar);
        AddWidget("gmeter", WdgGmeter);
        AddWidget("tower", WdgTower);
    }

    private void AddWidget(string id, Border widget)
    {
        _widgets[id] = widget;
        widget.LayoutTransform = _widgetScale;
        widget.MouseLeftButtonDown += (_, e) => BeginWidgetDrag(id, widget, e);
        widget.MouseMove += (_, e) => MoveWidgetDrag(widget, e);
        widget.MouseLeftButtonUp += (_, e) => EndWidgetDrag(widget, e);
    }

    private void BeginWidgetDrag(string id, Border widget, MouseButtonEventArgs e)
    {
        if (!_positionMode)
        {
            return;
        }

        EnsureWidgetPosition(widget);
        _dragTarget = widget;
        _dragging = true;
        _dragStartMouse = e.GetPosition(this);
        _dragStartLeft = Canvas.GetLeft(widget);
        _dragStartTop = Canvas.GetTop(widget);
        widget.CaptureMouse();
        e.Handled = true;
    }

    private void MoveWidgetDrag(Border widget, MouseEventArgs e)
    {
        if (!_dragging || !ReferenceEquals(_dragTarget, widget))
        {
            return;
        }

        var pos = e.GetPosition(this);
        widget.BeginInit();
        Canvas.SetLeft(widget, ClampLeft(widget, _dragStartLeft + pos.X - _dragStartMouse.X));
        Canvas.SetTop(widget, ClampTop(widget, _dragStartTop + pos.Y - _dragStartMouse.Y));
        widget.EndInit();
        e.Handled = true;
    }

    private void EndWidgetDrag(Border widget, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(_dragTarget, widget))
        {
            return;
        }

        _dragging = false;
        _dragTarget = null;
        widget.ReleaseMouseCapture();
        SaveWidgetPositions();
        e.Handled = true;
    }

    /// <summary>Gives a widget without a real canvas position its auto-layout spot
    /// (top-right, below the lowest positioned widget). Never-shown widgets (battles,
    /// feed) start Collapsed with NaN positions — rendering them without this puts them
    /// at (0,0) and every drag offset computes NaN, so the widget is stuck.</summary>
    private void EnsureWidgetPosition(Border widget)
    {
        var left = Canvas.GetLeft(widget);
        var top = Canvas.GetTop(widget);
        if (!double.IsNaN(left) && left >= 0 && !double.IsNaN(top) && top >= 0)
        {
            return;
        }

        var y = 24.0;
        foreach (var other in _widgets.Values)
        {
            if (ReferenceEquals(other, widget) || other.Visibility != Visibility.Visible)
            {
                continue;
            }

            var otherLeft = Canvas.GetLeft(other);
            var otherTop = Canvas.GetTop(other);
            if (double.IsNaN(otherLeft) || otherLeft < 0 || double.IsNaN(otherTop) || otherTop < 0)
            {
                continue;
            }

            y = Math.Max(y, otherTop + other.ActualHeight + 10);
        }

        Canvas.SetLeft(widget, Math.Max(8, SystemParameters.WorkArea.Width - 360));
        Canvas.SetTop(widget, y);
    }

    private double ClampLeft(Border widget, double left)
    {
        var max = Math.Max(0, ActualWidth - widget.ActualWidth);
        return Math.Clamp(double.IsNaN(Canvas.GetLeft(widget)) ? 0 : left, 0, max);
    }

    private double ClampTop(Border widget, double top)
    {
        var max = Math.Max(0, ActualHeight - widget.ActualHeight);
        return Math.Clamp(double.IsNaN(Canvas.GetTop(widget)) ? 0 : top, 0, max);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32Interop.AddExtendedStyle(
            hwnd,
            Win32Interop.WsExTransparent | Win32Interop.WsExNoActivate | Win32Interop.WsExToolWindow);

        // The window spans the whole work area; the widgets are positioned on its canvas.
        Left = 0;
        Top = 0;
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;
        ApplyWidgetPositions();
    }

    /// <summary>Places every widget: saved [x, y] from settings when present, else stacked
    /// top-right in registration order (applied once after first layout, when sizes exist).</summary>
    private void ApplyWidgetPositions()
    {
        var saved = _settings?.Current.HudWidgetPositions;
        foreach (var (id, widget) in _widgets)
        {
            if (double.IsNaN(Canvas.GetLeft(widget)) || double.IsNaN(Canvas.GetTop(widget)))
            {
                Canvas.SetLeft(widget, double.NaN);
                Canvas.SetTop(widget, double.NaN);
                _layoutDone = false; // needs the auto stack pass
                continue;
            }
        }

        if (saved is not null)
        {
            foreach (var (id, xy) in saved)
            {
                if (_widgets.TryGetValue(id, out var widget))
                {
                    Canvas.SetLeft(widget, xy[0]);
                    Canvas.SetTop(widget, xy[1]);
                }
            }
        }
        else
        {
            // No saved layout (fresh start or preset reset) — drop every widget back to
            // the auto stack instead of keeping a stale dragged position.
            foreach (var (_, widget) in _widgets)
            {
                Canvas.SetLeft(widget, double.NaN);
                Canvas.SetTop(widget, double.NaN);
            }

            _layoutDone = false;
        }

        if (!_layoutDone)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyDefaultStackLayout);
        }
    }

    /// <summary>One-time auto layout: stack widgets top-right in id order using their
    /// measured heights; only runs while nothing is saved yet / newly enabled widgets.</summary>
    private void ApplyDefaultStackLayout()
    {
        var x = Math.Max(8, SystemParameters.WorkArea.Width - 360);
        var y = 24.0;
        foreach (var widget in _widgets
                     .Where(kvp => kvp.Value.Visibility == Visibility.Visible)
                     .Select(kvp => kvp.Value))
        {
            if (!double.IsNaN(Canvas.GetLeft(widget)) && Canvas.GetLeft(widget) >= 0)
            {
                continue; // widget already has a real position
            }

            Canvas.SetLeft(widget, x);
            Canvas.SetTop(widget, y);
            y += widget.ActualHeight + 10;
        }

        _layoutDone = true;
    }

    /// <summary>Begins consuming snapshots; called when the window is shown. Created hidden,
    /// so nothing runs while the user plays with the HUD off.</summary>
    public void Start()
    {
        EnsureTopmost();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Stops the snapshot timer on close — before this, the window closed but the
    /// 30 fps timer kept ticking (busy channel polling) for the whole multi-second app
    /// teardown until process exit (LOW, 2026-09-16).</summary>
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    /// <summary>Re-asserts HWND_TOPMOST so the HUD stays above the game window (some
    /// fullscreen-optimized game windows demote TOPMOST z-order).</summary>
    private void EnsureTopmost()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        Win32Interop.SetWindowPos(
            hwnd, Win32Interop.HwndTopmost, 0, 0, 0, 0,
            Win32Interop.SwpNosize | Win32Interop.SwpNomove | Win32Interop.SwpNoactivate);
    }

    /// <summary>Applies the persisted HUD preferences (widget groups, size preset,
    /// saved position when not currently dragging).</summary>
    public void ApplyHudSettings(AppSettings settings)
    {
        TimingRow.Visibility = settings.HudShowTiming ? Visibility.Visible : Visibility.Collapsed;
        DriveRow.Visibility = settings.HudShowDrive ? Visibility.Visible : Visibility.Collapsed;
        RpmRow.Visibility = settings.HudShowDrive ? Visibility.Visible : Visibility.Collapsed;
        ThrRow.Visibility = settings.HudShowDrive ? Visibility.Visible : Visibility.Collapsed;
        BrkRow.Visibility = settings.HudShowDrive ? Visibility.Visible : Visibility.Collapsed;
        ErsRow.Visibility = settings.HudShowDrive ? Visibility.Visible : Visibility.Collapsed;
        StatusRow.Visibility = settings.HudShowStatus ? Visibility.Visible : Visibility.Collapsed;
        WeatherRow.Visibility = settings.HudShowWeather ? Visibility.Visible : Visibility.Collapsed;
        WdgStatus.Visibility = settings.HudShowStatus || settings.HudShowWeather
            ? Visibility.Visible
            : Visibility.Collapsed;
        WdgMap.Visibility = settings.HudShowMap ? Visibility.Visible : Visibility.Collapsed;
        WdgRace.Visibility = settings.HudShowRace ? Visibility.Visible : Visibility.Collapsed;
        WdgRadar.Visibility = settings.HudShowRadar ? Visibility.Visible : Visibility.Collapsed;
        WdgGmeter.Visibility = settings.HudShowGmeter ? Visibility.Visible : Visibility.Collapsed;
        WdgTower.Visibility = settings.HudShowTower ? Visibility.Visible : Visibility.Collapsed;
        RaceReset(); // no session data yet — every line collapsed

        // Widget frames follow their group toggles — a disabled group must not leave an
        // empty bordered box on the canvas.
        WdgTiming.Visibility = settings.HudShowTiming ? Visibility.Visible : Visibility.Collapsed;
        WdgDrive.Visibility = settings.HudShowDrive ? Visibility.Visible : Visibility.Collapsed;
        WdgRival.Visibility = settings.HudShowRival ? Visibility.Visible : Visibility.Collapsed;

        ApplyScheme(settings.ColorScheme);

        // Size preset scales every widget (one shared, live-updated transform).
        _widgetScale.ScaleX = _widgetScale.ScaleY =
            settings.HudLayout == HudLayout.Large ? LargeScale : 1.0;

        ApplyWidgetPositions();

        // A group enabled mid-session (e.g. the map) has no position yet — place it.
        foreach (var widget in _widgets.Values)
        {
            if (widget.Visibility == Visibility.Visible)
            {
                EnsureWidgetPosition(widget);
            }
        }
    }

    /// <summary>Global hotkey (Ctrl+Shift+M): flips the persisted HUD map toggle and
    /// re-applies — identical to flipping the checkbox in Settings → HUD.</summary>
    public void ToggleMapWidget()
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Update(s => s with { HudShowMap = !s.HudShowMap });
        ApplyHudSettings(_settings.Current);
    }

    private readonly ScaleTransform _widgetScale = new();

    /// <summary>Enables drag-positioning: makes the widgets hit-testable, drops the
    /// click-through style and highlights every widget border. Esc saves and ends.</summary>
    public void BeginPositionMode()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _positionMode = true;
        IsHitTestVisible = true;
        Win32Interop.RemoveExtendedStyle(hwnd, Win32Interop.WsExTransparent);
        foreach (var widget in _widgets.Values)
        {
            widget.BorderBrush = (Brush)Resources["HudGold"];
            widget.BorderThickness = new Thickness(2);
        }
    }

    /// <summary>Back to click-through, persisting the last saved widget positions.</summary>
    public void EndPositionMode(bool save)
    {
        if (!_positionMode)
        {
            return;
        }

        _positionMode = false;
        _dragging = false;
        _dragTarget = null;
        IsHitTestVisible = false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.AddExtendedStyle(hwnd, Win32Interop.WsExTransparent | Win32Interop.WsExNoActivate | Win32Interop.WsExToolWindow);
        }

        foreach (var widget in _widgets.Values)
        {
            widget.SetResourceReference(Border.BorderBrushProperty, "HudBorder");
            widget.BorderThickness = new Thickness(1);
        }

        if (save)
        {
            SaveWidgetPositions();
        }
    }

    /// <summary>Persists every widget's current canvas position to settings.</summary>
    private void SaveWidgetPositions()
    {
        if (_settings is null)
        {
            return;
        }

        Dictionary<string, double[]> positions = new(StringComparer.Ordinal);
        foreach (var (id, widget) in _widgets)
        {
            positions[id] = [Canvas.GetLeft(widget), Canvas.GetTop(widget)];
        }

        _settings.Update(s => s with { HudWidgetPositions = positions });
    }

    private void ConsumeSnapshots()
    {
        while (_services.OverlayWindowSnapshots.Reader.TryRead(out var snapshot))
        {
            _latest = snapshot;
        }

        while (_services.OverlayWindowEvents.Reader.TryRead(out var storeEvent))
        {
            // Lap completions arrive every lap/driver — feed keeps only headline events.
            if (storeEvent is LapCompleted { CarIndex: var lapCar } &&
                _latest?.Meta?.PlayerCarIndex == lapCar &&
                _latest.Positions is { } lapPos &&
                lapCar < Math.Min((int)lapPos.Count, TelemetryConstants.MaxCars) &&
                (lapPos.X[lapCar] != 0 || lapPos.Z[lapCar] != 0))
            {
                // Player crossed the lap line: the fitter uses these crossings' median
                // as the start/finish point (needs ≥3).
                _mapFitter?.MarkStartFinishCrossing(lapPos.X[lapCar], lapPos.Z[lapCar]);
            }

            if (storeEvent is not LapCompleted)
            {
                _feed.Add(storeEvent);
                if (_feed.Count > 4)
                {
                    _feed.RemoveAt(0);
                }
            }
        }

        if (Environment.TickCount64 - _lastTopmostAssert >= ReassertTopmostMs)
        {
            _lastTopmostAssert = Environment.TickCount64;
            EnsureTopmost();
        }

        if (_latest is not null && _latest.FrameVersion != _lastFrameVersion)
        {
            _lastFrameVersion = _latest.FrameVersion;
            UpdateHud(_latest);
            UpdateBattles(_latest);
            UpdateFeed();
        }
    }

    /// <summary>Live battle board: consecutive live cars under 1 s apart — one line per
    /// duel, newest on top, at most 3 shown. Widget hides itself when nobody is battling.</summary>
    private void UpdateBattles(TelemetrySnapshot snapshot)
    {
        string[] lines = [];
        if (snapshot.Standings.Count > 1)
        {
            List<string> duels = [];
            for (var i = 1; i < snapshot.Standings.Count; i++)
            {
                var back = snapshot.Standings[i];
                var front = snapshot.Standings[i - 1];
                if (back.PitStatus != PitStatus.None ||
                    back.GapToCarInFrontMs is <= 0 or >= 1000)
                {
                    continue;
                }

                duels.Add($"P{front.Position} {front.Name}  vs  P{back.Position} {back.Name}" +
                          $"  {back.GapToCarInFrontMs / 1000.0:0.000}s");
                if (duels.Count >= 3)
                {
                    break;
                }
            }

            lines = [.. duels];
        }

        if (lines.Length > 0)
        {
            EnsureWidgetPosition(WdgBattles);
        }

        BattlesText.Text = string.Join('\n', lines);
        WdgBattles.Visibility = lines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Last four race-control happenings (penalties, retirements, fastest laps,
    /// speed trap) — shown newest on top; the widget hides itself while the feed is empty.</summary>
    private void UpdateFeed()
    {
        var lines = new List<string>(_feed.Count);
        for (var i = _feed.Count - 1; i >= 0; i--)
        {
            lines.Add(FeedLine(_feed[i]));
        }

        if (lines.Count > 0)
        {
            EnsureWidgetPosition(WdgFeed);
        }

        FeedText.Text = string.Join('\n', lines);
        WdgFeed.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FeedLine(StoreEvent storeEvent) => storeEvent switch
    {
        SessionStarted s => s.Meta is null
            ? "Session started"
            : $"Started: {s.Meta.SessionType} at {s.Meta.Track}",
        SessionEnded => "Session ended",
        LapCompleted l => $"Lap {l.LapNumber}: {l.DriverName} P{l.Position}",
        RaceControl rc => rc.Event.Text,
        _ => string.Empty,
    };

    private long _lastTopmostAssert;

    private void UpdateHud(TelemetrySnapshot snapshot)
    {
        if (snapshot.Player is not { } frame)
        {
            NoTelemetryText.Visibility = Visibility.Visible;
            return;
        }

        NoTelemetryText.Visibility = Visibility.Collapsed;

        // Standings row carries position + gap (updated at packet rate, not 60 Hz).
        var row = snapshot.Standings.FirstOrDefault(r => r.IsPlayer);

        PositionText.Text = row?.Position switch
        {
            null => "—",
            1 => "P1",
            _ => $"P{row.Position}",
        };

        DriverNameText.Text = frame.Name;
        UpdateBattleGaps(snapshot, row);
        UpdateLapLine(row);
        UpdateLapDelta(row);
        UpdateSectorDelta(row);

        SpeedText.Text = frame.Speed.ToString();
        GearText.Text = frame.Gear switch
        {
            < 0 => "R",
            0 => "N",
            _ => frame.Gear.ToString(),
        };

        RpmFill.Width = Math.Clamp(frame.EngineRpm / (double)RpmRedline, 0, 1)
                        * ((Border)RpmFill.Parent).ActualWidth;

        SetPedalFill(ThrottleFill, frame.Throttle);
        SetPedalFill(BrakeFill, frame.Brake);

        var ersPercent = Math.Clamp(frame.ErsStoreEnergy / TelemetryConstants.MaxErsJoules * 100f, 0f, 100f);
        ErsFill.Width = Math.Clamp(frame.ErsStoreEnergy / TelemetryConstants.MaxErsJoules, 0, 1)
                        * ((Border)ErsFill.Parent).ActualWidth;
        ErsText.Text = $"{ersPercent:0}%";

        UpdateDrs(frame);
        TyreText.Text = CompoundLabel(frame.TyreCompound);

        // Tyre line: age + worst-wheel wear, red once wear gets critical (~70 %).
        var tyres = snapshot.Tyres?.FirstOrDefault(t => t.CarIndex == frame.CarIndex);
        TyreAgeText.Text = tyres is null
            ? $"· {frame.TyreAgeLaps} laps"
            : $"· {frame.TyreAgeLaps} laps · {tyres.WearPercent:0}%";
        var tyreCritical = tyres is { } t && t.WearPercent >= 70f;
        TyreAgeText.Foreground = (Brush)Resources[tyreCritical ? "HudAccent" : "HudMuted"];

        // Fuel: warn red while the remaining laps exceed what the tank still covers.
        var raceLapsLeft = snapshot.Meta is { } meta && meta.TotalLaps > 0 && row is not null
            ? meta.TotalLaps - row.CurrentLapNum
            : 0;
        var fuelCritical = raceLapsLeft > 0 && frame.FuelRemainingLaps + 0.5f < raceLapsLeft;
        FuelText.Text = $"⛽ {frame.FuelInTank:0.0}kg · {frame.FuelRemainingLaps:0.0} laps" +
                        (fuelCritical ? " ⚠" : string.Empty);
        FuelText.Foreground = (Brush)Resources[fuelCritical ? "HudAccent" : "HudMuted"];

        UpdateWeather(snapshot);
        UpdateStrategy(snapshot);
        UpdateMap(snapshot);
        UpdateRadar(snapshot);
        UpdateGmeter(snapshot);
        UpdateRivalPanel(snapshot);
        UpdateRaceInfo(snapshot, row);
        UpdateTower(snapshot);
    }

    /// <summary>Collapses every race-info line (used on toggle and when no player is live).</summary>
    private void RaceReset()
    {
        PitWindowText.Text = string.Empty;
        RaceClockText.Text = string.Empty;
        FlagText.Visibility = Visibility.Collapsed;
        InvalidText.Visibility = Visibility.Collapsed;
        DamageText.Visibility = Visibility.Collapsed;
        TyreBankText.Visibility = Visibility.Collapsed;
        TrafficText.Visibility = Visibility.Collapsed;
        FuelTrendText.Visibility = Visibility.Collapsed;
        FuelAdviceText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The race-day info block: game pit-window hint, session clock, marshal flag
    /// (worst active), lap-invalid badge, body damage, best fresh tyre set, lapped-car
    /// traffic warnings and the per-lap fuel trend. Every line collapses on missing data.</summary>
    private void UpdateRaceInfo(TelemetrySnapshot snapshot, StandingsRow? row)
    {
        if (WdgRace.Visibility != Visibility.Visible)
        {
            return;
        }

        var meta = snapshot.Meta;
        if (meta is null)
        {
            RaceReset();
            return;
        }

        // Pit-window hint — highlighted while the player's current lap is inside it.
        if (meta.PitWindowIdealLap > 0)
        {
            PitWindowText.Text = $"PIT {meta.PitWindowIdealLap}–{meta.PitWindowLatestLap}";
            var inWindow = row != null &&
                           row.CurrentLapNum >= meta.PitWindowIdealLap &&
                           row.CurrentLapNum <= meta.PitWindowLatestLap;
            PitWindowText.Foreground = (Brush)Resources[inWindow ? "HudAccent" : "HudMuted"];
        }
        else
        {
            PitWindowText.Text = string.Empty;
        }

        // Session clock: remaining seconds, or lap counter in lap-based sessions.
        RaceClockText.Text = meta.SessionTimeLeft > 0
            ? TimeSpan.FromSeconds(meta.SessionTimeLeft).ToString(@"h\:mm\:ss")
            : meta.TotalLaps > 0 && row is not null ? $"L {row.CurrentLapNum}/{meta.TotalLaps}" : string.Empty;

        // Marshal flags: worst active zone flag wins (yellow beats blue beats green).
        var flagged = meta.MarshalZones?.Where(z => z.Flag is >= 1).ToList();
        if (flagged is { Count: > 0 })
        {
            var worst = flagged.OrderByDescending(z => z.Flag).First();
            FlagText.Text = worst.Flag switch
            {
                3 => $"YELLOW @ {worst.ZoneStart * 100:0}%",
                2 => $"BLUE @ {worst.ZoneStart * 100:0}%",
                _ => $"GREEN @ {worst.ZoneStart * 100:0}%",
            };
            FlagText.Foreground = (Brush)Resources[worst.Flag switch
            {
                3 => "HudAccent",
                2 => "HudDRS",
                _ => "HudGreen",
            }];
            FlagText.Visibility = Visibility.Visible;
        }
        else
        {
            FlagText.Visibility = Visibility.Collapsed;
        }

        // Invalid-lap badge (corner cut) — removed lap times cost qualifying laps.
        if (row is { LapValidity: 1 })
        {
            InvalidText.Text = "LAP INVALID";
            InvalidText.Visibility = Visibility.Visible;
        }
        else
        {
            InvalidText.Visibility = Visibility.Collapsed;
        }

        ShowDamage(snapshot, row);
        ShowTyreBank(snapshot);
        ShowTraffic(snapshot, row);
        ShowFuelTrend(snapshot, row);
        ShowFuelAdvice(snapshot);
    }

    /// <summary>Compact body-damage line ("which side got hit"), accent at ≥ 20 %.</summary>
    private void ShowDamage(TelemetrySnapshot snapshot, StandingsRow? row)
    {
        if (row is null)
        {
            DamageText.Visibility = Visibility.Collapsed;
            return;
        }

        var damage = snapshot.Damages?.FirstOrDefault(d => d.CarIndex == row.CarIndex);
        if (damage is null)
        {
            DamageText.Visibility = Visibility.Collapsed;
            return;
        }

        var worst = Math.Max(
            Math.Max(damage.FrontLeftWing, damage.FrontRightWing),
            Math.Max(damage.RearWing, Math.Max(damage.Floor, damage.Diffuser)));
        // Show the line only when something actually broke (clean car = less HUD noise).
        DamageText.Visibility = worst > 0 ? Visibility.Visible : Visibility.Collapsed;
        DamageText.Foreground = (Brush)Resources[worst >= 20 ? "HudAccent" : "HudMuted"];
        DamageText.Text = $"DMG L{damage.FrontLeftWing:0} R{damage.FrontRightWing:0} " +
                          $"W{damage.RearWing:0} F{damage.Floor:0}" +
                          (damage.DrsFault ? " · DRS" : string.Empty);
    }

    /// <summary>Best fresh tyre set still in the bank (lowest wear, available, not fitted).</summary>
    private void ShowTyreBank(TelemetrySnapshot snapshot)
    {
        var best = snapshot.TyreSets?
            .Where(s => s.IsAvailable && !s.IsFitted && s.Wear < 10)
            .OrderBy(s => s.Wear)
            .FirstOrDefault();
        if (best is null)
        {
            TyreBankText.Visibility = Visibility.Collapsed;
            return;
        }

        var setsLeft = snapshot.TyreSets!.Count(s => s.IsAvailable && !s.IsFitted && s.Wear < 10);
        TyreBankText.Text = $"SETS {setsLeft} · best {CompoundLabel(best.Compound)} {best.Wear:0}%";
        TyreBankText.Visibility = Visibility.Visible;
        TyreBankText.Foreground = (Brush)Resources["HudMuted"];
    }

    /// <summary>Lapped-car warnings: car directly ahead a lap up (blue flag) or the car
    /// behind about to be lapped (traffic).</summary>
    private void ShowTraffic(TelemetrySnapshot snapshot, StandingsRow? row)
    {
        if (row is null)
        {
            TrafficText.Visibility = Visibility.Collapsed;
            return;
        }

        var ahead = snapshot.Standings.FirstOrDefault(r => r.Position == (byte)(row.Position - 1));
        var behind = snapshot.Standings.FirstOrDefault(r => r.Position == (byte)(row.Position + 1));

        if (ahead is { } a && a.CurrentLapNum > row.CurrentLapNum)
        {
            TrafficText.Text = $"BLUE FLAG · P{a.Position} +{a.CurrentLapNum - row.CurrentLapNum} lap";
            TrafficText.Foreground = (Brush)Resources["HudDRS"];
            TrafficText.Visibility = Visibility.Visible;
        }
        else if (behind is { } b && b.CurrentLapNum < row.CurrentLapNum && row.IsPlayer)
        {
            TrafficText.Text = $"TRAFFIC · P{b.Position} −1 lap";
            TrafficText.Foreground = (Brush)Resources["HudGold"];
            TrafficText.Visibility = Visibility.Visible;
        }
        else
        {
            TrafficText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Fuel trend: last lap's burn plus what that means for remaining laps.</summary>
    private void ShowFuelTrend(TelemetrySnapshot snapshot, StandingsRow? row)
    {
        if (row is null || row.FuelUsedLastLap <= 0.01f)
        {
            FuelTrendText.Visibility = Visibility.Collapsed;
            return;
        }

        var lapsLeft = snapshot.Player?.FuelRemainingLaps ?? 0f;
        FuelTrendText.Text = $"FUEL −{row.FuelUsedLastLap:0.0} L/lap · {lapsLeft:0.0} laps left";
        FuelTrendText.Visibility = Visibility.Visible;
        FuelTrendText.Foreground = (Brush)Resources["HudMuted"];
    }

    /// <summary>Fuel-to-finish advice: red = tank won't reach the finish (litres to add),
    /// green = spare fuel in laps; hidden without a race lap counter.</summary>
    private void ShowFuelAdvice(TelemetrySnapshot snapshot)
    {
        var advice = snapshot.Fuel;
        if (advice is null)
        {
            FuelAdviceText.Visibility = Visibility.Collapsed;
            return;
        }

        if (advice.ShortfallLaps is { } shortLaps && shortLaps > 0)
        {
            FuelAdviceText.Text = $"FUEL SHORT · +{advice.LitresToFuel:0.#} L to finish";
            FuelAdviceText.Foreground = (Brush)Resources["HudRed"];
        }
        else if (advice.ShortfallLaps is { } spare)
        {
            FuelAdviceText.Text = $"FUEL OK · {-spare:0.#} lap(s) spare";
            FuelAdviceText.Foreground = (Brush)Resources["HudGreen"];
        }
        else
        {
            FuelAdviceText.Visibility = Visibility.Collapsed;
            return;
        }

        FuelAdviceText.Visibility = Visibility.Visible;
    }

    private void UpdateDrs(PlayerFrame frame)
    {
        if (!frame.DrsAllowed && !frame.IsDrsOn)
        {
            DrsText.Opacity = 0.35;
            DrsText.Foreground = (Brush)Resources["HudMuted"];
            return;
        }

        DrsText.Opacity = 1.0;
        DrsText.Foreground = frame.IsDrsOn
            ? (Brush)Resources["HudDRS"]  // on: solid blue like the web overlay badge
            : (Brush)Resources["HudMuted"];
    }

    private void SetPedalFill(Border fill, double value)
    {
        fill.Width = Math.Clamp(value, 0, 1) * ((Border)fill.Parent).ActualWidth;
    }

    private static string CompoundLabel(ActualCompound compound) => compound switch
    {
        ActualCompound.F1Inter => "INT",
        ActualCompound.F1Wet => "WET",
        _ when compound.ToString() is { } c && c.StartsWith("F1C") => c[3..],
        _ => compound.ToString(),
    };

    private static string PositionGapText(StandingsRow? row)
    {
        if (row is null)
        {
            return string.Empty;
        }

        if (row.Position == 1)
        {
            return "LEADER";
        }

        return row.GapToCarInFrontMs > 0 ? $"+{row.GapToCarInFrontMs / 1000.0:0.000}" : string.Empty;
    }

    /// <summary>Duel gaps for the race: interval to the car ahead (large, right column)
    /// and gap to the car behind (small, muted). Falls back to the leader gap when the
    /// neighbouring rows don't report (e.g. session start).</summary>
    private void UpdateBattleGaps(TelemetrySnapshot snapshot, StandingsRow? row)
    {
        if (row is null)
        {
            GapText.Text = string.Empty;
            BackText.Text = string.Empty;
            return;
        }

        var ahead = row.Position > 1 ? row.GapToCarInFrontMs : 0;
        var behind = 0;
        if (row.Position < snapshot.Standings.Count)
        {
            // GapToCarInFrontMs of the car BEHIND = its distance back to us.
            var backRow = snapshot.Standings.FirstOrDefault(r => r.Position == row.Position + 1);
            if (backRow is { } b && b.GapToCarInFrontMs > 0)
            {
                behind = b.GapToCarInFrontMs;
            }
        }

        GapText.Text = row.Position == 1
            ? "LEADER"
            : ahead > 0 ? $"+{ahead / 1000.0:0.000}" : string.Empty;
        BackText.Text = behind > 0 ? $"−{behind / 1000.0:0.000}" : string.Empty;
    }

    /// <summary>Last / best lap line under the timing row; best-lap turns gold when it is
    /// also the session's fastest lap.</summary>
    private void UpdateLapLine(StandingsRow? row)
    {
        if (row is null || row.LastLapTimeMs == 0 && row.BestLapTimeMs == 0)
        {
            LapsRow.Visibility = Visibility.Collapsed;
            return;
        }

        LapsRow.Visibility = Visibility.Visible;
        LastLapText.Text = row.LastLapTimeMs > 0 ? $"L {LapTime(row.LastLapTimeMs)}" : string.Empty;
        if (row.BestLapTimeMs > 0)
        {
            var isSessionBest = _latest?.Standings
                .Where(r => r.BestLapTimeMs > 0)
                .Min(r => r.BestLapTimeMs) == row.BestLapTimeMs;
            BestLapText.Text = $"B {LapTime(row.BestLapTimeMs)}";
            BestLapText.Foreground = (Brush)Resources[isSessionBest ? "HudGold" : "HudMuted"];
        }
        else
        {
            BestLapText.Text = string.Empty;
        }
    }

    /// <summary>Live lap delta chip next to the best lap — cumulative delta of the current
    /// lap against the driver's own best sectors (known from sector 2 on). Green when at
    /// or under the reference, red when slower, hidden when unknown (sector 1).</summary>
    private void UpdateLapDelta(StandingsRow? row)
    {
        if (row is null || row.LapDeltaMs == 0)
        {
            LapDeltaText.Text = string.Empty;
            return;
        }

        LapDeltaText.Text = $"Δ {row.LapDeltaMs / 1000.0:+0.000;-0.000}";
        LapDeltaText.Foreground = (Brush)Resources[row.LapDeltaMs <= 0 ? "HudGreen" : "HudRed"];
    }

    /// <summary>Live sector delta line — for each sector with a current time and a known
    /// personal best: "S1 31.245 +0.145". Green when on/under the best, red when slower,
    /// muted when no comparison is possible (sector not completed or no best yet).</summary>
    private void UpdateSectorDelta(StandingsRow? row)
    {
        SectorDeltaText.Inlines.Clear();
        if (row is null)
        {
            return; // hidden implicitly — empty inlines
        }

        ReadOnlySpan<(int Sector, uint Time, uint Best)> sectors = stackalloc (int, uint, uint)[3]
        {
            (1, row.Sector1TimeMs, row.BestSector1Ms),
            (2, row.Sector2TimeMs, row.BestSector2Ms),
            (3, row.Sector3TimeMs, row.BestSector3Ms),
        };
        foreach (var (sector, time, best) in sectors)
        {
            if (time == 0)
            {
                continue; // sector not completed since the last reset
            }

            SectorDeltaText.Inlines.Add(new Run($"S{sector} ")
            {
                Foreground = (Brush)Resources["HudMuted"],
            });
            SectorDeltaText.Inlines.Add(new Run(ShortTime(time))
            {
                Foreground = (Brush)Resources["HudText"],
            });
            if (best != 0)
            {
                var deltaMs = (int)time - best;
                SectorDeltaText.Inlines.Add(new Run($" {deltaMs:+0.000;-0.000}")
                {
                    Foreground = deltaMs <= 0
                        ? (Brush)Resources["HudGreen"]
                        : (Brush)Resources["HudRed"],
                });
            }

            SectorDeltaText.Inlines.Add(new Run("   ")
            {
                Foreground = (Brush)Resources["HudMuted"],
            });
        }
    }

    /// <summary>ss.mmm — sector-time format for the delta line (sectors never exceed a minute).</summary>
    private static string ShortTime(uint ms) =>
        TimeSpan.FromMilliseconds(ms) is { } t
            ? $"{t.Seconds:00}.{t.Milliseconds:000}"
            : string.Empty;

    /// <summary>m:ss.mmm — the HUD's one lap-time format.</summary>
    private static string LapTime(uint ms) =>
        TimeSpan.FromMilliseconds(ms) is { } t && ms > 0
            ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}"
            : string.Empty;

    /// <summary>Applies the color scheme by swapping the merged HUD color dictionary
    /// (classic ↔ German black-red-gold). Brush consumers use DynamicResource, so the
    /// change is picked up live; code lookups resolve at the new dictionary.</summary>
    private void ApplyScheme(OverlayColorScheme scheme)
    {
        // Absolute pack URI: a relative Uri built in code has no base and resolves
        // against the app root, missing the OverlayWindow/ folder the compiled page
        // resource lives under.
        var uri = new Uri(scheme == OverlayColorScheme.German
            ? "pack://application:,,,/ERCTelemetry;component/OverlayWindow/HudColors.German.xaml"
            : "pack://application:,,,/ERCTelemetry;component/OverlayWindow/HudColors.Classic.xaml");
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
    }

    /// <summary>Wetter-Radar: text summary ("Rain 65% in ~12 min" / "Dry · ~NN min") plus a
    /// visual rain-probability bar over the forecast horizon. Hidden when no forecast
    /// arrived yet or the widget is disabled.</summary>
    private void UpdateWeather(TelemetrySnapshot snapshot)
    {
        var showSetting = _settings?.Current.HudShowWeather ?? true;
        if (!showSetting)
        {
            return; // ApplyHudSettings has already collapsed the row
        }

        var radar = WeatherRadar.Build(snapshot.Meta?.Forecast);
        if (radar.Segments.Count == 0)
        {
            WeatherRow.Visibility = Visibility.Collapsed;
            WeatherRadarCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        WeatherRow.Text = radar.Summary;
        WeatherRow.Visibility = Visibility.Visible;
        DrawWeatherRadar(radar);
    }

    /// <summary>Draws the radar bar: one colored segment per forecast sample, blue by rain
    /// probability, with the time offset underneath. Tooltip shows the full sample.</summary>
    private void DrawWeatherRadar(WeatherRadarModel radar)
    {
        WeatherRadarCanvas.Children.Clear();
        const double segWidth = 22;
        const double segHeight = 10;
        const double gap = 3;
        var x = 0.0;
        foreach (var seg in radar.Segments)
        {
            var rect = new Rectangle
            {
                Width = segWidth,
                Height = segHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = RainBrush(seg.RainPercent),
                ToolTip = $"{seg.TimeOffsetMinutes} min · {seg.Weather} · {seg.RainPercent}%",
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            WeatherRadarCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{seg.TimeOffsetMinutes}",
                FontSize = 8,
                Foreground = (Brush)Resources["HudMuted"],
            };
            Canvas.SetLeft(label, x + segWidth / 2 - 4);
            Canvas.SetTop(label, segHeight + 1);
            WeatherRadarCanvas.Children.Add(label);

            x += segWidth + gap;
        }

        WeatherRadarCanvas.Width = x - gap;
        WeatherRadarCanvas.Visibility = Visibility.Visible;
    }

    /// <summary>Segment color by rain probability: grey (dry) → pale blue → blue → deep blue.</summary>
    private static Brush RainBrush(byte rainPercent) => rainPercent switch
    {
        >= 80 => new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A)),
        >= 50 => new SolidColorBrush(Color.FromRgb(0x2E, 0x6F, 0xC8)),
        >= 20 => new SolidColorBrush(Color.FromRgb(0x8A, 0xB8, 0xE8)),
        _ => new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
    };

    /// <summary>Strategy hint line under the weather row: tyre laps left (wear trend), the
    /// suggested pit lap, the fuel-corrected target and the live undercut/overcut window
    /// vs. the car ahead; hidden until the advisor has a usable trend.</summary>
    private void UpdateStrategy(TelemetrySnapshot snapshot)
    {
        if (snapshot.Strategy is not { } advice || advice.TyreLapsLeft < 0)
        {
            StrategyRow.Visibility = Visibility.Collapsed;
            return;
        }

        var text = "Tyres " + (advice.TyreLapsLeft <= 0
            ? "gone — pit now"
            : $"~{advice.TyreLapsLeft} laps") +
            (advice.SuggestedPitLap > 0 ? $" · pit L{advice.SuggestedPitLap}" : string.Empty) +
            (advice.TargetLapMs > 0
                ? $" · target {advice.TargetLapMs / 60000:D}:{advice.TargetLapMs % 60000 / 1000:D2}.{advice.TargetLapMs % 1000:D3}"
                : string.Empty);

        if (BuildPitWindowText(snapshot, advice) is { } pit)
        {
            text += " · " + pit;
        }

        StrategyRow.Text = text;
        StrategyRow.Visibility = Visibility.Visible;
    }

    /// <summary>Undercut/overcut line vs. the car directly ahead: "undercut ✓ (window N
    /// laps)" when pitting now puts you ahead, "overcut ✓" when staying out wins. Null when
    /// there is no car ahead (leader) or no usable data.</summary>
    private string? BuildPitWindowText(TelemetrySnapshot snapshot, StrategyAdvice advice)
    {
        var player = snapshot.Standings.FirstOrDefault(s => s.IsPlayer);
        if (player is null || player.Position <= 1)
        {
            return null;
        }

        var ahead = snapshot.Standings.FirstOrDefault(s => s.Position == player.Position - 1);
        if (ahead is null)
        {
            return null;
        }

        // Fresh-tyre delta: the car ahead's current wear × cost per wear percent.
        var tyreDelta = 0;
        var aheadWear = snapshot.Tyres?.FirstOrDefault(t => t.CarIndex == ahead.CarIndex)?.WearPercent;
        if (aheadWear is { } wear && wear > 0)
        {
            tyreDelta = (int)Math.Round(wear * PitWindowCalculator.MsPerWearPercent);
        }

        // Laps until the car ahead pits: their remaining life relative to the player's
        // (same wear rate assumed), floored at 1.
        var totalLife = player.TyreAgeLaps + Math.Max(0, advice.TyreLapsLeft);
        var lapsUntilAheadPits = Math.Max(1, totalLife - ahead.TyreAgeLaps);

        var pit = PitWindowCalculator.Compute(
            player.GapToCarInFrontMs,
            PitWindowCalculator.DefaultPitLaneMs,
            PitWindowCalculator.DefaultInOutDeltaMs,
            tyreDelta,
            lapsUntilAheadPits);

        if (pit.UndercutWorks)
        {
            return pit.UndercutWindowLaps is { } w
                ? $"undercut ✓ (window {w} lap{(w == 1 ? string.Empty : "s")})"
                : "undercut ✓";
        }

        return pit.OvercutWorks ? "overcut ✓" : null;
    }

    /// <summary>Field-wide timing tower: position · name · mini sector marks (green/purple)
    /// · gap to leader, like the F1 TV broadcast. Rebuilt only when the visible state
    /// changes (signature compare) so the 30 fps tick does not churn the visual tree.</summary>
    private void UpdateTower(TelemetrySnapshot snapshot)
    {
        if (WdgTower.Visibility != Visibility.Visible)
        {
            return;
        }

        var rows = snapshot.Standings;
        if (rows.Count == 0)
        {
            TowerPanel.Children.Clear();
            _towerSignature = string.Empty;
            return;
        }

        var count = Math.Min(rows.Count, TowerMaxRows);
        var sig = string.Join(';', rows.Take(count).Select(r =>
            $"{r.Position}|{r.Name}|{(byte)r.S1Status}{(byte)r.S2Status}{(byte)r.S3Status}|{r.GapToLeaderMs}|{r.LastLapTimeMs}"));
        if (sig == _towerSignature)
        {
            return;
        }
        _towerSignature = sig;

        TowerPanel.Children.Clear();
        for (var i = 0; i < count; i++)
        {
            TowerPanel.Children.Add(BuildTowerRow(rows[i]));
        }
    }

    /// <summary>One tower row: position, name (gold + highlight for the player), three mini
    /// sector chips and the gap to the leader (the leader shows its last lap instead).</summary>
    private Border BuildTowerRow(StandingsRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pos = new TextBlock
        {
            Text = $"P{row.Position}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)Resources[row.IsPlayer ? "HudGold" : "HudMuted"],
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 26,
        };
        Grid.SetColumn(pos, 0);
        grid.Children.Add(pos);

        var name = new TextBlock
        {
            Text = row.Name,
            FontSize = 11,
            FontWeight = row.IsPlayer ? FontWeights.Bold : FontWeights.Normal,
            Foreground = (Brush)Resources["HudText"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        chips.Children.Add(SectorChip(row.S1Status));
        chips.Children.Add(SectorChip(row.S2Status));
        chips.Children.Add(SectorChip(row.S3Status));
        Grid.SetColumn(chips, 2);
        grid.Children.Add(chips);

        var gap = new TextBlock
        {
            Text = row.Position == 1
                ? LapTime(row.LastLapTimeMs)
                : $"+{row.GapToLeaderMs / 1000.0:0.000}s",
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)Resources["HudMuted"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(gap, 3);
        grid.Children.Add(gap);

        return new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(3),
            Background = row.IsPlayer
                ? new SolidColorBrush(((SolidColorBrush)Resources["HudGold"]).Color) { Opacity = 0.16 }
                : null,
        };
    }

    /// <summary>One 7×7 sector chip: purple = session best, green = personal best,
    /// dim track = unremarkable.</summary>
    private Rectangle SectorChip(SectorMark mark)
    {
        var fill = mark switch
        {
            SectorMark.Purple => (Brush)Resources["HudPurple"],
            SectorMark.Green => (Brush)Resources["HudGreen"],
            _ => (Brush)Resources["HudBarTrack"],
        };
        return new Rectangle
        {
            Width = 7,
            Height = 7,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = fill,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Session-scoped auto-scaling minimap: one pre-created dot per grid slot,
    /// expand-only bounds (so dots never jump while the track extent grows in during the
    /// first frames), reset on session change. Zero (0,0) slots are unreported cars and
    /// stay hidden. Player = accent, rival = gold, others = muted.</summary>
    private void UpdateMap(TelemetrySnapshot snapshot)
    {
        var positions = snapshot.Positions;
        if (positions is null || MapRow.Visibility != Visibility.Visible)
        {
            return;
        }

        var meta = snapshot.Meta;
        var uid = meta?.SessionUid ?? 0;
        if (uid != _mapSessionUid)
        {
            ResetMapBounds();
            ResetMapFit();
            _mapSessionUid = uid;
        }

        // Fitted-layout pipeline (this window's own fitter): feed player positions
        // until the fit locks, then draw the world-transformed outline below the dots
        // and re-project dots onto the layout bounds so outline and dots share one
        // coordinate frame — that is what makes positions read "correct" on the map.
        UpdateMapFit(meta, positions);

        var playerIndex = meta?.PlayerCarIndex;
        var rivalIndex = snapshot.Rival?.CarIndex;

        for (var i = 0; i < positions.Count; i++)
        {
            var x = positions.X[i];
            var z = positions.Z[i];
            var dot = EnsureMapDots()[i];

            if (x == 0 && z == 0)
            {
                dot.Visibility = Visibility.Collapsed;
                continue;
            }

            // Track bounds expand monotonically — the mapping is stable from the first
            // frame on and never re-zooms (dots would visibly jump otherwise).
            if (!_mapHasBounds || x < _mapMinX)
            {
                _mapMinX = x;
            }
            if (!_mapHasBounds || x > _mapMaxX)
            {
                _mapMaxX = x;
            }
            if (!_mapHasBounds || z < _mapMinZ)
            {
                _mapMinZ = z;
            }
            if (!_mapHasBounds || z > _mapMaxZ)
            {
                _mapMaxZ = z;
            }
            _mapHasBounds = true;

            dot.Visibility = Visibility.Visible;
            var (px, py) = _mapFitter?.IsLocked == true ? MapPointFromFit(x, z) : MapPoint(x, z);
            Canvas.SetLeft(dot, px - dot.Width / 2);
            Canvas.SetTop(dot, py - dot.Width / 2);

            dot.Fill = i == playerIndex ? (Brush)Resources["HudAccent"]
                : i == rivalIndex ? (Brush)Resources["HudGold"]
                : (Brush)Resources["HudMuted"];
        }
    }

    private void ResetMapBounds()
    {
        _mapHasBounds = false;
    }

    /// <summary>Session change: drop the fitted outline shapes and this window's fitter —
    /// the next frame rebuilds both for the new session's track. Silent fallback for
    /// circuits without an embedded layout (legacy self-scaling dots keep running).</summary>
    private void ResetMapFit()
    {
        _mapFitter = null;
        _mapLayout = null;
        _mapLayoutWorld = null;
        if (_mapTrackLine is { } line)
        {
            line.Visibility = Visibility.Collapsed;
        }

        if (_mapStartFinish is { } sf)
        {
            sf.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Feeds the player's position into the HUD's own fitter and, once the fit
    /// locks, draws the world-transformed circuit outline below the dots and re-projects
    /// dots onto the layout bounds (same fixed mapping — dots ride ON the outline).
    /// Null fitter = circuit without embedded layout → legacy self-scaling dots.</summary>
    private void UpdateMapFit(SessionMeta? meta, MotionFrame positions)
    {
        if (meta is null)
        {
            return;
        }

        if (_mapFitter is null)
        {
            // Unknown circuit: TryGet fails → fitter stays null and the legacy
            // self-scaling dot map keeps working (fallback is silent by design).
            if (!TrackLayoutCatalog.TryGet(meta.Track.ToString(), out var layout))
            {
                return;
            }

            _mapLayout = layout;
            _mapFitter = new TrackFitter(layout);
        }

        var fitter = _mapFitter;
        if (fitter.IsLocked)
        {
            UpdateMapStartFinish(fitter);
            return; // frozen — nothing left to learn
        }

        var player = meta.PlayerCarIndex;
        if (player >= Math.Min((int)positions.Count, TelemetryConstants.MaxCars))
        {
            return;
        }

        var x = positions.X[player];
        var z = positions.Z[player];
        if (x == 0 && z == 0)
        {
            return; // unreported slot — same skip rule as the dot loop
        }

        fitter.Feed(x, z);
        if (fitter.IsLocked)
        {
            // Locked on this very frame: draw the outline once, then every later
            // frame (dots included) project through the layout's fixed bounds.
            DrawMapLayout(fitter.Fit!);
        }
    }

    /// <summary>One-time draw of the locked fit: transforms the layout to world metres,
    /// caches the layout bounds for dot projection and creates the outline Polyline
    /// (muted, half-transparent, under the dots) plus the start/finish tick.</summary>
    private void DrawMapLayout(TrackFitResult fit)
    {
        var layout = _mapLayout!;
        var world = new (double X, double Z)[layout.PointCount];
        _mapFitMinX = _mapFitMaxX = world[0].X;
        _mapFitMinZ = _mapFitMaxZ = world[0].Z;
        for (var i = 0; i < layout.PointCount; i++)
        {
            var (wx, wz) = fit.ToWorld(layout.X[i], layout.Y[i]);
            world[i] = (wx, wz);
            if (wx < _mapFitMinX) { _mapFitMinX = wx; }
            if (wx > _mapFitMaxX) { _mapFitMaxX = wx; }
            if (wz < _mapFitMinZ) { _mapFitMinZ = wz; }
            if (wz > _mapFitMaxZ) { _mapFitMaxZ = wz; }
        }

        var line = _mapTrackLine ??= new Polyline
        {
            Stroke = (Brush)Resources["HudMuted"],
            StrokeThickness = 2,
            Opacity = 0.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = Visibility.Collapsed,
        };

        _mapLayoutWorld = world;
        Panel.SetZIndex(line, -10); // outline under the dots, always
        if (!MapCanvas.Children.Contains(line))
        {
            MapCanvas.Children.Add(line);
        }

        line.Points = new PointCollection(
            world.Select(p => ToPoint(MapPointFromFit(p.X, p.Z))));
        line.Visibility = Visibility.Visible;

        // Start/finish may already be known (3 lap crossings) — if not, the tick is
        // drawn by UpdateMapStartFinish once the fitter has the median point.
        UpdateMapStartFinish(_mapFitter!);
    }

    /// <summary>Locked-fit projection: world metres → canvas pixels through the layout's
    /// fixed bounds — outline and dots share this one mapping after the lock.</summary>
    private (double X, double Y) MapPointFromFit(double x, double z)
    {
        var spanX = Math.Max(_mapFitMaxX - _mapFitMinX, 1.0);
        var spanZ = Math.Max(_mapFitMaxZ - _mapFitMinZ, 1.0);
        var scale = Math.Min(
            (MapRow.Width - 2 * MapPadding) / spanX,
            (MapRow.Height - 2 * MapPadding) / spanZ);

        var px = MapPadding + (x - _mapFitMinX) * scale;
        var py = MapRow.Height - MapPadding - (z - _mapFitMinZ) * scale;
        return (px, py);
    }

    /// <summary>Tuple → WPF Point helper for the layout polyline.</summary>
    private static System.Windows.Point ToPoint((double X, double Y) p) => new(p.X, p.Y);

    /// <summary>Start/finish tick across the layout at the median lap-line crossing,
    /// drawn once the fitter reports it (≥3 LapCompleted events). Direction of the tick
    /// is perpendicular to the nearest layout segment so it reads as the lap line.</summary>
    private void UpdateMapStartFinish(TrackFitter fitter)
    {
        if (_mapLayout is null || _mapFitter?.IsLocked != true || fitter.StartFinishWorld is not { } sf)
        {
            return;
        }

        if (_mapStartFinish is null)
        {
            _mapStartFinish = new System.Windows.Shapes.Line
            {
                Stroke = (Brush)Resources["HudAccent"],
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(_mapStartFinish, -9); // above the outline, under the dots
            MapCanvas.Children.Add(_mapStartFinish);
        }

        // Nearest layout world segment → tick direction = its perpendicular. The
        // world points were cached in DrawMapLayout; the tick uses an 8 m half-width
        // in world metres so its pixel length follows the projection scale.
        var layout = _mapLayout!;
        var best = double.MaxValue;
        var (dirX, dirZ) = (1.0, 0.0);
        for (var i = 0; i < layout.PointCount; i++)
        {
            var j = (i + 1) % layout.PointCount;
            var (ax, az) = _mapLayoutWorld![i];
            var (bx, bz) = _mapLayoutWorld[j];
            var mx = (ax + bx) / 2;
            var mz = (az + bz) / 2;
            var dx = mx - sf.X;
            var dz = mz - sf.Z;
            var d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                var segLen = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                if (segLen > 1e-3)
                {
                    (dirX, dirZ) = ((bx - ax) / segLen, (bz - az) / segLen);
                }
            }
        }

        var (perpX, perpZ) = (-dirZ, dirX); // perpendicular across the track
        const double HalfWidthMetres = 8;
        var (x1, y1) = MapPointFromFit(sf.X - perpX * HalfWidthMetres, sf.Z - perpZ * HalfWidthMetres);
        var (x2, y2) = MapPointFromFit(sf.X + perpX * HalfWidthMetres, sf.Z + perpZ * HalfWidthMetres);
        _mapStartFinish.X1 = x1;
        _mapStartFinish.Y1 = y1;
        _mapStartFinish.X2 = x2;
        _mapStartFinish.Y2 = y2;
        _mapStartFinish.Visibility = Visibility.Visible;
    }

    /// <summary>Expand-only bounds → canvas pixels. X maps to the horizontal, Z to the
    /// (inverted) vertical axis; at least 1 m of track extent is assumed.</summary>
    private (double X, double Y) MapPoint(double x, double z)
    {
        var spanX = Math.Max(_mapMaxX - _mapMinX, 1.0);
        var spanZ = Math.Max(_mapMaxZ - _mapMinZ, 1.0);
        var scale = Math.Min(
            (MapRow.Width - 2 * MapPadding) / spanX,
            (MapRow.Height - 2 * MapPadding) / spanZ);

        var px = MapPadding + (x - _mapMinX) * scale;
        var py = MapRow.Height - MapPadding - (z - _mapMinZ) * scale;
        return (px, py);
    }

    /// <summary>Creates the 22 minimap dots once (diameter 8 px); reused every frame.</summary>
    private Ellipse[] EnsureMapDots()
    {
        if (_mapDots is { } existing)
        {
            return existing;
        }

        var dots = new Ellipse[TelemetryConstants.MaxCars];
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i] = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = (Brush)Resources["HudMuted"],
                Visibility = Visibility.Collapsed,
            };
            MapCanvas.Children.Add(dots[i]);
        }

        return _mapDots = dots;
    }

    /// <summary>360° radar widget (Assetto-Corsa style): draws every car inside the
    /// player's radar radius (from <see cref="BlindSpotCalculator"/>) around a centered
    /// player triangle, distance = range, 1:1 in both axes. Dots are red for a car within
    /// one second of race gap (the classic battle window), gold for the selected rival,
    /// muted otherwise; the hint line names the nearest threat.</summary>
    private void UpdateRadar(TelemetrySnapshot snapshot)
    {
        if (WdgRadar.Visibility != Visibility.Visible)
        {
            return;
        }

        var playerIndex = snapshot.Meta?.PlayerCarIndex ?? 0;
        var cars = BlindSpotCalculator.Compute(
            snapshot.Positions, playerIndex, snapshot.Standings);

        var (dots, triangle) = EnsureRadar();
        foreach (var dot in dots)
        {
            dot.Visibility = Visibility.Collapsed;
        }

        if (cars.Count == 0)
        {
            RadarHint.Text = string.Empty;
            return; // player triangle stays up: "no one around"
        }

        // The player triangle always shows while the widget is visible — an empty
        // radar means "no one around", not "no radar".
        triangle.Visibility = Visibility.Visible;

        var center = RadarCanvas.Width / 2;
        const double MetresPerPixel = 0.35; // 200 m = 70 px - the radar radius on the 150 px canvas
        const double RadarRadiusPx = 70.0;  // 200 m * 0.35 px/m - dots clamp onto the rim
        var threatCar = byte.MaxValue;
        var threatGap = int.MaxValue;

        foreach (var car in cars)
        {
            if (car.CarIndex >= dots.Length)
            {
                continue;
            }

            var dot = dots[car.CarIndex];
            dot.Visibility = Visibility.Visible;
            var dx = car.LateralMetres * MetresPerPixel;
            var dy = -car.LongitudinalMetres * MetresPerPixel; // behind = below center
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > RadarRadiusPx)
            {
                var scale = RadarRadiusPx / dist;
                dx *= scale;
                dy *= scale;
            }

            Canvas.SetLeft(dot, center + dx - dot.Width / 2);
            Canvas.SetTop(dot, center + dy - dot.Height / 2);

            var isThreat = car.GapMs > 0 && car.GapMs <= BlindSpotCalculator.AlertGapMs;
            dot.Fill = isThreat ? (Brush)Resources["HudAccent"]
                : car.CarIndex == snapshot.Rival?.CarIndex ? (Brush)Resources["HudGold"]
                : (Brush)Resources["HudMuted"];
            dot.Width = dot.Height = isThreat ? 10 : 6;

            if (isThreat && car.GapMs < threatGap)
            {
                threatCar = car.CarIndex;
                threatGap = car.GapMs;
            }
        }

        RadarHint.Text = threatCar != byte.MaxValue && FindRow(snapshot.Standings, threatCar) is { } threatRow
            ? $"⚠ {ShortName(threatRow.Name)} · {threatGap / 1000.0:0.0}s"
            : string.Empty;
    }

    private static StandingsRow? FindRow(IReadOnlyList<StandingsRow> standings, byte carIndex)
    {
        foreach (var row in standings)
        {
            if (row.CarIndex == carIndex)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>Three-letter code from a game-provided driver name's last segment
    /// ("M. VERSTAPPEN" -> "VER") for the radar hint line.</summary>
    private static string ShortName(string name)
    {
        var parts = name.Split(' ');
        var last = parts[^1];
        return last.Length > 3 ? last[..3] : last;
    }

    /// <summary>Creates the radar elements once: 22 reusable car dots plus the centered
    /// player triangle pointing up (the radar's "forward").</summary>
    private (Ellipse[] Dots, Polygon Triangle) EnsureRadar()
    {
        if (_radarDots is { } existing && _radarTriangle is { } tri)
        {
            return (existing, tri);
        }

        var dots = new Ellipse[TelemetryConstants.MaxCars];
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i] = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (Brush)Resources["HudMuted"],
                Visibility = Visibility.Collapsed,
            };
            RadarCanvas.Children.Add(dots[i]);
        }

        _radarTriangle = new Polygon
        {
            Points = new PointCollection
            {
                new Point(0, -7), new Point(5, 6), new Point(-5, 6),
            },
            Fill = (Brush)Resources["HudAccent"],
        };
        Canvas.SetLeft(_radarTriangle, RadarCanvas.Width / 2);
        Canvas.SetTop(_radarTriangle, RadarCanvas.Height / 2);
        RadarCanvas.Children.Add(_radarTriangle);
        return (_radarDots = dots, _radarTriangle!);
    }

    // Radar working state — dots created lazily on first visible frame.
    private Ellipse[]? _radarDots;
    private Polygon? _radarTriangle;

    /// <summary>G-meter: dot position = (lateral, longitudinal) g-force of the player car,
    /// braking pulls down, acceleration up, right-hand corners right. Forces beyond the
    /// canvas edge clamp onto the rim, so the dot never leaves the circle.</summary>
    private void UpdateGmeter(TelemetrySnapshot snapshot)
    {
        if (WdgGmeter.Visibility != Visibility.Visible)
        {
            return;
        }

        var dot = EnsureGmeter();
        if (snapshot.Player is not { } player)
        {
            dot.Visibility = Visibility.Collapsed;
            return; // crosshair stays up: "no data", not "no meter"
        }

        const double Radius = 46; // canvas 110 minus padding — ring at 46, crosshair inside
        const double GPerRadius = 3.0; // 3 g reach the rim; F1 cornering sits around that
        var px = Math.Clamp(player.GLat / GPerRadius, -1, 1) * Radius;
        var py = Math.Clamp(player.GLong / GPerRadius, -1, 1) * Radius;

        // Clamp the combined vector onto the rim so diagonal 4-g loads stay inside.
        var magnitude = Math.Sqrt(px * px + py * py);
        if (magnitude > Radius)
        {
            px = px / magnitude * Radius;
            py = py / magnitude * Radius;
        }

        dot.Visibility = Visibility.Visible;
        Canvas.SetLeft(dot, GmeterCanvas.Width / 2 + px - dot.Width / 2);
        Canvas.SetTop(dot, GmeterCanvas.Height / 2 + py - dot.Height / 2);
        dot.Fill = magnitude >= Radius * 0.66 ? (Brush)Resources["HudAccent"]
            : magnitude >= Radius * 0.33 ? (Brush)Resources["HudGold"]
            : (Brush)Resources["HudMuted"];
    }

    /// <summary>Creates the meter graphics (ring, crosshair, force dot) once.</summary>
    private Ellipse EnsureGmeter()
    {
        if (_gmeterDot is { } existing)
        {
            return existing;
        }

        const double Center = 55;
        const double Radius = 46;

        var ring = new Ellipse
        {
            Width = Radius * 2,
            Height = Radius * 2,
            Stroke = (Brush)Resources["HudBorder"],
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(ring, Center - Radius);
        Canvas.SetTop(ring, Center - Radius);
        GmeterCanvas.Children.Add(ring);

        var horizontal = new Line
        {
            X1 = Center - Radius, Y1 = Center, X2 = Center + Radius, Y2 = Center,
            Stroke = (Brush)Resources["HudMuted"],
            StrokeThickness = 1,
            Opacity = 0.5,
        };
        var vertical = new Line
        {
            X1 = Center, Y1 = Center - Radius, X2 = Center, Y2 = Center + Radius,
            Stroke = (Brush)Resources["HudMuted"],
            StrokeThickness = 1,
            Opacity = 0.5,
        };
        GmeterCanvas.Children.Add(horizontal);
        GmeterCanvas.Children.Add(vertical);

        _gmeterDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = (Brush)Resources["HudMuted"],
            Visibility = Visibility.Collapsed,
        };
        GmeterCanvas.Children.Add(_gmeterDot);
        return _gmeterDot;
    }

    // G-meter working state — created lazily on first visible frame.
    private Ellipse? _gmeterDot;
    /// <summary>Forces the rival panel visible for ~5 s (Ctrl+Shift+R) and shows the HUD
    /// if it is currently hidden.</summary>
    public void ShowRivalNow()
    {
        if (_settings?.Current.HudShowRival == false)
        {
            return; // widget disabled — hotkey is a no-op then (settings checkbox explains)
        }

        _forcedWindowUntil = Environment.TickCount64 + RivalShowMs;
        if (!IsVisible && _services.OverlayWindowSnapshots.Reader.Count > 0)
        {
            Show();
            Start();
        }
    }

    /// <summary>Auto-cycle + forced window visibility logic for the rival panel, plus
    /// content refresh (position/name/gap, tyres with wear% and damage%, ERS, speed).</summary>
    private void UpdateRivalPanel(TelemetrySnapshot snapshot)
    {
        var settings = _settings?.Current;
        if (settings is null || !settings.HudShowRival)
        {
            WdgRival.Visibility = Visibility.Collapsed;
            return;
        }
        if (snapshot.Rival is not { } rival)
        {
            WdgRival.Visibility = Visibility.Collapsed;
            return;
        }

        var now = Environment.TickCount64;
        var cycleMs = Math.Max(settings.HudRivalCycleSeconds, 5) * 1000L;
        if (!_rivalPhaseBaseSet)
        {
            _rivalPhaseBase = now;
            _rivalPhaseBaseSet = true;
        }

        var inAutoWindow = (now - _rivalPhaseBase) % cycleMs < RivalShowMs;
        var show = now < _forcedWindowUntil || inAutoWindow;

        WdgRival.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        EnsureWidgetPosition(WdgRival);

        var row = snapshot.Standings.FirstOrDefault(r => r.CarIndex == rival.CarIndex);
        var tyres = snapshot.Tyres?.FirstOrDefault(t => t.CarIndex == rival.CarIndex);
        RivalHeaderText.Text = row is null
            ? rival.Name
            : $"P{row.Position} {rival.Name} · gap +{row.GapToLeaderMs / 1000.0:0.000}s";
        RivalTyreText.Text = $"{CompoundLabel(rival.TyreCompound)} · age {rival.TyreAgeLaps} laps" +
                             (tyres is null ? string.Empty : $" · wear {tyres.WearPercent:0}% · damage {tyres.DamagePercent:0}%");
        RivalStatText.Text = $"Speed {rival.Speed} km/h · ERS {Math.Clamp(rival.ErsStoreEnergy / TelemetryConstants.MaxErsJoules * 100f, 0f, 100f):0}%";
    }
}