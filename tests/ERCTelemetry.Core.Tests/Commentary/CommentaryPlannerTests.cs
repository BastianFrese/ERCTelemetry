using ERCTelemetry.Core.Commentary;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Commentary;

public sealed class CommentaryPlannerTests
{
    private static StandingsRow Row(
        byte position, byte carIndex, string name, int gapToLeader, int gapToFront,
        bool isPlayer = false, PitStatus pit = PitStatus.None) => new(
        position, carIndex, name, Team.RedBullRacing, 1, 12, 91_000, 90_000,
        gapToLeader, gapToFront, pit, 0, ResultStatus.Active, ActualCompound.F1C3, 12, isPlayer);

    private static TelemetrySnapshot Snapshot(
        IReadOnlyList<StandingsRow>? standings = null,
        SessionMeta? meta = null,
        IReadOnlyList<TyreStatus>? tyres = null) => new(
        Meta: meta,
        Drivers: Array.Empty<DriverEntry>(),
        Standings: standings ?? Array.Empty<StandingsRow>(),
        FinalResults: Array.Empty<FinalResultRow>(),
        RecentEvents: Array.Empty<RaceEventEntry>(),
        Player: null,
        Rival: null,
        FrameVersion: 0,
        BuiltAtUtc: new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
        Tyres: tyres);

    private static SessionMeta Meta(byte playerCarIndex = 1, IReadOnlyList<ForecastSample>? forecast = null) =>
        new(1, SessionType.Race, Track.Bahrain, 44, 5412, false, playerCarIndex,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0, Forecast: forecast);

    // ---- pressure: gap to the car behind < 0.5s ----

    [Fact]
    public void Comments_when_the_car_behind_is_under_half_a_second()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            standings:
            [
                Row(3, 1, "Erdi", 5000, 2000, isPlayer: true),
                Row(4, 2, "Max", 5400, 400),
            ],
            meta: Meta());

        var lines = planner.Plan(snapshot);

        var line = Assert.Single(lines);
        Assert.Equal("Druck von hinten! Max ist nur +0.400s dahinter.", line);
    }

    [Fact]
    public void No_comment_when_the_gap_is_half_a_second_or_more()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            standings:
            [
                Row(3, 1, "Erdi", 5000, 2000, isPlayer: true),
                Row(4, 2, "Max", 5600, 600),
            ],
            meta: Meta());

        Assert.Empty(planner.Plan(snapshot));
    }

    [Fact]
    public void Pressure_comment_is_throttled()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            standings:
            [
                Row(3, 1, "Erdi", 5000, 2000, isPlayer: true),
                Row(4, 2, "Max", 5400, 400),
            ],
            meta: Meta());

        Assert.Single(planner.Plan(snapshot));
        Assert.Empty(planner.Plan(snapshot)); // within the 20s cooldown
    }

    [Fact]
    public void No_pressure_comment_without_a_player_row()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            standings: [Row(1, 2, "Max", 0, 0)],
            meta: Meta());

        Assert.Empty(planner.Plan(snapshot));
    }

    // ---- box window: tyre wear > 80% ----

    [Fact]
    public void Comments_once_per_stint_when_tyre_wear_crosses_80_percent()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            standings: [Row(1, 1, "Erdi", 0, 0, isPlayer: true)],
            meta: Meta(),
            tyres: [new TyreStatus(1, 85f, 0f)]);

        var line = Assert.Single(planner.Plan(snapshot));
        Assert.Equal("Boxenfenster! Reifen bei 85% Verschleiß.", line);
        Assert.Empty(planner.Plan(snapshot)); // once per stint
    }

    [Fact]
    public void Box_window_re_arms_after_fresh_tyres()
    {
        var planner = new CommentaryPlanner();
        var worn = Snapshot(
            standings: [Row(1, 1, "Erdi", 0, 0, isPlayer: true)],
            meta: Meta(),
            tyres: [new TyreStatus(1, 90f, 0f)]);
        var fresh = Snapshot(
            standings: [Row(1, 1, "Erdi", 0, 0, isPlayer: true)],
            meta: Meta(),
            tyres: [new TyreStatus(1, 10f, 0f)]);

        Assert.Single(planner.Plan(worn));
        Assert.Empty(planner.Plan(fresh)); // re-arms
        Assert.Single(planner.Plan(worn)); // comments again
    }

    [Fact]
    public void No_box_window_comment_below_80_percent()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            standings: [Row(1, 1, "Erdi", 0, 0, isPlayer: true)],
            meta: Meta(),
            tyres: [new TyreStatus(1, 60f, 0f)]);

        Assert.Empty(planner.Plan(snapshot));
    }

    // ---- rain: forecast ----

    [Fact]
    public void Comments_when_rain_is_forecast_within_a_few_minutes()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            meta: Meta(forecast: [new ForecastSample(3, Weather.LightRain, 60, 26, 20)]));

        var line = Assert.Single(planner.Plan(snapshot));
        Assert.Equal("Regen in ~3 Runden", line);
    }

    [Fact]
    public void Comments_immediately_when_rain_is_here()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            meta: Meta(forecast: [new ForecastSample(0, Weather.LightRain, 70, 26, 20)]));

        var line = Assert.Single(planner.Plan(snapshot));
        Assert.Equal("Regen gleich!", line);
    }

    [Fact]
    public void Rain_comment_is_throttled()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            meta: Meta(forecast: [new ForecastSample(3, Weather.LightRain, 60, 26, 20)]));

        Assert.Single(planner.Plan(snapshot));
        Assert.Empty(planner.Plan(snapshot)); // within the 60s cooldown
    }

    [Fact]
    public void No_rain_comment_when_forecast_is_dry()
    {
        var planner = new CommentaryPlanner();
        var snapshot = Snapshot(
            meta: Meta(forecast: [new ForecastSample(3, Weather.Clear, 0, 26, 20)]));

        Assert.Empty(planner.Plan(snapshot));
    }

    // ---- events ----

    [Fact]
    public void Comments_when_the_player_overtakes()
    {
        var planner = new CommentaryPlanner();
        planner.Plan(Snapshot(meta: Meta(playerCarIndex: 1))); // learn the player index

        var lines = planner.PlanEvent(new Overtake(1, "Erdi", 2, "Max", 3, 12));

        var line = Assert.Single(lines);
        Assert.Equal("Überholmanöver! Erdi zieht an Max vorbei.", line);
    }

    [Fact]
    public void Comments_when_the_player_is_passed()
    {
        var planner = new CommentaryPlanner();
        planner.Plan(Snapshot(meta: Meta(playerCarIndex: 1)));

        var lines = planner.PlanEvent(new Overtake(2, "Max", 1, "Erdi", 4, 12));

        var line = Assert.Single(lines);
        Assert.Equal("Max überholt Erdi.", line);
    }

    [Fact]
    public void Comments_on_global_race_control_moments()
    {
        var planner = new CommentaryPlanner();
        planner.Plan(Snapshot(meta: Meta(playerCarIndex: 1)));

        var lines = planner.PlanEvent(new RaceControl(new RaceEventEntry(
            DateTimeOffset.UtcNow, "SafetyCar", null, "Safety car deployed")));

        var line = Assert.Single(lines);
        Assert.Equal("Safety Car auf der Strecke!", line);
    }

    [Fact]
    public void Comments_on_lights_out_but_not_the_countdown()
    {
        var planner = new CommentaryPlanner();
        planner.Plan(Snapshot(meta: Meta(playerCarIndex: 1)));

        // The countdown (STLG, sent once per light 5→1) must stay silent — the
        // "Ampeln aus" moment is the separate LightsOut (LGOT) event.
        var countdown = planner.PlanEvent(new RaceControl(new RaceEventEntry(
            DateTimeOffset.UtcNow, "StartLights", null, "Start lights: 5 on")));
        Assert.Empty(countdown);

        var lightsOut = planner.PlanEvent(new RaceControl(new RaceEventEntry(
            DateTimeOffset.UtcNow, "LightsOut", null, "Lights out")));
        var line = Assert.Single(lightsOut);
        Assert.Equal("Ampeln aus, los geht's!", line);
    }

    [Fact]
    public void Comments_on_player_specific_race_control_moments()
    {
        var planner = new CommentaryPlanner();
        planner.Plan(Snapshot(meta: Meta(playerCarIndex: 1)));

        var lines = planner.PlanEvent(new RaceControl(new RaceEventEntry(
            DateTimeOffset.UtcNow, "FastestLap", 1, "Fastest lap")));

        var line = Assert.Single(lines);
        Assert.Equal("Schnellste Runde für den Spieler!", line);
    }

    [Fact]
    public void Ignores_race_control_moments_of_other_cars()
    {
        var planner = new CommentaryPlanner();
        planner.Plan(Snapshot(meta: Meta(playerCarIndex: 1)));

        var lines = planner.PlanEvent(new RaceControl(new RaceEventEntry(
            DateTimeOffset.UtcNow, "FastestLap", 2, "Fastest lap")));

        Assert.Empty(lines);
    }
}
