using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.VoiceAlerts;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.VoiceAlerts;

/// <summary>VoiceAlertPlanner: which German alerts the TTS service speaks for a given
/// store event. The planner is stateful (player index, tyre-stint dedup), so tests that
/// need a session feed a SessionStarted first.</summary>
public sealed class VoiceAlertPlannerTests
{
    private static SessionMeta Meta(byte playerCarIndex = 0, IReadOnlyList<ForecastSample>? forecast = null) =>
        new(1, SessionType.Race, Track.Bahrain, 3, 5412, true, playerCarIndex,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0, Forecast: forecast);

    private static LapCompleted PlayerLap(byte tyreAge, ActualCompound compound = ActualCompound.F1C3) =>
        new(0, "Alice", 5, 90_000u, 30_000, 30_000, compound, tyreAge, 1, 0f);

    private static RaceControl PlayerEvent(string type) =>
        new(new RaceEventEntry(DateTimeOffset.UtcNow, type, 0, "text"));

    [Fact]
    public void SessionStarted_announces_race_start()
    {
        var planner = new VoiceAlertPlanner();

        var alerts = planner.Plan(new SessionStarted(Meta()));

        Assert.Contains(alerts, a => a == "Rennen gestartet");
    }

    [Fact]
    public void SessionStarted_announces_qualifying_start()
    {
        var planner = new VoiceAlertPlanner();
        var meta = Meta() with { SessionType = SessionType.Qualifying1 };

        var alerts = planner.Plan(new SessionStarted(meta));

        Assert.Contains(alerts, a => a == "Qualifying gestartet");
    }

    [Fact]
    public void SessionStarted_speaks_rain_alert_when_forecast_has_rain()
    {
        var planner = new VoiceAlertPlanner();
        var meta = Meta(forecast: [new ForecastSample(6, Weather.LightRain, 80, 20, 18)]);

        var alerts = planner.Plan(new SessionStarted(meta));

        Assert.Contains(alerts, a => a == "Achtung, Regen in 6 Minuten");
    }

    [Fact]
    public void SessionStarted_skips_rain_alert_when_forecast_is_dry()
    {
        var planner = new VoiceAlertPlanner();
        var meta = Meta(forecast: [new ForecastSample(6, Weather.Clear, 0, 20, 18)]);

        var alerts = planner.Plan(new SessionStarted(meta));

        Assert.DoesNotContain(alerts, a => a.StartsWith("Achtung", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionStarted_skips_rain_alert_when_rain_is_too_far_away()
    {
        var planner = new VoiceAlertPlanner();
        var meta = Meta(forecast: [new ForecastSample(30, Weather.LightRain, 80, 20, 18)]);

        var alerts = planner.Plan(new SessionStarted(meta));

        Assert.DoesNotContain(alerts, a => a.StartsWith("Achtung", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionEnded_announces_player_position()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));
        var ended = new SessionEnded(1, "checkered",
            [new FinalResultRow(3, 0, "Alice", Team.McLaren, 7, 20, 5, 15f, ResultStatus.Active, 90_000u, 3600.0, 0, 0)]);

        var alerts = planner.Plan(ended);

        Assert.Contains(alerts, a => a == "Rennen beendet, Position 3");
    }

    [Fact]
    public void Penalty_for_player_speaks_alert()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(PlayerEvent("Penalty"));

        Assert.Contains(alerts, a => a == "Strafe für dich!");
    }

    [Fact]
    public void Warning_for_player_speaks_alert()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(PlayerEvent("Warning"));

        Assert.Contains(alerts, a => a == "Verwarnung für dich!");
    }

    [Fact]
    public void Penalty_for_other_car_is_silent()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));
        var other = new RaceControl(
            new RaceEventEntry(DateTimeOffset.UtcNow, "Penalty", 1, "Bob: Penalty (5s) · TrackLimits"));

        var alerts = planner.Plan(other);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Overtake_by_player_speaks_alert()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(new Overtake(0, "Alice", 1, "Bob", 3, 5));

        Assert.Contains(alerts, a => a == "Du überholst Bob");
    }

    [Fact]
    public void Overtake_on_player_speaks_alert()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(new Overtake(1, "Bob", 0, "Alice", 2, 5));

        Assert.Contains(alerts, a => a == "Bob überholt dich");
    }

    [Fact]
    public void Overtake_by_player_speaks_english_alert_in_english_mode()
    {
        var planner = new VoiceAlertPlanner { Language = VoiceLanguage.English };
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(new Overtake(0, "Alice", 1, "Bob", 3, 5));

        Assert.Contains(alerts, a => a == "Overtake");
    }

    [Fact]
    public void Overtake_on_player_speaks_english_alert_in_english_mode()
    {
        var planner = new VoiceAlertPlanner { Language = VoiceLanguage.English };
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(new Overtake(1, "Bob", 0, "Alice", 2, 5));

        Assert.Contains(alerts, a => a == "Car behind");
    }

    [Fact]
    public void SafetyCar_speaks_alert_for_everyone()
    {
        var planner = new VoiceAlertPlanner();
        var sc = new RaceControl(
            new RaceEventEntry(DateTimeOffset.UtcNow, "SafetyCar", null, "Safety car: Deployed (Full)"));

        var alerts = planner.Plan(sc);

        Assert.Contains(alerts, a => a == "Safety Car auf der Strecke");
    }

    [Fact]
    public void LightsOut_speaks_alert()
    {
        var planner = new VoiceAlertPlanner();
        var lightsOut = new RaceControl(
            new RaceEventEntry(DateTimeOffset.UtcNow, "LightsOut", null, "Lights out"));

        var alerts = planner.Plan(lightsOut);

        Assert.Contains(alerts, a => a == "Ampeln aus, los geht's!");
    }

    [Fact]
    public void StartLights_countdown_is_silent()
    {
        // The countdown (STLG, sent once per light 5→1) must NOT speak "Ampeln aus" —
        // that moment is the separate LightsOut (LGOT) event.
        var planner = new VoiceAlertPlanner();
        var lights = new RaceControl(
            new RaceEventEntry(DateTimeOffset.UtcNow, "StartLights", null, "Start lights: 5 on"));

        var alerts = planner.Plan(lights);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Tyre_alert_when_age_reaches_threshold()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(PlayerLap(tyreAge: 25)); // F1C3 → 25 laps

        Assert.Contains(alerts, a => a == "Box, Box, Reifen sind durch!");
    }

    [Fact]
    public void Tyre_alert_fires_once_per_stint()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        planner.Plan(PlayerLap(tyreAge: 25));
        var alerts = planner.Plan(PlayerLap(tyreAge: 26));

        Assert.Empty(alerts);
    }

    [Fact]
    public void Tyre_alert_rearms_after_fresh_tyres()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        planner.Plan(PlayerLap(tyreAge: 25)); // alert
        planner.Plan(PlayerLap(tyreAge: 1));  // fresh tyres — re-arm
        var alerts = planner.Plan(PlayerLap(tyreAge: 25)); // alert again

        Assert.Contains(alerts, a => a == "Box, Box, Reifen sind durch!");
    }

    [Fact]
    public void Tyre_alert_not_before_threshold()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));

        var alerts = planner.Plan(PlayerLap(tyreAge: 5));

        Assert.Empty(alerts);
    }

    [Fact]
    public void Tyre_alert_ignores_other_cars()
    {
        var planner = new VoiceAlertPlanner();
        planner.Plan(new SessionStarted(Meta()));
        var other = new LapCompleted(1, "Bob", 5, 91_000u, 30_400, 30_200, ActualCompound.F1C3, 25, 2, 0f);

        var alerts = planner.Plan(other);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Player_events_are_silent_before_a_session_starts()
    {
        var planner = new VoiceAlertPlanner();

        var alerts = planner.Plan(PlayerEvent("Penalty"));

        Assert.Empty(alerts);
    }
}
