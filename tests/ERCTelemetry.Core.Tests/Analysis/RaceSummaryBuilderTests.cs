using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>RaceSummaryBuilder (Layer 1): deterministic German narrative from a RaceReport —
/// intro, winner, player story, player events and the duel paragraph.</summary>
public sealed class RaceSummaryBuilderTests
{
    private static ReportHeader Header() => new(
        SessionId: 1, SessionType: "Race", Track: "F1_Bahrain", TotalLaps: 57, TrackLength: 5412,
        GameMode: "OnlineCustom", Weather: "Clear", IsNetworkGame: true, TrackTemp: 30, AirTemp: 24,
        TimeOfDay: 0, Formula: "F1 26", PitSpeedLimit: 80, NumDrsZones: 3, RuleSet: "Standard",
        SessionDuration: 3600, StartedUtc: "2026-09-07T12:00:00Z", EndReason: "checkered",
        NumDrivers: 3, NumLapsStored: 57);

    private static PerDriverSummary Driver(
        byte car, string name, byte position, ushort grid, uint bestMs = 0,
        byte stops = 0, int gained = 0, int led = 0, int penalties = 0) => new(
        CarIndex: car, Name: name, Team: Team.McLaren, RaceNumber: 7, Position: position,
        Points: 0, ResultStatus: "Active", ResultReason: "Finished", GridPosition: grid,
        NumPitStops: stops, RacingLaps: 57, BestLapMs: bestMs, AverageLapMs: 90_000,
        ConsistencySigmaMs: 500, S1: new(30_000, 30_100, 100), S2: new(30_000, 30_100, 100),
        S3: new(30_000, 30_100, 100), LapsValid: 57, LapsLed: led, PositionsGained: gained,
        PenaltiesSeconds: penalties, FuelUsedLitres: 100, ErsUsedMegajoules: 5,
        TotalRaceTimeSeconds: 3600, Stints: [], DamageEndState: null);

    private static RaceReport Report(
        PerDriverSummary? player = null,
        IReadOnlyList<TimelineEntry>? timeline = null,
        DuelReport? duel = null) => new(
        Header: Header(),
        Classification:
        [
            Driver(0, "Alice", 1, 1, 88_000),
            Driver(1, "Bob", 2, 3, 88_500),
            Driver(2, "Charlie", 3, 2, 89_000),
        ],
        Player: player,
        Consistency: [],
        LapSeries: [],
        FieldPositions: [],
        Duel: duel,
        DamageLog: [],
        Timeline: timeline ?? [],
        Setup: null,
        Traces: []);

    [Fact]
    public void Empty_report_yields_empty_summary()
    {
        var summary = RaceSummaryBuilder.Build(new RaceReport(null, [], null, [], [], [], null, [], [], null, []));

        Assert.Equal(string.Empty, summary.Title);
        Assert.Empty(summary.Paragraphs);
    }

    [Fact]
    public void Intro_mentions_track_laps_drivers_and_weather()
    {
        var summary = RaceSummaryBuilder.Build(Report());

        Assert.Contains("Rennen auf Bahrain über 57 Runden", summary.Paragraphs[0]);
        Assert.Contains("3 Fahrer am Start", summary.Paragraphs[0]);
        Assert.Contains("Wetter: Clear", summary.Paragraphs[0]);
    }

    [Fact]
    public void Winner_paragraph_names_podium()
    {
        var summary = RaceSummaryBuilder.Build(Report());

        Assert.Contains("Sieg für Alice vor Bob und Charlie.", summary.Paragraphs[1]);
    }

    [Fact]
    public void Player_story_reports_grid_to_finish_and_gains()
    {
        var player = Driver(0, "Alice", 3, 5, 88_000, stops: 2, gained: 2, led: 4);
        var summary = RaceSummaryBuilder.Build(Report(player));

        var story = summary.Paragraphs[2];
        Assert.Contains("Du startest von P5 und beendest das Rennen auf P3 (2 Positionen gewonnen).", story);
        Assert.Contains("Deine schnellste Runde: 1:28.000.", story);
        Assert.Contains("Du legst 2 Boxenstopps ein.", story);
        Assert.Contains("Du führst 4 Runden an.", story);
    }

    [Fact]
    public void Player_story_reports_lost_positions()
    {
        var player = Driver(0, "Alice", 5, 2, 88_000);
        var summary = RaceSummaryBuilder.Build(Report(player));

        Assert.Contains("Du startest von P2 und beendest das Rennen auf P5 (3 Positionen verloren).", summary.Paragraphs[2]);
    }

    [Fact]
    public void Player_events_weave_overtakes_and_safety_car()
    {
        var player = Driver(0, "Alice", 1, 1, 88_000);
        var timeline = new List<TimelineEntry>
        {
            new(DateTimeOffset.UtcNow, "Overtake", 0, 1, "Alice passed Bob", 12, 0),
            new(DateTimeOffset.UtcNow, "Overtake", 2, 0, "Charlie passed Alice", 20, 0),
            new(DateTimeOffset.UtcNow, "FastestLap", 0, null, "Alice: fastest lap", 30, 0),
            new(DateTimeOffset.UtcNow, "SafetyCar", null, null, "Safety car: Deployed", 25, 0),
        };
        var summary = RaceSummaryBuilder.Build(Report(player, timeline));

        var events = summary.Paragraphs[3];
        Assert.Contains("Du überholst Bob in Runde 12.", events);
        Assert.Contains("Du wirst von Charlie überholt in Runde 20.", events);
        Assert.Contains("Schnellste Runde in Runde 30.", events);
        Assert.Contains("Safety Car in Runde 25.", events);
    }

    [Fact]
    public void Player_events_fall_back_when_nothing_happened()
    {
        var player = Driver(0, "Alice", 1, 1, 88_000);
        var summary = RaceSummaryBuilder.Build(Report(player));

        Assert.Contains("Keine besonderen Ereignisse für dich gespeichert.", summary.Paragraphs[3]);
    }

    [Fact]
    public void Player_events_parse_german_overtake_texts()
    {
        // German race control emits its own formats ("du hast X überholt" for the player
        // as mover, "X hat dich überholt" when the player is overtaken). The English
        // "X passed Y" slicing must not garble them or throw on a missing substring.
        var player = Driver(0, "Alice", 1, 1, 88_000);
        var timeline = new List<TimelineEntry>
        {
            new(DateTimeOffset.UtcNow, "Overtake", 0, 1, "OVERHAUL! P3 — du hast Bob überholt", 12, 0),
            new(DateTimeOffset.UtcNow, "Overtake", 1, 0, "Bob hat dich überholt", 20, 0),
        };
        var summary = RaceSummaryBuilder.Build(Report(player, timeline));

        var events = summary.Paragraphs[3];
        Assert.Contains("Du überholst Bob in Runde 12.", events);
        Assert.Contains("Du wirst von Bob überholt in Runde 20.", events);
    }

    [Fact]
    public void Duel_paragraph_reports_lap_wins_and_best_delta()
    {
        var player = Driver(0, "Alice", 1, 1, 88_000);
        var duel = new DuelReport(
            A: player, B: Driver(1, "Bob", 2, 3, 88_500),
            LapWinsA: 30, LapWinsB: 20, LapPairs: [], SectorWins: new(10, 5, 10, 5, 10, 5),
            BestDeltaMs: -500, AverageDeltaMs: -200, SigmaDeltaMs: 100, Stints: [],
            TraceTips: [], CornerLosses: [new CornerLoss(3, 0.4f, 0, 0)]);
        var summary = RaceSummaryBuilder.Build(Report(player, duel: duel));

        var story = summary.Paragraphs[4];
        Assert.Contains("Im Duell gegen Bob: 30:20 Runden gewonnen.", story);
        Assert.Contains("Beste Runde: Alice um 0,500 s schneller.", story);
        Assert.Contains("Größter Verlust: Kurve 3 (−0,400 s).", story);
    }
}
