using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.VoiceAlerts;

/// <summary>Decides which voice alerts to speak from the store's event feed. Pure,
/// headless-testable logic — the App's TTS service just speaks the returned strings.
/// Stateful (single consumer): tracks the player car index and the tyre-stint alert
/// state so nothing repeats within a stint. The overtake announcements are AC-spotter
/// style and follow <see cref="Language"/>; the rest of the alerts stay German.</summary>
public sealed class VoiceAlertPlanner
{
    private const byte RainPercentThreshold = 50;
    private const int RainAlertMinutes = 15;

    private byte _playerCarIndex = 255;    // 255 = unknown (no session yet)
    private byte _lastTyreAlertAge = 255;  // 255 = armed (no alert yet this stint)

    /// <summary>Language of the overtake announcements (AC spotter style). Set by the
    /// App from the settings before each plan; defaults to German.</summary>
    public VoiceLanguage Language { get; set; } = VoiceLanguage.German;

    /// <summary>Returns the alert texts for one store event (usually 0 or 1).</summary>
    public IReadOnlyList<string> Plan(StoreEvent storeEvent)
    {
        var alerts = new List<string>(1);
        switch (storeEvent)
        {
            case SessionStarted s:
                _playerCarIndex = s.Meta.PlayerCarIndex;
                alerts.Add(SessionStartText(s.Meta));
                AddRainAlert(s.Meta, alerts);
                break;

            case SessionEnded ended:
                alerts.Add(SessionEndText(ended));
                break;

            case LapCompleted lap when lap.CarIndex == _playerCarIndex:
                AddTyreAlert(lap, alerts);
                break;

            case Overtake o when o.CarIndex == _playerCarIndex:
                alerts.Add(Language == VoiceLanguage.English
                    ? "Overtake"
                    : $"Du überholst {o.PassedDriverName}");
                break;

            case Overtake o when o.PassedCarIndex == _playerCarIndex:
                alerts.Add(Language == VoiceLanguage.English
                    ? "Car behind"
                    : $"{o.DriverName} überholt dich");
                break;

            case RaceControl rc:
                AddRaceControlAlert(rc.Event, alerts);
                break;
        }

        return alerts;
    }

    private static string SessionStartText(SessionMeta meta)
    {
        var type = meta.SessionType.ToString();
        return type switch
        {
            "Race" => "Rennen gestartet",
            "TimeTrial" => "Zeitrennen gestartet",
            _ when type.StartsWith("Qualifying", StringComparison.Ordinal) => "Qualifying gestartet",
            _ when type.StartsWith("Practice", StringComparison.Ordinal) => "Training gestartet",
            _ => "Session gestartet",
        };
    }

    private string SessionEndText(SessionEnded ended)
    {
        var position = ended.Results.FirstOrDefault(r => r.CarIndex == _playerCarIndex)?.Position;
        return position is > 0 ? $"Rennen beendet, Position {position}" : "Rennen beendet";
    }

    /// <summary>Speaks the earliest forecast sample with meaningful rain inside the next
    /// <see cref="RainAlertMinutes"/> minutes. The game refreshes the forecast during the
    /// session, but the planner only sees the session-start snapshot — forecast-change
    /// tracking belongs to the Wetter-Radar feature.</summary>
    private static void AddRainAlert(SessionMeta meta, List<string> alerts)
    {
        if (meta.Forecast is not { Count: > 0 } forecast)
        {
            return;
        }

        var rain = forecast
            .Where(f => f.RainPercent >= RainPercentThreshold && f.TimeOffsetMinutes <= RainAlertMinutes)
            .OrderBy(f => f.TimeOffsetMinutes)
            .FirstOrDefault();
        if (rain is null)
        {
            return;
        }

        alerts.Add(rain.TimeOffsetMinutes == 0
            ? "Achtung, es regnet gleich!"
            : $"Achtung, Regen in {rain.TimeOffsetMinutes} Minuten");
    }

    /// <summary>Speaks "Box, Box" once per stint when the player's tyre age reaches the
    /// compound's threshold. A lower age on a later lap means fresh tyres — re-arm.</summary>
    private void AddTyreAlert(LapCompleted lap, List<string> alerts)
    {
        if (lap.TyreAgeLaps == 0)
        {
            return; // no tyre data yet
        }

        var threshold = TyreAlertLaps(lap.TyreCompound);
        if (lap.TyreAgeLaps >= threshold && _lastTyreAlertAge == 255)
        {
            alerts.Add("Box, Box, Reifen sind durch!");
            _lastTyreAlertAge = lap.TyreAgeLaps;
        }
        else if (lap.TyreAgeLaps < _lastTyreAlertAge)
        {
            _lastTyreAlertAge = 255; // fresh tyres — re-arm
        }
    }

    private void AddRaceControlAlert(RaceEventEntry ev, List<string> alerts)
    {
        // Global events — speak regardless of who is involved.
        switch (ev.Type)
        {
            case "SafetyCar":
                alerts.Add("Safety Car auf der Strecke");
                return;
            case "LightsOut":
                alerts.Add("Ampeln aus, los geht's!");
                return;
            case "DrsDisabled":
                alerts.Add("DRS deaktiviert");
                return;
            case "StartLights": // countdown events (5→1) — the lights-out moment is "LightsOut"
            case "Flashback":
            case "SpeedTrap":
            case "Overtake": // spoken via the store's Overtake event (has both names)
                return;
        }

        // Player-specific events.
        if (ev.CarIndex != _playerCarIndex)
        {
            return;
        }

        switch (ev.Type)
        {
            case "FastestLap": alerts.Add("Schnellste Runde!"); break;
            case "RaceWinner": alerts.Add("Du gewinnst das Rennen!"); break;
            case "Retirement": alerts.Add("Du bist ausgeschieden"); break;
            case "Penalty": alerts.Add("Strafe für dich!"); break;
            case "Warning": alerts.Add("Verwarnung für dich!"); break;
            case "Collision": alerts.Add("Kollision!"); break;
            case "DriveThroughServed": alerts.Add("Durchfahrtsstrafe abgesessen"); break;
            case "StopGoServed": alerts.Add("Stop-and-Go abgesessen"); break;
        }
    }

    /// <summary>Compound-aware tyre alert threshold: F1C1 (härteste) → 35 Runden,
    /// F1C5 (weichste) → 15. Intermediates/Regen/unbekannt → 20.</summary>
    private static int TyreAlertLaps(ActualCompound compound)
    {
        var name = compound.ToString();
        if (name.StartsWith("F1C", StringComparison.Ordinal) && name.Length == 4 && char.IsAsciiDigit(name[3]))
        {
            return 40 - (name[3] - '0') * 5;
        }

        return 20;
    }
}
