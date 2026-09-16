using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Layer-1 AI-Coach tests: corner-loss advice tiers, trace-tip advice by kind,
/// total-loss aggregation, and the no-data guards.</summary>
public sealed class CoachReportBuilderTests
{
    private static PerDriverSummary Driver(string name) => new(
        CarIndex: 1,
        Name: name,
        Team: Team.RedBullRacing,
        RaceNumber: 1,
        Position: 3,
        Points: 15,
        ResultStatus: "Finished",
        ResultReason: "",
        GridPosition: 5,
        NumPitStops: 2,
        RacingLaps: 44,
        BestLapMs: 90_000,
        AverageLapMs: 92_000,
        ConsistencySigmaMs: 500,
        S1: new SectorStats(28_000, 28_500, 200),
        S2: new SectorStats(31_000, 31_500, 200),
        S3: new SectorStats(28_000, 28_500, 200),
        LapsValid: 40,
        LapsLed: 0,
        PositionsGained: 2,
        PenaltiesSeconds: 0,
        FuelUsedLitres: 100,
        ErsUsedMegajoules: 4,
        TotalRaceTimeSeconds: 5400,
        Stints: Array.Empty<StintSummary>(),
        DamageEndState: null);

    private static DuelReport Duel(
        IReadOnlyList<CornerLoss>? corners = null,
        IReadOnlyList<TraceTip>? tips = null) => new(
        A: Driver("Erdi"),
        B: Driver("Max"),
        LapWinsA: 20,
        LapWinsB: 24,
        LapPairs: Array.Empty<DuelLapPair>(),
        SectorWins: new SectorWinTally(5, 6, 5, 6, 5, 6),
        BestDeltaMs: -100,
        AverageDeltaMs: 50,
        SigmaDeltaMs: 300,
        Stints: Array.Empty<StintPair>(),
        TraceTips: tips ?? Array.Empty<TraceTip>(),
        CornerLosses: corners ?? Array.Empty<CornerLoss>());

    // ---- corner losses ----

    [Fact]
    public void Builds_a_finding_per_corner_loss_with_advice()
    {
        var report = CoachReportBuilder.Build(Duel(
            corners: [new CornerLoss(3, 0.4f, 42f, 1200f)]));

        Assert.NotNull(report);
        var finding = Assert.Single(report!.Findings);
        Assert.Equal((byte)3, finding.CornerNumber);
        Assert.Equal(0.4f, finding.LossSeconds);
        Assert.Equal(42f, finding.AtPercent);
        Assert.Contains("Kurve 3", finding.Advice);
        Assert.Contains("−0.4s", finding.Advice);
    }

    [Fact]
    public void Big_loss_gets_the_strongest_advice()
    {
        var report = CoachReportBuilder.Build(Duel(
            corners: [new CornerLoss(1, 0.7f, 10f, 200f)]));

        var finding = Assert.Single(report!.Findings);
        Assert.Contains("verlierst du am meisten", finding.Advice);
    }

    [Fact]
    public void Small_loss_gets_the_gentle_advice()
    {
        var report = CoachReportBuilder.Build(Duel(
            corners: [new CornerLoss(5, 0.1f, 60f, 3000f)]));

        var finding = Assert.Single(report!.Findings);
        Assert.Contains("kleine Unsicherheit", finding.Advice);
    }

    [Fact]
    public void Total_loss_sums_all_corner_losses()
    {
        var report = CoachReportBuilder.Build(Duel(
            corners:
            [
                new CornerLoss(3, 0.4f, 42f, 1200f),
                new CornerLoss(7, 0.2f, 70f, 3500f),
            ]));

        Assert.Equal(0.6, report!.TotalLossSeconds, precision: 3);
    }

    // ---- trace tips ----

    [Fact]
    public void Trace_tip_advice_is_keyed_by_kind()
    {
        var report = CoachReportBuilder.Build(Duel(
            tips:
            [
                new TraceTip("loss", "Runde 7 verliert 0.3s ggü. Runde 9"),
                new TraceTip("brake", "Bremspunkt 40 m später"),
                new TraceTip("apex", "Apex 5 km/h langsamer"),
                new TraceTip("gain", "Runde 9 gewinnt 0.2s"),
            ]));

        Assert.Equal(4, report!.Findings.Count);
        Assert.Contains(report.Findings, f => f.Advice.Contains("DRS-Fenster"));
        Assert.Contains(report.Findings, f => f.Advice.Contains("Bremspunkt entsprechend"));
        Assert.Contains(report.Findings, f => f.Advice.Contains("Kurvenausfahrt"));
        Assert.Contains(report.Findings, f => f.Advice.Contains("Behalte diese Linie"));
    }

    [Fact]
    public void Unknown_tip_kind_passes_the_text_through()
    {
        var report = CoachReportBuilder.Build(Duel(
            tips: [new TraceTip("weird", "etwas Unerwartetes")]));

        var finding = Assert.Single(report!.Findings);
        Assert.Equal("etwas Unerwartetes", finding.Advice);
    }

    // ---- guards ----

    [Fact]
    public void Returns_null_for_no_duel()
    {
        Assert.Null(CoachReportBuilder.Build(null));
    }

    [Fact]
    public void Returns_null_when_there_is_nothing_to_coach_on()
    {
        Assert.Null(CoachReportBuilder.Build(Duel()));
    }

    [Fact]
    public void Report_names_the_rival()
    {
        var report = CoachReportBuilder.Build(Duel(
            corners: [new CornerLoss(3, 0.4f, 42f, 1200f)]));

        Assert.Equal("Max", report!.RivalName);
    }
}
