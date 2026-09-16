using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using ERCTelemetry.App.Composition;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Drives the Dashboard and Race Control tabs. Pulls the latest snapshot from the
/// pipeline on a UI-thread timer — all property updates happen on the dispatcher thread.</summary>
public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private const string AutoRival = "Auto (teammate)";
    private const int MaxDisplayedEvents = 50;

    private readonly AppServices _services;
    private readonly AppSettingsService _settings;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _timer;
    private long _lastEventSequence;
    private ulong? _sessionUid;
    private bool _resultsNotified;

    private string _sessionInfo = "No session";
    private string _forecastSummary = string.Empty;
    private string _emptyStateText = "Telemetrie-Listener ist gestoppt — starte ihn oben in der Kopfzeile.";
    private TelemetrySnapshot? _snapshot;
    private bool _hasSnapshot;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DashboardViewModel(AppServices services, AppSettingsService settings, TrayIconService tray)
    {
        _services = services;
        _settings = settings;
        _tray = tray;
        Standings = new ObservableCollection<StandingsRow>();
        Drivers = new ObservableCollection<string> { AutoRival };
        RaceEvents = new ObservableCollection<RaceEventRow>();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30 fps
        };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    public ObservableCollection<StandingsRow> Standings { get; }

    public ObservableCollection<string> Drivers { get; }

    public ObservableCollection<RaceEventRow> RaceEvents { get; }

    public string SessionInfo
    {
        get => _sessionInfo;
        private set => Set(ref _sessionInfo, value);
    }

    /// <summary>Weather lookahead for the session: "Rain 65% in ~12 min" when a sample
    /// carries rain, "Dry · ~90 min" when all samples are dry; empty until forecast
    /// data arrives (also hides the bound TextBlock).</summary>
    public string ForecastSummary
    {
        get => _forecastSummary;
        private set => Set(ref _forecastSummary, value);
    }

    public TelemetrySnapshot? Current
    {
        get => _snapshot;
        private set => Set(ref _snapshot, value);
    }

    public bool HasSnapshot
    {
        get => _hasSnapshot;
        private set => Set(ref _hasSnapshot, value);
    }

    /// <summary>Hint shown over the standings grid while it is empty — the message follows
    /// the live state (listener stopped / waiting for packets / session without positions).</summary>
    public string EmptyStateText
    {
        get => _emptyStateText;
        private set => Set(ref _emptyStateText, value);
    }

    /// <summary>Two-way bound to the rival combo. Null/empty selections are ignored so a
    /// transient clear (list rebuild) never silently drops the user's choice.</summary>
    public string? SelectedRivalName
    {
        get
        {
            if (_services.SessionStore.RivalOverride is byte index)
            {
                var driver = Current?.Drivers.FirstOrDefault(d => d.CarIndex == index);
                if (driver is not null)
                {
                    return DriverDisplay(driver.CarIndex, driver.Name);
                }
            }

            return AutoRival;
        }
        set => SelectRival(value);
    }

    public void SelectRival(string? driverName)
    {
        if (string.IsNullOrEmpty(driverName))
        {
            return; // transient clear during a driver-list rebuild — keep the current rival
        }

        if (driverName == AutoRival)
        {
            _services.SessionStore.RivalOverride = null;
            _settings.Update(s => s with { RivalDriverName = null });
            return;
        }

        var driver = Current?.Drivers.FirstOrDefault(d => DriverDisplay(d.CarIndex, d.Name) == driverName);
        if (driver is not null)
        {
            _services.SessionStore.RivalOverride = driver.CarIndex;
            _settings.Update(s => s with { RivalDriverName = driver.Name });
        }
    }

    private static string DriverDisplay(byte carIndex, string name) => $"{name} (Car {carIndex + 1})";

    /// <summary>Empty-state message for the standings grid, keyed to the live pipeline
    /// state so the user always knows what to do next.</summary>
    private string ComputeEmptyState()
    {
        if (!_services.IsListening)
        {
            return "Telemetrie-Listener ist gestoppt — starte ihn oben in der Kopfzeile.";
        }

        if (!HasSnapshot)
        {
            return "Warte auf UDP-Pakete von F1 26… Starte das Spiel und aktiviere Telemetrie (Einstellungen → Telemetrie).";
        }

        return "Session läuft — Positionsdaten erscheinen hier, sobald das Rennen startet.";
    }

    private void Pump()
    {
        EmptyStateText = ComputeEmptyState(); // refresh even while no snapshot arrives
        TelemetrySnapshot? latest = null;
        while (_services.Snapshots.Reader.TryRead(out var snapshot))
        {
            latest = snapshot; // drain to the freshest
        }

        if (latest is null)
        {
            return;
        }

        Current = latest;
        HasSnapshot = true;
        DetectSessionChange(latest);
        UpdateSessionInfo(latest);
        UpdateForecast(latest);
        UpdateStandings(latest);
        UpdateDrivers(latest);
        UpdatePlayerCard(latest);
        AppendNewEvents(latest);
        NotifyResults(latest);
    }

    /// <summary>Toast notification when the final classification lands — a race finishing
    /// is exactly the moment the user may want to return to the dashboard.</summary>
    private void NotifyResults(TelemetrySnapshot snapshot)
    {
        if (_resultsNotified || snapshot.FinalResults.Count == 0)
        {
            return;
        }

        _resultsNotified = true;
        _tray.Notify("ERCTelemetry",
            $"Session finished — results for {snapshot.FinalResults.Count} drivers are in the History tab.");
    }

    private void DetectSessionChange(TelemetrySnapshot snapshot)
    {
        var uid = snapshot.Meta?.SessionUid;
        if (uid == _sessionUid)
        {
            return;
        }

        _sessionUid = uid;
        _resultsNotified = false;
        RaceEvents.Clear(); // don't mix events from different sessions
    }

    private void UpdateSessionInfo(TelemetrySnapshot snapshot)
    {
        if (snapshot.Meta is not { } meta)
        {
            SessionInfo = "No session";
            return;
        }

        var timeLeft = TimeSpan.FromSeconds(meta.SessionTimeLeft);
        SessionInfo = $"{meta.SessionType} · {meta.Track} · {meta.Weather} · " +
                      (meta.TotalLaps > 0 ? $"{meta.TotalLaps} laps" : $"{timeLeft:%m\\:ss} left");
    }

    /// <summary>Sets <see cref="ForecastSummary"/> from the session forecast via the shared
    /// <see cref="WeatherRadar"/> model (same summary text as the HUD's Wetter-Radar).</summary>
    private void UpdateForecast(TelemetrySnapshot snapshot)
    {
        ForecastSummary = WeatherRadar.Build(snapshot.Meta?.Forecast).Summary;
    }

    private void UpdateStandings(TelemetrySnapshot snapshot)
    {
        var rows = snapshot.Standings;

        // Fast path: records have value equality — skip all UI work when unchanged.
        if (rows.Count == Standings.Count)
        {
            var unchanged = true;
            for (var i = 0; i < rows.Count; i++)
            {
                if (!Equals(Standings[i], rows[i]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        // Keyed in-place update: only rows that actually changed raise Replace events,
        // so the grid's selection and scroll position survive.
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Standings.Count)
            {
                if (!Equals(Standings[i], rows[i]))
                {
                    Standings[i] = rows[i];
                }
            }
            else
            {
                Standings.Add(rows[i]);
            }
        }

        while (Standings.Count > rows.Count)
        {
            Standings.RemoveAt(Standings.Count - 1);
        }
    }

    private void UpdateDrivers(TelemetrySnapshot snapshot)
    {
        var names = new List<string>(snapshot.Drivers.Count + 1) { AutoRival };
        names.AddRange(snapshot.Drivers.Select(d => DriverDisplay(d.CarIndex, d.Name)));

        var same = names.Count == Drivers.Count;
        for (var i = 0; same && i < names.Count; i++)
        {
            same = names[i] == Drivers[i];
        }

        if (same)
        {
            return;
        }

        Drivers.Clear();
        foreach (var name in names)
        {
            Drivers.Add(name);
        }

        // First participants of a session: restore the persisted rival preference if it
        // names a driver in this session (by ORIGINAL game name — car indices change
        // between sessions, and Name may be a user display-name override).
        if (_services.SessionStore.RivalOverride is null
            && _settings.Current.RivalDriverName is { Length: > 0 } wanted)
        {
            var driver = snapshot.Drivers.FirstOrDefault(d => (d.GameName ?? d.Name) == wanted);
            if (driver is not null)
            {
                _services.SessionStore.RivalOverride = driver.CarIndex;
            }
        }
    }

    private void UpdatePlayerCard(TelemetrySnapshot snapshot)
    {
        OnPropertyChanged(nameof(Player));
        OnPropertyChanged(nameof(Rival));
        OnPropertyChanged(nameof(Comparison));
        OnPropertyChanged(nameof(SelectedRivalName)); // re-select after driver-list rebuilds
    }

    /// <summary>One Race-Control feed row with the raw event type for badge styling.</summary>
    public sealed record RaceEventRow(string Time, string Type, string Text);

    private void AppendNewEvents(TelemetrySnapshot snapshot)
    {
        foreach (var entry in snapshot.RecentEvents)
        {
            if (entry.Sequence <= _lastEventSequence)
            {
                continue; // already shown (events can repeat across snapshot windows)
            }

            _lastEventSequence = entry.Sequence;
            RaceEvents.Insert(0, new RaceEventRow(
                entry.Utc.LocalDateTime.ToString("HH:mm:ss"), entry.Type, entry.Text));
        }

        while (RaceEvents.Count > MaxDisplayedEvents)
        {
            RaceEvents.RemoveAt(RaceEvents.Count - 1);
        }
    }

    public PlayerFrame? Player => Current?.Player;

    public PlayerFrame? Rival => Current?.Rival;

    public TelemetryComparison? Comparison
    {
        get
        {
            if (Current is not { } snapshot ||
                snapshot.Player is not { } player || snapshot.Rival is not { } rival)
            {
                return null;
            }

            return TelemetryComparer
                .Compare(player, rival)
                .WithLapDelta(BestLapFor(snapshot, player.CarIndex), BestLapFor(snapshot, rival.CarIndex));
        }
    }

    private static uint BestLapFor(TelemetrySnapshot snapshot, byte carIndex)
    {
        foreach (var row in snapshot.Standings)
        {
            if (row.CarIndex == carIndex)
            {
                return row.BestLapTimeMs;
            }
        }

        return 0;
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _timer.Stop();
}