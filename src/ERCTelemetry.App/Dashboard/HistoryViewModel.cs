using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using F1Game.UDP.Enums;
using ERCTelemetry.App.Composition;
using ERCTelemetry.App.Share;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Dashboard;

/// <summary>History tab: stored sessions from SQLite, with results, per-driver laps,
/// pace analysis (stints/degradation/consistency) and JSON/CSV export. The TrackTitan-style
/// session-card list lives in the HistoryViewModel.Sessions partial.</summary>
public sealed partial class HistoryViewModel : INotifyPropertyChanged
{
    private readonly TelemetryDb _db;
    private readonly ShareService _share;
    private long? _sessionId;
    private string _sessionTrack = string.Empty;

    private SessionRow? _selectedSession;
    private DriverOption? _selectedDriver;
    private SessionRow? _compareSessionB;
    private string _status = "Ready";
    private bool _isSharing;
    private string _shareStatus = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryViewModel(AppServices services)
    {
        _db = services.Database;
        _share = services.Share;
        Sessions = [];
        Results = [];
        Drivers = [];
        LapRows = [];
        Events = [];
        Penalties = [];
        PenaltyGroups = [];
        CompareRows = [];
        StintRows = [];
        ConsistencyRows = [];
        OvertakeRows = [];
        Clips = [];
        TraceLaps = [];
        TraceTips = [];
        TraceCornerTexts = [];
        ChampionshipRows = [];
        ConstructorRows = [];
        FormRows = [];
        RatingRows = [];
        Refresh();
        LoadCareerStats();
    }

    public ObservableCollection<SessionRow> Sessions { get; }

    public SessionRow? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetField(ref _selectedSession, value))
            {
                LoadSession();
            }
        }
    }

    public ObservableCollection<DriverOption> Drivers { get; }

    public DriverOption? SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (SetField(ref _selectedDriver, value) && _sessionId is { } id)
            {
                LoadLaps(id);
            }
        }
    }

    public ObservableCollection<ResultRow> Results { get; }

    public ObservableCollection<LapRow> LapRows { get; }

    /// <summary>Compare picker: session B of the A-vs-B comparison (A stays the session
    /// selected at the top so the detail view keeps its context).</summary>
    public SessionRow? CompareSessionB
    {
        get => _compareSessionB;
        set => SetField(ref _compareSessionB, value);
    }

    public ObservableCollection<CompareRow> CompareRows { get; }

    /// <summary>Race-pace analysis of the selected driver: one row per tyre stint
    /// (best/average/degradation/consistency). Empty until a session+driver is chosen.</summary>
    public ObservableCollection<StintRow> StintRows { get; }

    /// <summary>Consistency comparison (best/avg/σ/score) of ALL drivers in the session.</summary>
    public ObservableCollection<ConsistencyGridRow> ConsistencyRows { get; }

    /// <summary>Detected overtakes of the session (highlights timeline).</summary>
    public ObservableCollection<OvertakeGridRow> OvertakeRows { get; }

    /// <summary>Collision clips of the selected session (lap, drivers, severity, time,
    /// duration, file path) — newest first.</summary>
    public ObservableCollection<ClipRow> Clips { get; }

    /// <summary>Driver standings of the mini-championship (all stored Race sessions).</summary>
    public ObservableCollection<ChampionshipRow> ChampionshipRows { get; }

    /// <summary>Constructor standings of the mini-championship.</summary>
    public ObservableCollection<ConstructorRow> ConstructorRows { get; }

    /// <summary>The player's form curve (points per race, chronological).</summary>
    public ObservableCollection<FormRow> FormRows { get; }

    /// <summary>Local ELO leaderboard across all stored Race sessions (player row
    /// highlighted).</summary>
    public ObservableCollection<RatingRow> RatingRows { get; }

    /// <summary>The player's own ELO rating (formatted, empty when no classified race).</summary>
    public string PlayerRating { get; private set; } = string.Empty;

    /// <summary>One-line pace picture for the selected driver ("Best … · Ø … · σ …").</summary>
    public string PaceSummary { get; private set; } = string.Empty;

    public ObservableCollection<string> Events { get; }

    /// <summary>Penalty review of the selected session: one row per stored penalty
    /// event, with the lap the penalty was issued on ("—" when unknown).</summary>
    public ObservableCollection<PenaltyRow> Penalties { get; }

    /// <summary>League requirement: penalties AND warnings reviewable per lap and
    /// corner. One group per lap (lap-ascending), each incident stamped with its
    /// place on the track via CornerLocator ("Kurve 12 · Sektor 3 · 82 % · 4,1 km";
    /// "—" for sessions stored before the distance stamp existed).</summary>
    public ObservableCollection<PenaltyGroupRow> PenaltyGroups { get; }

    /// <summary>One-line incident picture: "3 Strafen · 5 Verwarnungen" / "Keine Vorfälle".</summary>
    public string PenaltySummary { get; private set; } = string.Empty;

    /// <summary>One incident row of the penalties section; <see cref="Badge"/> is the
    /// uppercase kind label, colored red/gold in XAML by <see cref="Kind"/>.</summary>
    public sealed record PenaltyIncidentRow(string Badge, string Kind, string Place, string Detail);

    /// <summary>All incidents of one lap.</summary>
    public sealed record PenaltyGroupRow(
        int Lap, string LapLabel, IReadOnlyList<PenaltyIncidentRow> Incidents);

    /// <summary>Rebuilds the per-lap penalty/warning groups from the stored events of
    /// the selected session, using the session's track length and sector boundaries for
    /// the place description.</summary>
    private void RefreshPenalties(long sessionId, byte? playerCarIndex)
    {
        PenaltyGroups.Clear();
        PenaltySummary = "Keine Vorfälle";
        if (_db.GetSession(sessionId) is not { } session)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PenaltySummary)));
            return;
        }

        var report = PenaltyReport.Build(
            _db.GetEvents(sessionId, playerCarIndex),
            (ushort)Math.Clamp(session.TrackLength, 0, ushort.MaxValue),
            session.Sector2Start,
            session.Sector3Start,
            session.Track);
        PenaltySummary = report.Summary;
        foreach (var g in report.Groups)
        {
            PenaltyGroups.Add(new PenaltyGroupRow(
                g.LapNumber,
                $"Runde {g.LapNumber}",
                [.. g.Incidents.Select(i => new PenaltyIncidentRow(
                    i.Kind switch
                    {
                        "Penalty" => "STRAFE",
                        "CornerCuttingWarning" => "ABKÜRZEN",
                        _ => "VERWARNUNG",
                    },
                    i.Kind,
                    i.Place,
                    i.Event.Text))]));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PenaltySummary)));
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>True while a session upload is in flight — disables the share button.</summary>
    public bool IsSharing
    {
        get => _isSharing;
        private set => SetField(ref _isSharing, value);
    }

    /// <summary>Share progress/result line ("Lade clip-1.mp4 hoch …", the public link, or an error).</summary>
    public string ShareStatus
    {
        get => _shareStatus;
        private set => SetField(ref _shareStatus, value);
    }

    /// <summary>Uploads the selected session (manifest + clips) to the share server and
    /// copies the public link to the clipboard. Never throws — errors land in ShareStatus.</summary>
    public async Task ShareSessionAsync()
    {
        if (_sessionId is not { } id || SelectedSession is not { } row)
        {
            ShareStatus = "Keine Session ausgewählt.";
            return;
        }

        if (IsSharing)
        {
            return;
        }

        IsSharing = true;
        ShareStatus = "Session wird hochgeladen …";
        try
        {
            if (_db.GetSession(id) is not { } session)
            {
                ShareStatus = "Session nicht gefunden.";
                return;
            }

            var clips = _db.GetClips(id);
            var manifest = ShareManifestBuilder.Build(session, _db.GetResults(id), clips);
            var progress = new Progress<string>(msg => ShareStatus = msg);
            var result = await _share.ShareSessionAsync(manifest, clips, progress);
            if (result.Success && result.Url is { } url)
            {
                try
                {
                    Clipboard.SetText(url);
                    ShareStatus = $"{url} — Link in Zwischenablage kopiert.";
                }
                catch (Exception)
                {
                    ShareStatus = $"{url} — Link konnte nicht in die Zwischenablage kopiert werden.";
                }
            }
            else
            {
                ShareStatus = $"Teilen fehlgeschlagen: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            ShareStatus = $"Teilen fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsSharing = false;
        }
    }

    /// <summary>Raised when the user asks to open the selected session in the report tab;
    /// MainWindow switches the panel and hands the row to RaceReportViewModel.</summary>
    public event Action<HistoryViewModel.SessionRow>? OpenReportRequested;

    /// <summary>Toolbar entry point for "Report öffnen".</summary>
    public void RequestOpenReport()
    {
        if (SelectedSession is { } row)
        {
            OpenReportRequested?.Invoke(row);
        }
    }

    /// <summary>Reloads the session list from the database.</summary>
    public void Refresh()
    {
        LoadCareerStats();
        Sessions.Clear();
        foreach (var s in _db.GetSessions())
        {
            Sessions.Add(new SessionRow(
                s.Id, s.SessionUid, $"{s.SessionType} · {GetTrackLabel(s.Track)}",
                FormatUtc(s.StartedUtc), LabelFinalized(s.Finalized), s.LapCount, s.DriverCount,
                s.Track));
        }

        RefreshFilterOptions();
        ApplyFilters();
    }

    private void LoadSession()
    {
        Results.Clear();
        LapRows.Clear();
        Events.Clear();
        Penalties.Clear();
        Clips.Clear();
        TraceLaps.Clear();
        TraceTips.Clear();
        TraceCornerTexts.Clear();
        Drivers.Clear();
        if (SelectedSession is not { } row)
        {
            return;
        }

        _sessionId = row.Id;
        _sessionTrack = row.Track;
        // Player car index scopes the events/overtakes timelines to what the player
        // themselves got (the game broadcasts every car's events).
        var session = _db.GetSession(row.Id);
        var playerCarIndex = session?.PlayerCarIndex;
        RefreshConsistency(row.Id);
        RefreshOvertakes(row.Id, playerCarIndex);
        LoadClips(row.Id);

        foreach (var r in _db.GetResults(row.Id))
        {
            Results.Add(new ResultRow(
                r.Position, r.Name, r.Team.Display(), r.NumLaps, r.GridPosition,
                r.Points, r.ResultStatus.ToString(), FormatMs(r.BestLapTimeMs),
                FormatSeconds(r.TotalRaceTimeSeconds), r.PenaltiesTime, r.NumPenalties));
        }

        foreach (var evt in _db.GetEvents(row.Id, playerCarIndex))
        {
            var lap = evt.LapNumber > 0 ? $"L{evt.LapNumber} " : string.Empty;
            Events.Add($"{lap}{evt.Utc.ToLocalTime():HH:mm:ss} {evt.Text}");
            if (evt.Type == "Penalty")
            {
                Penalties.Add(new PenaltyRow(
                    evt.LapNumber > 0 ? $"L{evt.LapNumber}" : "—", evt.Text));
            }
        }

        RefreshPenalties(row.Id, playerCarIndex);
        LoadDrivers(row.Id);

        // LoadDrivers assigns SelectedDriver (= Drivers[0]); when the record-equal driver
        // is already selected the change handler is skipped — load the laps explicitly so
        // the table shows this session's laps instead of the previous selection's.
        if (SelectedDriver is not null)
        {
            LoadLaps(row.Id);
        }
    }

    private void LoadDrivers(long sessionId)
    {
        var names = _db.GetDriverNames(sessionId);
        foreach (var (carIndex, name) in names.OrderBy(k => k.Key))
        {
            Drivers.Add(new DriverOption(carIndex, name));
        }

        SelectedDriver = Drivers.Count > 0 ? Drivers[0] : null;
    }

    /// <summary>Laps of the selected driver: table rows + chart rebuild.</summary>
    private void LoadLaps(long sessionId)
    {
        LapRows.Clear();
        if (SelectedDriver is not { } driver)
        {
            return;
        }

        foreach (var lap in _db.GetLaps(sessionId)
                     .Where(l => l.CarIndex == driver.Index))
        {
            LapRows.Add(new LapRow(
                lap.LapNumber, FormatMs(lap.LapTimeMs), FormatMs(lap.Sector1TimeMs),
                FormatMs(lap.Sector2TimeMs),
                CompoundLabel(lap.TyreCompound), lap.TyreAgeLaps, lap.Position,
                FormatErs(lap.ErsUsedJoules)));
        }

        LapCount = LapRows.Count > 0
            ? LapRows.Max(r => r.Lap)
            : 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LapCount)));

        RefreshPaceAnalysis(sessionId, driver.Index);
        RefreshTraceOptions(sessionId, driver.Index);
    }

    // ---- lap comparison (speed trace) ------------------------------------------

    private TraceLapOption? _selectedTraceA;
    private TraceLapOption? _selectedTraceB;

    /// <summary>Traced laps of the selected driver for the comparison pickers. Only the
    /// player's laps carry speed traces (the recorder buffers the player only), so the
    /// list stays empty for every other driver.</summary>
    public ObservableCollection<TraceLapOption> TraceLaps { get; }

    public TraceLapOption? SelectedTraceA
    {
        get => _selectedTraceA;
        set
        {
            if (SetField(ref _selectedTraceA, value))
            {
                RecomputeTraceCompare();
            }
        }
    }

    public TraceLapOption? SelectedTraceB
    {
        get => _selectedTraceB;
        set
        {
            if (SetField(ref _selectedTraceB, value))
            {
                RecomputeTraceCompare();
            }
        }
    }

    /// <summary>Coach tips of the current A-vs-B comparison (loss/gain/brake/apex).</summary>
    public ObservableCollection<TraceTip> TraceTips { get; }

    /// <summary>Timekiller ranking: the corners costing the most time in this comparison
    /// (already formatted for display, most-significant first).</summary>
    public ObservableCollection<string> TraceCornerTexts { get; }

    /// <summary>Enough traced laps (≥2) to pick two for comparison — gates the pickers.</summary>
    public bool HasTraceLaps { get; private set; }

    /// <summary>When set, lap B comes from the track's all-session best traced lap
    /// instead of the second picker — the cross-session reference comparison.</summary>
    public bool CompareVsTrackBest
    {
        get => _compareVsTrackBest;
        set
        {
            if (SetField(ref _compareVsTrackBest, value))
            {
                RecomputeTraceCompare();
            }
        }
    }

    private bool _compareVsTrackBest;

    /// <summary>Picker B is off while the track-best reference is selected.</summary>
    public bool TraceBPickerEnabled => HasTraceLaps && !CompareVsTrackBest;

    /// <summary>Empty-state / error hint for the comparison section.</summary>
    public string TraceHint { get; private set; } = string.Empty;

    /// <summary>The two loaded traces — HistoryTabView draws them on its Canvas.</summary>
    public LapTrace? TraceCurveA { get; private set; }
    public LapTrace? TraceCurveB { get; private set; }

    private void RefreshTraceOptions(long sessionId, byte carIndex)
    {
        TraceLaps.Clear();
        TraceTips.Clear();
        TraceCornerTexts.Clear();
        _selectedTraceA = null;
        _selectedTraceB = null;
        TraceCurveA = null;
        TraceCurveB = null;
        foreach (var summary in _db.GetLapTraceSummaries(sessionId)
                     .Where(s => s.CarIndex == carIndex))
        {
            TraceLaps.Add(new TraceLapOption(summary.LapNumber, summary.CarIndex, summary.LapTimeMs,
                $"Runde {summary.LapNumber} · {FormatMs(summary.LapTimeMs)}"));
        }

        HasTraceLaps = TraceLaps.Count >= 2;
        TraceHint = TraceLaps.Count switch
        {
            0 => "Keine aufgezeichneten Runden für diesen Fahrer (nur der eigene Fahrer wird aufgezeichnet).",
            1 => "Nur eine aufgezeichnete Runde — zum Vergleichen braucht es zwei.",
            _ => "Zwei Runden auswählen, um Speed-Trace, Bremspunkte und Apex zu vergleichen.",
        };
        RaiseTraceChanged();
    }

    /// <summary>Loads both picked traces from the DB, runs the comparer and exposes the
    /// curves for the chart. A corrupt/missing trace just clears the view.</summary>
    private void RecomputeTraceCompare()
    {
        TraceTips.Clear();
        TraceCornerTexts.Clear();
        TraceCurveA = null;
        TraceCurveB = null;
        if (_sessionId is not { } id || SelectedTraceA is not { } a)
        {
            RaiseTraceChanged();
            return;
        }

        LapTrace? traceB;
        if (CompareVsTrackBest)
        {
            var best = _sessionTrack.Length > 0 ? _db.GetBestLapTrace(_sessionTrack) : null;
            if (best is null)
            {
                TraceHint = "Keine Best-Trace für diese Strecke vorhanden (alle Sessions).";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceHint)));
                return;
            }

            traceB = best.Trace;
            TraceHint = $"Referenz: Runde {best.Trace.LapNumber} · {FormatMs(best.LapTimeMs)} " +
                        $"({best.SessionType}, {FormatUtc(best.StartedUtc)})";
        }
        else
        {
            if (SelectedTraceB is not { } b || a.LapNumber == b.LapNumber)
            {
                RaiseTraceChanged();
                return;
            }

            traceB = _db.GetLapTrace(id, b.CarIndex, b.LapNumber);
        }

        try
        {
            var traceA = _db.GetLapTrace(id, a.CarIndex, a.LapNumber);
            if (traceA is null || traceB is null)
            {
                TraceHint = "Speed-Traces konnten nicht geladen werden.";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceHint)));
                return;
            }

            TraceCurveA = traceA;
            TraceCurveB = traceB;
            foreach (var tip in LapTraceComparer.Compare(traceA, traceB))
            {
                TraceTips.Add(tip);
            }

            foreach (var corner in LapTraceComparer.RankCornerLosses(traceA, traceB))
            {
                TraceCornerTexts.Add(
                    $"Kurve {corner.CornerNumber} ({corner.AtPercent:0}% der Runde): +{corner.LossSeconds:0.0}s");
            }
        }
        catch (Exception ex)
        {
            TraceHint = $"Trace-Vergleich fehlgeschlagen: {ex.Message}";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceHint)));
        }
        finally
        {
            RaiseTraceChanged();
        }
    }

    private void RaiseTraceChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTraceLaps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceHint)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTraceA)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTraceB)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceCurveA)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceCurveB)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompareVsTrackBest)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceBPickerEnabled)));
    }

    /// <summary>Consistency comparison of ALL drivers (best/avg/σ/score) for the selected
    /// session — computed from the stored laps, independent of the selected driver.</summary>
    private void RefreshConsistency(long sessionId)
    {
        ConsistencyRows.Clear();
        foreach (var row in PaceAnalyzer.AnalyzeConsistency(_db.GetLaps(sessionId)))
        {
            ConsistencyRows.Add(new ConsistencyGridRow(
                row.DriverName,
                $"{row.RacingLaps}",
                FormatMs(row.BestLapMs),
                FormatMs((uint)Math.Round(row.AverageLapMs)),
                $"{row.ConsistencySigmaMs / 1000.0:0.000} s",
                double.IsNaN(row.Score) ? "–" : $"{row.Score:0.0}%"));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConsistencyRows)));
    }

    /// <summary>Highlights timeline: the session's detected overtakes, chronological.</summary>
    private void RefreshOvertakes(long sessionId, byte? playerCarIndex)
    {
        OvertakeRows.Clear();
        foreach (var o in _db.GetOvertakes(sessionId, playerCarIndex))
        {
            OvertakeRows.Add(new OvertakeGridRow(
                o.LapNumber > 0 ? $"L{o.LapNumber}" : "—",
                $"{o.DriverName} überholt {o.PassedDriverName}",
                $"P{o.NewPosition}"));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OvertakeRows)));
    }

    /// <summary>Collision clips of the selected session, newest first.</summary>
    private void LoadClips(long sessionId)
    {
        Clips.Clear();
        foreach (var clip in _db.GetClips(sessionId))
        {
            Clips.Add(new ClipRow(
                clip.LapNumber > 0 ? $"L{clip.LapNumber}" : "—",
                clip.SecondDriverName is { } second
                    ? $"{clip.DriverName} vs {second}"
                    : $"{clip.DriverName} vs Umgebung",
                CollisionSeverity.Label(clip.Severity),
                clip.Utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                $"{clip.DurationSeconds:0.0} s",
                clip.FilePath));
        }
    }

    /// <summary>Opens a clip in the default player (shell execute).</summary>
    public void PlayClip(ClipRow clip)
    {
        try
        {
            Process.Start(new ProcessStartInfo(clip.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Clip konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    /// <summary>Opens the selected session's clip folder in Explorer.</summary>
    public void OpenClipFolder()
    {
        if (SelectedSession is not { } row)
        {
            return;
        }

        var folder = ClipStorage.SessionFolder(row.SessionUid);
        if (!Directory.Exists(folder))
        {
            Status = "Kein Clip-Ordner für diese Session.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Ordner konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    /// <summary>Recomputes stint/degradation/consistency for the selected driver from the
    /// stored laps; clears the pace expander when the driver has no laps.</summary>
    private void RefreshPaceAnalysis(long sessionId, byte carIndex)
    {
        var stints = PaceAnalyzer.Analyze(_db.GetLaps(sessionId), carIndex).Stints;
        StintRows.Clear();
        foreach (var s in stints)
        {
            StintRows.Add(new StintRow(
                $"#{s.StintNumber}",
                s.Tyre,
                $"{s.StartLap}–{s.EndLap} ({s.Laps})",
                FormatMs(s.BestLapMs),
                FormatMs(s.AverageLapMs),
                $"+{s.DegradationMsPerLap / 1000.0:0.000} s/lap",
                $"{s.ConsistencySigmaMs / 1000.0:0.000} s"));
        }

        PaceSummary = stints.Count == 0 ? string.Empty : SummarizePace(sessionId, carIndex);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PaceSummary)));
    }

    private string SummarizePace(long sessionId, byte carIndex)
    {
        var report = PaceAnalyzer.Analyze(_db.GetLaps(sessionId), carIndex);
        var laps = report.Stints.Sum(s => s.Laps);
        return $"Best {FormatMs(report.BestLapMs)} · Ø {FormatMs(report.AverageLapMs)} · " +
               $"σ {report.ConsistencySigmaMs / 1000.0:0.000} s · {laps} racing laps";
    }

    /// <summary>Exports the selected session's classification as JSON.</summary>
    public void ExportJson()
    {
        if (_sessionId is not { } id || SelectedSession is not { } row)
        {
            Status = "No session selected";
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"results-{row.SessionUid}.json",
            Filter = "JSON|*.json",
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                ResultsExporter.WriteJson(dlg.FileName, null, _db.GetResults(id));
                Status = $"Exported {dlg.FileName}";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
            }
        }
    }

    /// <summary>A-vs-B comparison of the session selected at the top (A) against
    /// <see cref="CompareSessionB"/> (B): best lap + per-sector deltas, B − A.</summary>
    public void ComputeComparison()
    {
        CompareRows.Clear();
        if (SelectedSession is not { } a)
        {
            Status = "No session A selected";
            return;
        }

        if (CompareSessionB is not { } b)
        {
            Status = "Pick a comparison session (B)";
            return;
        }

        if (a.Id == b.Id)
        {
            Status = "Pick two different sessions";
            return;
        }

        var namesA = _db.GetDriverNames(a.Id);
        var namesB = _db.GetDriverNames(b.Id);
        var rows = SessionComparer.Compare(
            _db.GetLaps(a.Id), _db.GetLaps(b.Id), namesA, namesB);
        foreach (var r in rows)
        {
            var lapDelta = r.HasBoth ? FormatDelta((int)r.B.LapMs - (int)r.A.LapMs) : String.Empty;
            var s1Delta = r.HasBoth && r.A.S1Ms > 0 && r.B.S1Ms > 0
                ? FormatDelta(r.B.S1Ms - r.A.S1Ms) : String.Empty;
            var s2Delta = r.HasBoth && r.A.S2Ms > 0 && r.B.S2Ms > 0
                ? FormatDelta(r.B.S2Ms - r.A.S2Ms) : String.Empty;
            var s3Delta = r.HasBoth && r.A.S3Ms > 0 && r.B.S3Ms > 0
                ? FormatDelta(r.B.S3Ms - r.A.S3Ms) : String.Empty;
            CompareRows.Add(new CompareRow(
                r.Name, FormatMs(r.A.LapMs), FormatMs(r.B.LapMs),
                lapDelta, s1Delta, s2Delta, s3Delta));
        }

        Status = $"Compared «{a.Title}» vs «{b.Title}» — {rows.Count} drivers";
    }

    // ---- career stats ----------------------------------------------------------

    /// <summary>Career aggregates (races, wins, podiums, Ø grid, points, best track)
    /// read from the DB on construction and every Refresh. Empty = no races stored.</summary>
    public string CareerSummary { get; private set; } = string.Empty;

    private void LoadCareerStats()
    {
        var s = _db.GetCareerStats();
        CareerSummary = s.Races == 0
            ? "No races stored yet"
            : $"{s.Races} races · {s.Wins} wins · {s.Podiums} podiums · " +
              $"Ø grid {s.AverageGridPosition:0.0} · {s.Points:0.#} pts · " +
              (s.BestLapMs > 0 ? $"best lap {FormatMs(s.BestLapMs)} · " : string.Empty) +
              (s.BestTrack.Length > 0
                ? $"best track {GetTrackLabel(s.BestTrack)} (Ø pos {s.BestTrackAvgPosition:0.0})"
                : "no best track");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CareerSummary)));
        LoadChampionship();
    }

    /// <summary>Mini-championship across stored Race sessions: driver standings, constructor
    /// standings and the player's form curve. Rebuilt on every Refresh.</summary>
    private void LoadChampionship()
    {
        var data = _db.GetChampionship();
        var model = ChampionshipCalculator.Build(data.Races, data.PlayerRaces);

        ChampionshipRows.Clear();
        foreach (var d in model.DriverStandings)
        {
            ChampionshipRows.Add(new ChampionshipRow(
                d.Rank, d.Name, TeamName.Display(d.Team), d.Races, d.Points, d.Wins, d.Podiums, d.BestPosition));
        }

        ConstructorRows.Clear();
        foreach (var c in model.ConstructorStandings)
        {
            ConstructorRows.Add(new ConstructorRow(
                c.Rank, TeamName.Display(c.Team), c.Races, c.Points, c.Wins));
        }

        FormRows.Clear();
        foreach (var f in model.FormCurve)
        {
            FormRows.Add(new FormRow(
                GetTrackLabel(f.Track), FormatUtc(f.StartedUtc), f.Position, f.Points));
        }

        var rating = DriverRatingCalculator.Build(data.Races, _db.GetPlayerName());
        RatingRows.Clear();
        foreach (var r in rating.Standings)
        {
            RatingRows.Add(new RatingRow(
                r.Rank, r.Name, TeamName.Display(r.Team), r.Races,
                r.Rating.ToString("0", CultureInfo.InvariantCulture),
                r.Wins, r.BestPosition, r.IsPlayer));
        }

        PlayerRating = rating.PlayerRating is { } pr
            ? pr.ToString("0", CultureInfo.InvariantCulture)
            : string.Empty;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChampionshipRows)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConstructorRows)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormRows)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RatingRows)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerRating)));
    }

    private static string FormatUtc(string startedUtc)
    {
        try
        {
            return DateTime.Parse(startedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                .ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return startedUtc;
        }
    }

    private static string LabelFinalized(int finalized) => finalized switch
    {
        0 => "open",
        1 => "finished",
        _ => "crashed",
    };

    private static string GetTrackLabel(string track) =>
        track.StartsWith("F1_", StringComparison.Ordinal) ? track[3..] : track;

    private static string FormatMs(uint ms) =>
        ms <= 0 ? String.Empty : $"{ms / 60000:D}:{ms % 60000 / 1000:D2}.{ms % 1000:D3}";

    private static string FormatMs(double ms) =>
        ms <= 0 ? String.Empty : $"{(long)ms / 60000}:{(long)ms % 60000 / 1000:00}.{(long)ms % 1000:000}";

    private static string FormatMs(ushort ms) => ms <= 0
        ? String.Empty
        : $"{ms / 60000:D}:{ms % 60000 / 1000:D2}.{ms % 1000:D3}";

    private static string FormatSeconds(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss\.fff",
            CultureInfo.InvariantCulture);

    /// <summary>Signed net ERS energy as kJ; "— " when no CarStatus was seen that lap.</summary>
    private static string FormatErs(float joules) => joules == 0
        ? "—"
        : $"{joules / 1000.0:+0.0;-0.0} kJ";

    /// <summary>B − A formatted as seconds; "±0.000" for a true zero delta.</summary>
    private static string FormatDelta(int ms) => ms == 0
        ? "±0.000"
        : (ms / 1000.0).ToString("+0.000;-0.000", CultureInfo.InvariantCulture);

    private static string CompoundLabel(ActualCompound compound) => compound switch
    {
        ActualCompound.F1Wet => "WET",
        ActualCompound.F1Inter => "INT",
        _ when compound.ToString().StartsWith("F1C", StringComparison.Ordinal)
            => compound.ToString()[3..],
        _ => compound.ToString(),
    };

    private bool SetField<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }

    public void ExportCsv()
    {
        if (_sessionId is not { } id || SelectedSession is not { } row)
        {
            Status = "No session selected";
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"standings-{row.SessionUid}.csv",
            Filter = "CSV|*.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                ResultsExporter.WriteCsv(dlg.FileName, _db.GetResults(id));
                Status = $"Exported {dlg.FileName}";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
            }
        }
    }

    /// <summary>Exports the selected driver's laps as CSV (raw ms + formatted lap time).</summary>
    public void ExportLapsCsv()
    {
        if (_sessionId is not { } id || SelectedSession is not { } row || SelectedDriver is not { } driver)
        {
            Status = "No session/driver selected";
            return;
        }

        var laps = _db.GetLaps(id)
            .Where(l => l.CarIndex == driver.Index)
            .OrderBy(l => l.LapNumber)
            .ToList();
        if (laps.Count == 0)
        {
            Status = "No laps stored for this driver";
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"laps-{row.SessionUid}-{driver.Name}.csv",
            Filter = "CSV|*.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                ResultsExporter.WriteLapsCsv(dlg.FileName, laps);
                Status = $"Exported {laps.Count} laps to {dlg.FileName}";
            }
            catch (Exception ex)
            {
                Status = $"Export failed: {ex.Message}";
            }
        }
    }

    /// <summary>Laps driven so far by the selected driver — the chart's x-axis extent.</summary>
    public int LapCount { get; private set; }

    /// <summary>One row in the history session list.</summary>
    public sealed record SessionRow(
        long Id,
        ulong SessionUid,
        string Title,
        string Started,
        string State,
        int LapCount,
        int DriverCount,
        string Track);

    /// <summary>Selected-driver option for the lap table and chart.</summary>
    public sealed record DriverOption(byte Index, string Name);

    /// <summary>One tyre stint of the selected driver (pace analysis grid row).</summary>
    public sealed record StintRow(
        string Stint,
        string Tyre,
        string Span,
        string Best,
        string Average,
        string Degradation,
        string Sigma);

    /// <summary>One row in the cross-driver consistency grid (formatted strings —
    /// culture-independent values are computed in Core).</summary>
    public sealed record ConsistencyGridRow(
        string Driver,
        string Laps,
        string Best,
        string Average,
        string Sigma,
        string Score);

    /// <summary>One row in the highlights timeline (overtakes).</summary>
    public sealed record OvertakeGridRow(string Lap, string Text, string Position);

    /// <summary>One collision clip of the selected session (formatted for the grid).</summary>
    public sealed record ClipRow(
        string Lap,
        string Drivers,
        string Severity,
        string Time,
        string Duration,
        string FilePath);

    /// <summary>One row in the stored classification grid.</summary>
    public sealed record ResultRow(
        byte Position,
        string Name,
        string Team,
        byte Laps,
        ushort Grid,
        float Points,
        string Status,
        string BestLap,
        string Total,
        byte Penalties,
        byte NumPenalties);

    /// <summary>One completed lap of the selected driver.</summary>
    public sealed record LapRow(
        byte Lap,
        string Time,
        string Sector1,
        string Sector2,
        string Tyre,
        byte Age,
        byte Position,
        string Ers);

    /// <summary>One stored penalty of the selected session (lap issued on + text).</summary>
    public sealed record PenaltyRow(string Lap, string Detail);

    /// <summary>Grid row of the A-vs-B comparison: formatted best lap per side and
    /// per-sector deltas (B − A, "—" when a side is missing).</summary>
    public sealed record CompareRow(
        string Name,
        string BestA,
        string BestB,
        string LapDelta,
        string S1Delta,
        string S2Delta,
        string S3Delta);

    /// <summary>Picker option for the lap comparison: one traced lap with its formatted
    /// time.</summary>
    public sealed record TraceLapOption(
        byte LapNumber,
        byte CarIndex,
        uint LapTimeMs,
        string Label);

    /// <summary>One row of the driver championship (formatted for the grid).</summary>
    public sealed record ChampionshipRow(
        int Rank,
        string Name,
        string Team,
        int Races,
        double Points,
        int Wins,
        int Podiums,
        byte BestPosition);

    /// <summary>One row of the constructor championship.</summary>
    public sealed record ConstructorRow(
        int Rank,
        string Team,
        int Races,
        double Points,
        int Wins);

    /// <summary>One point of the player's form curve (chronological).</summary>
    public sealed record FormRow(
        string Track,
        string Date,
        byte Position,
        double Points);

    /// <summary>One row of the local ELO leaderboard (formatted strings — the rating is
    /// culture-invariant in Core).</summary>
    public sealed record RatingRow(
        int Rank,
        string Name,
        string Team,
        int Races,
        string Rating,
        int Wins,
        byte BestPosition,
        bool IsPlayer);
}