using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using ERCTelemetry.App.Composition;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Race report tab view model: holds the built RaceReport, projects it into
/// observable row collections, rebuilds on picker changes and exports the HTML report.</summary>
public sealed class RaceReportViewModel : INotifyPropertyChanged
{
    private readonly TelemetryDb _db;
    private readonly LlmService? _llm;
    private int _rebuildVersion; // stale LLM results (from a superseded Rebuild) are dropped
    private HistoryViewModel.SessionRow? _selectedSession;
    private HistoryViewModel.DriverOption? _driverA;
    private HistoryViewModel.DriverOption? _driverB;
    private string _status = "Ready";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ReportRebuilt;

    public RaceReportViewModel(AppServices services)
    {
        _db = services.Database;
        _llm = services.Llm;
        Sessions = [];
        Drivers = [];
        Classification = [];
        StintRows = [];
        Timeline = [];
        DamageRows = [];
        DuelTips = [];
        Refresh();
    }

    public ObservableCollection<HistoryViewModel.SessionRow> Sessions { get; }
    public ObservableCollection<HistoryViewModel.DriverOption> Drivers { get; }
    public ObservableCollection<ClassificationRow> Classification { get; }
    public ObservableCollection<HistoryViewModel.StintRow> StintRows { get; }
    public ObservableCollection<string> Timeline { get; }
    public ObservableCollection<string> DamageRows { get; }
    public ObservableCollection<string> DuelTips { get; }
    public ObservableCollection<string> SummaryParagraphs { get; } = [];
    public ObservableCollection<SetupRow> SetupRows { get; } = [];
    public ObservableCollection<string> CoachFindings { get; } = [];

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>Session badge line for the overview card (empty when nothing selected).</summary>
    public string HeaderLine { get; private set; } = string.Empty;

    /// <summary>Player KPI line for the overview card.</summary>
    public string KpiLine { get; private set; } = string.Empty;

    /// <summary>Layer-1 race summary title (empty when no session is selected).</summary>
    public string SummaryTitle { get; private set; } = string.Empty;

    /// <summary>Layer-1 AI-Coach header: "AI-COACH — vs. {Rival}" (empty when no duel
    /// trace data is available).</summary>
    public string CoachTitle { get; private set; } = string.Empty;

    /// <summary>Layer-1 AI-Coach summary line: total time lost against the rival.</summary>
    public string CoachSummary { get; private set; } = string.Empty;

    /// <summary>Layer-2 LLM-polished race summary (empty when the LLM is off or failed —
    /// the Layer-1 paragraphs stay the source of truth).</summary>
    public string LlmSummary { get; private set; } = string.Empty;

    /// <summary>Layer-2 LLM-polished coach verdict (empty when the LLM is off or failed —
    /// the Layer-1 findings stay the source of truth).</summary>
    public string LlmCoach { get; private set; } = string.Empty;

    /// <summary>Share code of the currently shown setup (player's stored setup or an
    /// imported one); empty when no setup is available.</summary>
    public string SetupCode { get; private set; } = string.Empty;

    /// <summary>Feedback line for the setup card (copy/import result).</summary>
    public string SetupStatus { get; private set; } = string.Empty;

    /// <summary>The built report; null when no session is selected.</summary>
    public RaceReport? Report { get; private set; }

    public HistoryViewModel.SessionRow? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetField(ref _selectedSession, value))
            {
                LoadDrivers();
                Rebuild();
            }
        }
    }

    /// <summary>Duel driver A — any pair, racelab-style.</summary>
    public HistoryViewModel.DriverOption? DriverA
    {
        get => _driverA;
        set
        {
            if (SetField(ref _driverA, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>Duel driver B — any pair, racelab-style.</summary>
    public HistoryViewModel.DriverOption? DriverB
    {
        get => _driverB;
        set
        {
            if (SetField(ref _driverB, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>Reloads the session list; keeps the current selection when still present.</summary>
    public void Refresh()
    {
        var previous = _selectedSession?.Id;
        Sessions.Clear();
        foreach (var s in _db.GetSessions())
        {
            Sessions.Add(new HistoryViewModel.SessionRow(
                s.Id, s.SessionUid, $"{s.SessionType} · {TrackLabel(s.Track)}",
                FormatUtc(s.StartedUtc), LabelFinalized(s.Finalized), s.LapCount, s.DriverCount,
                s.Track));
        }

        if (previous is { } id)
        {
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == id);
        }
    }

    /// <summary>Entry point for the history tab's "open report" button.</summary>
    public void OpenReportFor(HistoryViewModel.SessionRow row)
    {
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == row.Id);
    }

    private void LoadDrivers()
    {
        Drivers.Clear();
        _driverA = null;
        _driverB = null;
        if (SelectedSession is not { } row)
        {
            return;
        }

        foreach (var d in _db.GetDriverNames(row.Id).OrderBy(k => k.Key))
        {
            Drivers.Add(new HistoryViewModel.DriverOption(d.Key, d.Value));
        }

        OnPropertyChanged(nameof(DriverA));
        OnPropertyChanged(nameof(DriverB));
    }

    private void Rebuild()
    {
        var version = ++_rebuildVersion; // any in-flight LLM result from an older build is dropped
        Classification.Clear();
        StintRows.Clear();
        Timeline.Clear();
        DamageRows.Clear();
        DuelTips.Clear();
        SummaryParagraphs.Clear();
        SetupRows.Clear();
        CoachFindings.Clear();
        CoachTitle = string.Empty;
        CoachSummary = string.Empty;
        LlmSummary = string.Empty;
        LlmCoach = string.Empty;
        SetupCode = string.Empty;
        SetupStatus = string.Empty;
        OnPropertyChanged(nameof(LlmSummary));
        OnPropertyChanged(nameof(LlmCoach));
        OnPropertyChanged(nameof(SetupCode));
        OnPropertyChanged(nameof(SetupStatus));
        if (SelectedSession is not { } row)
        {
            Report = null;
            HeaderLine = string.Empty;
            KpiLine = string.Empty;
            SummaryTitle = string.Empty;
            OnPropertyChanged(nameof(HeaderLine));
            OnPropertyChanged(nameof(KpiLine));
            OnPropertyChanged(nameof(SummaryTitle));
            OnPropertyChanged(nameof(Report));
            Status = "No session selected";
            return;
        }

        Report = RaceReportBuilder.Build(_db, row.Id, _driverA?.Index, _driverB?.Index);
        BuildOverview();
        var summary = RaceSummaryBuilder.Build(Report);
        SummaryTitle = summary.Title;
        foreach (var paragraph in summary.Paragraphs)
        {
            SummaryParagraphs.Add(paragraph);
        }

        OnPropertyChanged(nameof(SummaryTitle));
        if (_llm is { IsConfigured: true })
        {
            _ = SummarizeAsync(version, summary);
        }

        var leaderBest = Report.Classification
            .Select(x => x.BestLapMs)
            .Where(x => x > 0)
            .DefaultIfEmpty(0u)
            .Min();
        foreach (var s in Report.Classification)
        {
            Classification.Add(new ClassificationRow(
                s.Position, s.Name, s.Team.Display(), s.RaceNumber, s.RacingLaps,
                s.GridPosition, s.Points, FormatMs(s.BestLapMs), FormatDelta(s.BestLapMs, leaderBest),
                FormatMs(s.AverageLapMs), FormatMs(s.ConsistencySigmaMs), s.LapsValid,
                s.NumPitStops, s.PositionsGained, s.LapsLed, s.PenaltiesSeconds,
                s.ResultStatus, s.ResultReason));
        }

        if (Report.Player is { } player)
        {
            foreach (var st in player.Stints)
            {
                StintRows.Add(new HistoryViewModel.StintRow(
                    $"S{st.StintNumber}", st.Tyre, $"L{st.StartLap}–L{st.EndLap}",
                    FormatMs(st.BestLapMs), FormatMs(st.AverageLapMs),
                    FormatMs(st.DegradationMsPerLap), FormatMs(st.ConsistencySigmaMs)));
            }
        }

        foreach (var t in Report.Timeline)
        {
            Timeline.Add(
                t.Utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                (t.LapNumber > 0 ? $" L{t.LapNumber}" : string.Empty) +
                $" — {t.Text}");
        }

        foreach (var d in Report.DamageLog)
        {
            DamageRows.Add(
                $"L{d.LapNumber} car {d.CarIndex + 1}: {d.Damage.FrontLeftWing}% FLW, " +
                $"{d.Damage.FrontRightWing}% FRW, {d.Damage.RearWing}% RW, " +
                $"{d.Damage.Sidepod}% sidepod, {d.Damage.EngineDamage}% engine");
        }

        if (Report.Duel is { } duel)
        {
            foreach (var tip in duel.TraceTips)
            {
                DuelTips.Add(tip.Text);
            }
        }

        if (CoachReportBuilder.Build(Report.Duel) is { } coach)
        {
            CoachTitle = $"AI-COACH — vs. {coach.RivalName}";
            CoachSummary = $"Gesamtverlust: {coach.TotalLossSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
            foreach (var finding in coach.Findings)
            {
                CoachFindings.Add(finding.Advice);
            }

            if (_llm is { IsConfigured: true })
            {
                _ = CoachAsync(version, coach);
            }
        }

        OnPropertyChanged(nameof(CoachTitle));
        OnPropertyChanged(nameof(CoachSummary));

        if (Report.Setup is { } setup)
        {
            SetupCode = SetupCodec.Encode(setup);
            foreach (var sr in FormatSetup(setup))
            {
                SetupRows.Add(sr);
            }
        }

        OnPropertyChanged(nameof(SetupCode));
        OnPropertyChanged(nameof(Report));
        ReportRebuilt?.Invoke();
        Status = $"Report rebuilt ({Report.Classification.Count} drivers)";
    }

    /// <summary>Layer-2 summary politur. Fire-and-forget from <see cref="Rebuild"/>: the
    /// result is applied only when it is still the current build (the version guard drops
    /// stale answers after a picker change). No ConfigureAwait(false) — the continuation
    /// must resume on the UI thread to raise PropertyChanged.</summary>
    private async Task SummarizeAsync(int version, RaceSummary summary)
    {
        try
        {
            var text = await _llm!.SummarizeAsync(summary, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(text) || version != _rebuildVersion)
            {
                return;
            }

            LlmSummary = text;
            OnPropertyChanged(nameof(LlmSummary));
        }
        catch (Exception ex)
        {
            // Fire-and-forget: ein LLM-Ausfall (Netz, Auth, Timeout) darf die Task nicht
            // unobserved faulten und die L1-Paragrafen bleiben ohnehin die Quelle —
            // trotzdem sichtbar machen, warum die Politur fehlt (MEDIUM, 2026-09-16).
            App.Log($"L2-Racesummary fehlgeschlagen: {ex.Message}");
            Status = "L2-Zusammenfassung fehlgeschlagen — L1-Bericht bleibt maßgeblich.";
        }
    }

    /// <summary>Layer-2 coach politur — same fire-and-forget + version-guard contract as
    /// <see cref="SummarizeAsync"/>.</summary>
    private async Task CoachAsync(int version, CoachReport report)
    {
        try
        {
            var text = await _llm!.CoachAsync(report, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(text) || version != _rebuildVersion)
            {
                return;
            }

            LlmCoach = text;
            OnPropertyChanged(nameof(LlmCoach));
        }
        catch (Exception ex)
        {
            App.Log($"L2-AI-Coach fehlgeschlagen: {ex.Message}");
            Status = "L2-AI-Coach fehlgeschlagen — L1-Findings bleiben maßgeblich.";
        }
    }

    private void BuildOverview()
    {
        if (Report?.Header is not { } h)
        {
            HeaderLine = string.Empty;
            KpiLine = string.Empty;
            OnPropertyChanged(nameof(HeaderLine));
            OnPropertyChanged(nameof(KpiLine));
            return;
        }

        HeaderLine = string.Join(" · ",
            $"{h.SessionType}", TrackLabel(h.Track),
            $"{h.Weather}", $"{h.TrackTemp:0}°C track / {h.AirTemp:0}°C air",
            $"{h.RuleSet}", $"pit {h.PitSpeedLimit} km/h",
            $"{h.NumDrsZones} DRS zones", $"{h.NumDrivers} drivers");
        KpiLine = Report.Player is { } p
            ? string.Join(" · ",
                $"P{p.Position}", $"{p.Points:0} pts",
                p.GridPosition > 0 ? $"grid P{p.GridPosition} → P{p.Position}" : "",
                $"best {FormatMs(p.BestLapMs)}",
                $"Ø {FormatMs(p.AverageLapMs)}",
                $"σ {FormatMs(p.ConsistencySigmaMs)}",
                $"{p.NumPitStops} stops",
                $"{p.FuelUsedLitres:0.0} L fuel",
                $"{p.ErsUsedMegajoules:0.0} MJ ERS")
            : "no player data stored";
        OnPropertyChanged(nameof(HeaderLine));
        OnPropertyChanged(nameof(KpiLine));
    }

    /// <summary>Opens the HTML report export dialog and writes the current report.</summary>
    public void ExportHtml()
    {
        if (Report?.Header is not { } h)
        {
            Status = "Nothing to export — select a session first";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "HTML report (*.html)|*.html",
            FileName = $"race-report-{TrackLabel(h.Track).ToLowerInvariant()}-{h.SessionId}.html",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            RaceReportHtmlExporter.Write(dialog.FileName, Report);
            Status = $"Report exported: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Copies the current setup share code to the clipboard.</summary>
    public void CopySetupCode()
    {
        if (string.IsNullOrEmpty(SetupCode))
        {
            SetupStatus = "Kein Setup zum Kopieren vorhanden.";
            OnPropertyChanged(nameof(SetupStatus));
            return;
        }

        try
        {
            Clipboard.SetText(SetupCode);
            SetupStatus = "Code kopiert — in Chat/Discord einfügen.";
        }
        catch (Exception ex)
        {
            SetupStatus = $"Kopieren fehlgeschlagen: {ex.Message}";
        }

        OnPropertyChanged(nameof(SetupStatus));
    }

    /// <summary>Decodes a pasted share code and shows the values in the setup card.
    /// Invalid codes leave the current setup untouched and report an error.</summary>
    public void ImportSetupCode(string code)
    {
        var decoded = SetupCodec.Decode(code);
        if (decoded is null)
        {
            SetupStatus = "Ungültiger Code — bitte prüfen (falsches Format oder Tippfehler).";
            OnPropertyChanged(nameof(SetupStatus));
            return;
        }

        SetupRows.Clear();
        foreach (var row in FormatSetup(decoded))
        {
            SetupRows.Add(row);
        }

        SetupCode = SetupCodec.Encode(decoded);
        SetupStatus = "Setup importiert (nur Vorschau — wird nicht gespeichert).";
        OnPropertyChanged(nameof(SetupCode));
        OnPropertyChanged(nameof(SetupStatus));
    }

    /// <summary>Formats a setup into labeled rows for the setup card.</summary>
    private static IEnumerable<SetupRow> FormatSetup(CarSetupSnapshot s)
    {
        yield return new SetupRow("Frontflügel", s.FrontWing.ToString());
        yield return new SetupRow("Heckflügel", s.RearWing.ToString());
        yield return new SetupRow("Gasannahme", s.OnThrottle.ToString());
        yield return new SetupRow("Gasabgabe", s.OffThrottle.ToString());
        yield return new SetupRow("Sturz vorne", $"{s.FrontCamber:0.0}°");
        yield return new SetupRow("Sturz hinten", $"{s.RearCamber:0.0}°");
        yield return new SetupRow("Vorspur vorne", $"{s.FrontToe:0.00}°");
        yield return new SetupRow("Vorspur hinten", $"{s.RearToe:0.00}°");
        yield return new SetupRow("Federung vorne", s.FrontSuspension.ToString());
        yield return new SetupRow("Federung hinten", s.RearSuspension.ToString());
        yield return new SetupRow("Stabilisator vorne", s.FrontAntiRollBar.ToString());
        yield return new SetupRow("Stabilisator hinten", s.RearAntiRollBar.ToString());
        yield return new SetupRow("Fahrhöhe vorne", s.FrontSuspensionHeight.ToString());
        yield return new SetupRow("Fahrhöhe hinten", s.RearSuspensionHeight.ToString());
        yield return new SetupRow("Bremsdruck", s.BrakePressure.ToString());
        yield return new SetupRow("Bremsbalance", s.BrakeBias.ToString());
        yield return new SetupRow("Motorbremse", s.EngineBraking.ToString());
        yield return new SetupRow("Ballast", $"{s.Ballast:0.0} kg");
        yield return new SetupRow("Treibstoff", $"{s.FuelLoad:0.0} L");
        yield return new SetupRow("Reifendruck FL/FR/RL/RR",
            $"{s.TyresPressure[0]:0.0} / {s.TyresPressure[1]:0.0} / " +
            $"{s.TyresPressure[2]:0.0} / {s.TyresPressure[3]:0.0} PSI");
        yield return new SetupRow("Frontflügel (nächster Wert)", s.NextFrontWingValue.ToString());
    }

    private static string TrackLabel(string track) =>
        track.StartsWith("F1_", StringComparison.Ordinal) ? track[3..] : track;

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

    private static string FormatMs(uint ms) =>
        ms <= 0 ? string.Empty : $"{ms / 60000:D}:{ms % 60000 / 1000:D2}.{ms % 1000:D3}";

    private static string FormatMs(double ms) =>
        ms <= 0 ? string.Empty : $"{(long)ms / 60000}:{(long)ms % 60000 / 1000:00}.{(long)ms % 1000:000}";

    /// <summary>Delta to the leader's best lap as signed seconds ("-0.350"); empty for
    /// the leader itself (gap 0) and drivers without a lap time.</summary>
    private static string FormatDelta(uint bestMs, uint leaderBestMs)
    {
        if (bestMs <= 0 || leaderBestMs <= 0 || bestMs <= leaderBestMs)
        {
            return string.Empty;
        }

        var gapMs = (double)bestMs - leaderBestMs;
        return gapMs < 60_000
            ? $"-{gapMs / 1000:0.000}"
            : $"+{(long)gapMs / 60000}:{(long)gapMs % 60000 / 1000:00}.{(long)gapMs % 1000:000}";
    }

    /// <summary>One setup card row (label + formatted value).</summary>
    public sealed record SetupRow(string Label, string Value);

    /// <summary>One classification grid row (all values pre-formatted for binding).</summary>
    public sealed record ClassificationRow(
        byte Position,
        string Name,
        string Team,
        int RaceNumber,
        int Laps,
        ushort Grid,
        float Points,
        string Best,
        string Delta,
        string Average,
        string Sigma,
        int LapsValid,
        byte PitStops,
        int Gained,
        int Led,
        int Penalties,
        string Status,
        string Reason);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
