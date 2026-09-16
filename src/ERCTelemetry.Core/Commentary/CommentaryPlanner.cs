using System.Globalization;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Commentary;

/// <summary>Layer-1 rule engine of the live AI commentator: turns the snapshot + event
/// feed into German commentary lines for the stream overlay. Deterministic, throttled,
/// headless-testable. Stateful (single consumer) — tracks the player car index, the
/// per-stint box-window state and comment cooldowns so nothing repeats every snapshot.</summary>
public sealed class CommentaryPlanner
{
    // Layer-1 rules (IDEAS.md): gap to the car behind < 0.5s → "Druck von hinten!",
    // tyres > 80 % wear → "Boxenfenster!", rain rising → "Regen in ~3 Runden".
    private const int PressureGapMs = 500;
    private const byte BoxWindowWearPercent = 80;
    private const byte RainPercentThreshold = 50;
    private const int RainLookaheadMinutes = 5;

    private static readonly TimeSpan PressureCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RainCooldown = TimeSpan.FromSeconds(60);

    private byte _playerCarIndex = 255; // 255 = unknown (no session yet)
    private DateTimeOffset _lastPressureAt = DateTimeOffset.MinValue;
    private bool _boxWindowArmed = true; // true = no box-window comment yet this stint
    private DateTimeOffset _lastRainAt = DateTimeOffset.MinValue;

    /// <summary>Returns the German commentary lines for one snapshot (usually 0 or 1).
    /// Called by the overlay pump on every drained snapshot.</summary>
    public IReadOnlyList<string> Plan(TelemetrySnapshot snapshot)
    {
        var lines = new List<string>(1);
        var now = DateTimeOffset.UtcNow;

        if (snapshot.Meta is { } meta)
        {
            _playerCarIndex = meta.PlayerCarIndex;
            AddRainLine(meta, now, lines);
        }

        var player = snapshot.Standings.FirstOrDefault(r => r.IsPlayer);
        if (player is not null)
        {
            AddPressureLine(snapshot, player, now, lines);
            AddBoxWindowLine(snapshot, player, lines);
        }

        return lines;
    }

    /// <summary>Returns the German commentary lines for one store event (usually 0 or 1).
    /// Called by the overlay pump for every drained event.</summary>
    public IReadOnlyList<string> PlanEvent(StoreEvent storeEvent)
    {
        var lines = new List<string>(1);
        switch (storeEvent)
        {
            case Overtake o when o.CarIndex == _playerCarIndex:
                lines.Add($"Überholmanöver! {o.DriverName} zieht an {o.PassedDriverName} vorbei.");
                break;

            case Overtake o when o.PassedCarIndex == _playerCarIndex:
                lines.Add($"{o.DriverName} überholt {o.PassedDriverName}.");
                break;

            case RaceControl rc:
                AddRaceControlLine(rc.Event, lines);
                break;
        }

        return lines;
    }

    /// <summary>Comments when the car behind is under half a second away — throttled so a
    /// long battle does not spam the feed every snapshot.</summary>
    private void AddPressureLine(TelemetrySnapshot snapshot, StandingsRow player, DateTimeOffset now, List<string> lines)
    {
        if (now - _lastPressureAt < PressureCooldown || player.Position == 0)
        {
            return;
        }

        var behind = snapshot.Standings.FirstOrDefault(r => r.Position == player.Position + 1);
        if (behind is null || behind.PitStatus is PitStatus.Pitting or PitStatus.InPitArea)
        {
            return;
        }

        // The car behind's gap to the car in front IS the gap to the player (the player is
        // that car); fall back to the gap-to-leader difference when the packet gap is 0.
        var gapMs = behind.GapToCarInFrontMs > 0
            ? behind.GapToCarInFrontMs
            : behind.GapToLeaderMs - player.GapToLeaderMs;
        if (gapMs <= 0 || gapMs >= PressureGapMs)
        {
            return;
        }

        _lastPressureAt = now;
        lines.Add($"Druck von hinten! {behind.Name} ist nur {FormatGap(gapMs)} dahinter.");
    }

    /// <summary>Comments once per stint when the player's worst tyre wear crosses 80 %;
    /// a lower wear on a later snapshot means fresh tyres — re-arm.</summary>
    private void AddBoxWindowLine(TelemetrySnapshot snapshot, StandingsRow player, List<string> lines)
    {
        var wear = snapshot.Tyres?.FirstOrDefault(t => t.CarIndex == player.CarIndex)?.WearPercent;
        if (wear is null)
        {
            return; // no CarDamage data yet
        }

        if (wear >= BoxWindowWearPercent)
        {
            if (_boxWindowArmed)
            {
                _boxWindowArmed = false;
                lines.Add($"Boxenfenster! Reifen bei {wear}% Verschleiß.");
            }
        }
        else
        {
            _boxWindowArmed = true; // fresh tyres — re-arm
        }
    }

    /// <summary>Comments when the forecast shows meaningful rain inside the next few
    /// minutes — throttled so a static forecast does not repeat every snapshot.</summary>
    private void AddRainLine(SessionMeta meta, DateTimeOffset now, List<string> lines)
    {
        if (now - _lastRainAt < RainCooldown || meta.Forecast is not { Count: > 0 } forecast)
        {
            return;
        }

        var rain = forecast
            .Where(f => f.RainPercent >= RainPercentThreshold && f.TimeOffsetMinutes <= RainLookaheadMinutes)
            .OrderBy(f => f.TimeOffsetMinutes)
            .FirstOrDefault();
        if (rain is null)
        {
            return;
        }

        _lastRainAt = now;
        lines.Add(rain.TimeOffsetMinutes == 0 ? "Regen gleich!" : "Regen in ~3 Runden");
    }

    private void AddRaceControlLine(RaceEventEntry ev, List<string> lines)
    {
        // Global moments — comment regardless of who is involved.
        switch (ev.Type)
        {
            case "SafetyCar":
                lines.Add("Safety Car auf der Strecke!");
                return;
            case "LightsOut":
                lines.Add("Ampeln aus, los geht's!");
                return;
            case "DrsDisabled":
                lines.Add("DRS deaktiviert.");
                return;
            case "StartLights": // countdown events (5→1) — the lights-out moment is "LightsOut"
                return;
        }

        // Player-specific moments.
        if (ev.CarIndex != _playerCarIndex)
        {
            return;
        }

        switch (ev.Type)
        {
            case "FastestLap": lines.Add("Schnellste Runde für den Spieler!"); break;
            case "RaceWinner": lines.Add("Der Spieler gewinnt das Rennen!"); break;
            case "Retirement": lines.Add("Der Spieler ist ausgeschieden."); break;
            case "Penalty": lines.Add("Strafe für den Spieler!"); break;
        }
    }

    /// <summary>+s.fff for gaps — invariant culture so the overlay never flips to a comma
    /// decimal separator on a German system.</summary>
    private static string FormatGap(int ms) =>
        $"+{(ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}s";
}
