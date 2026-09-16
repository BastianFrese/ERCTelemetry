using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>ChampionshipCalculator: the cross-session mini-championship — driver standings,
/// constructor standings and the player's form curve, aggregated from stored race results.</summary>
public sealed class ChampionshipCalculatorTests
{
    private static ChampionshipRace Race(string track, string utc, params ChampionshipResult[] results) =>
        new(0, track, utc, results);

    private static ChampionshipResult R(string name, Team team, byte pos, float points) =>
        new(name, team, pos, points);

    [Fact]
    public void Driver_standings_sum_points_and_count_wins()
    {
        var model = ChampionshipCalculator.Build(
            [
                Race("F1_Monza", "2026-09-01T10:00:00Z",
                    R("Erdi", Team.RedBullRacing, 1, 25),
                    R("Manuel", Team.Mercedes, 2, 18)),
                Race("F1_Spa", "2026-09-02T10:00:00Z",
                    R("Erdi", Team.RedBullRacing, 1, 25),
                    R("Manuel", Team.Mercedes, 3, 15)),
            ],
            []);

        var erdi = Assert.Single(model.DriverStandings, d => d.Name == "Erdi");
        Assert.Equal(1, erdi.Rank);
        Assert.Equal(50, erdi.Points);
        Assert.Equal(2, erdi.Wins);
        Assert.Equal(2, erdi.Podiums);
        Assert.Equal(2, erdi.Races);
        Assert.Equal(1, erdi.BestPosition);

        var manuel = Assert.Single(model.DriverStandings, d => d.Name == "Manuel");
        Assert.Equal(2, manuel.Rank);
        Assert.Equal(33, manuel.Points);
        Assert.Equal(0, manuel.Wins);
    }

    [Fact]
    public void Driver_standings_tie_break_by_wins_then_best_position()
    {
        // Same points: more wins wins; then better best position.
        var model = ChampionshipCalculator.Build(
            [
                Race("F1_A", "2026-09-01T10:00:00Z",
                    R("A", Team.Ferrari, 1, 25), R("B", Team.Mercedes, 2, 18),
                    R("C", Team.RedBullRacing, 3, 15), R("D", Team.Ferrari, 2, 18)),
                Race("F1_B", "2026-09-02T10:00:00Z",
                    R("A", Team.Ferrari, 2, 18), R("B", Team.Mercedes, 3, 15),
                    R("C", Team.RedBullRacing, 3, 15), R("D", Team.Ferrari, 4, 12)),
                Race("F1_C", "2026-09-03T10:00:00Z",
                    R("B", Team.Mercedes, 5, 10), R("C", Team.RedBullRacing, 4, 12),
                    R("D", Team.Ferrari, 4, 12)),
            ],
            []);

        // A: 43 pts, 1 win, best 1 — wins tiebreak over B
        // B: 43 pts, 0 wins, best 2
        // D: 42 pts, 0 wins, best 2 — best-position tiebreak over C
        // C: 42 pts, 0 wins, best 3
        Assert.Equal("A", model.DriverStandings[0].Name);
        Assert.Equal("B", model.DriverStandings[1].Name);
        Assert.Equal("D", model.DriverStandings[2].Name);
        Assert.Equal("C", model.DriverStandings[3].Name);
    }

    [Fact]
    public void Driver_standings_ignore_unclassified_dnf()
    {
        var model = ChampionshipCalculator.Build(
            [
                Race("F1_Monza", "2026-09-01T10:00:00Z",
                    R("Erdi", Team.RedBullRacing, 1, 25),
                    R("Manuel", Team.Mercedes, 0, 0)), // DNF, no position
            ],
            []);

        Assert.Single(model.DriverStandings);
        Assert.Equal(1, model.DriverStandings[0].Races);
    }

    [Fact]
    public void Driver_team_is_the_most_common_one()
    {
        var model = ChampionshipCalculator.Build(
            [
                Race("F1_A", "2026-09-01T10:00:00Z", R("Erdi", Team.RedBullRacing, 1, 25)),
                Race("F1_B", "2026-09-02T10:00:00Z", R("Erdi", Team.RedBullRacing, 1, 25)),
                Race("F1_C", "2026-09-03T10:00:00Z", R("Erdi", Team.Mercedes, 2, 18)),
            ],
            []);

        Assert.Equal("RedBullRacing", model.DriverStandings[0].Team);
    }

    [Fact]
    public void Constructor_standings_group_by_team()
    {
        var model = ChampionshipCalculator.Build(
            [
                Race("F1_Monza", "2026-09-01T10:00:00Z",
                    R("Erdi", Team.RedBullRacing, 1, 25),
                    R("Max", Team.RedBullRacing, 2, 18),
                    R("Manuel", Team.Mercedes, 3, 15)),
            ],
            []);

        var rb = Assert.Single(model.ConstructorStandings, c => c.Team == Team.RedBullRacing);
        Assert.Equal(1, rb.Rank);
        Assert.Equal(43, rb.Points);
        Assert.Equal(1, rb.Wins);
        Assert.Equal(2, rb.Races);

        var mer = Assert.Single(model.ConstructorStandings, c => c.Team == Team.Mercedes);
        Assert.Equal(2, mer.Rank);
        Assert.Equal(15, mer.Points);
    }

    [Fact]
    public void Form_curve_keeps_chronological_order()
    {
        var model = ChampionshipCalculator.Build(
            [],
            [
                new PlayerRaceResult("F1_Monza", "2026-09-01T10:00:00Z", 5, 10),
                new PlayerRaceResult("F1_Spa", "2026-09-02T10:00:00Z", 1, 25),
                new PlayerRaceResult("F1_Silverstone", "2026-09-03T10:00:00Z", 3, 15),
            ]);

        Assert.Equal(3, model.FormCurve.Count);
        Assert.Equal("F1_Monza", model.FormCurve[0].Track);
        Assert.Equal(5, model.FormCurve[0].Position);
        Assert.Equal(10, model.FormCurve[0].Points);
        Assert.Equal("F1_Silverstone", model.FormCurve[2].Track);
    }

    [Fact]
    public void Empty_inputs_yield_empty_model()
    {
        var model = ChampionshipCalculator.Build([], []);

        Assert.Empty(model.DriverStandings);
        Assert.Empty(model.ConstructorStandings);
        Assert.Empty(model.FormCurve);
    }
}
