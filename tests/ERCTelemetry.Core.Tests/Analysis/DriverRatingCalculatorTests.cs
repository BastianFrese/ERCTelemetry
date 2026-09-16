using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Local ELO rating tests: winner gains / loser loses, stronger driver stays
/// ahead, player highlighting, and the empty-input guard.</summary>
public sealed class DriverRatingCalculatorTests
{
    private static ChampionshipRace Race(params ChampionshipResult[] results) => new(
        SessionId: 1,
        Track: "Bahrain",
        StartedUtc: "2026-09-07T12:00:00Z",
        Results: results);

    private static ChampionshipResult Result(string name, byte position, Team team = Team.RedBullRacing) =>
        new(name, team, position, Points: position <= 10 ? (float)(11 - position) : 0);

    [Fact]
    public void Winner_gains_and_loser_loses_rating()
    {
        var model = DriverRatingCalculator.Build(
            [Race(Result("Erdi", 1), Result("Max", 2))]);

        var erdi = Assert.Single(model.Standings, s => s.Name == "Erdi");
        var max = Assert.Single(model.Standings, s => s.Name == "Max");
        Assert.True(erdi.Rating > 1500);
        Assert.True(max.Rating < 1500);
        Assert.True(erdi.Rating > max.Rating);
    }

    [Fact]
    public void Stronger_driver_stays_ahead_after_repeated_races()
    {
        var races = Enumerable.Range(0, 5)
            .Select(_ => Race(Result("Erdi", 1), Result("Max", 2)))
            .ToList();

        var model = DriverRatingCalculator.Build(races);

        var erdi = Assert.Single(model.Standings, s => s.Name == "Erdi");
        var max = Assert.Single(model.Standings, s => s.Name == "Max");
        Assert.Equal(1, erdi.Rank);
        Assert.Equal(2, max.Rank);
        Assert.True(erdi.Rating > max.Rating);
    }

    [Fact]
    public void Equal_results_keep_ratings_near_starting_point()
    {
        // One win each — both end up back near 1500 (the second race's underdog gains
        // slightly more, so the ratings are not exactly equal — ELO is not symmetric).
        var model = DriverRatingCalculator.Build(
        [
            Race(Result("Erdi", 1), Result("Max", 2)),
            Race(Result("Max", 1), Result("Erdi", 2)),
        ]);

        var erdi = Assert.Single(model.Standings, s => s.Name == "Erdi");
        var max = Assert.Single(model.Standings, s => s.Name == "Max");
        Assert.InRange(erdi.Rating, 1490, 1510);
        Assert.InRange(max.Rating, 1490, 1510);
    }

    [Fact]
    public void Tracks_race_count_wins_and_best_position()
    {
        var model = DriverRatingCalculator.Build(
        [
            Race(Result("Erdi", 1), Result("Max", 3)),
            Race(Result("Erdi", 2), Result("Max", 1)),
        ]);

        var erdi = Assert.Single(model.Standings, s => s.Name == "Erdi");
        Assert.Equal(2, erdi.Races);
        Assert.Equal(1, erdi.Wins);
        Assert.Equal((byte)1, erdi.BestPosition);
    }

    [Fact]
    public void Marks_the_player_row_and_reports_their_rating()
    {
        var model = DriverRatingCalculator.Build(
            [Race(Result("Erdi", 1), Result("Max", 2))],
            playerName: "Erdi");

        var erdi = Assert.Single(model.Standings, s => s.Name == "Erdi");
        Assert.True(erdi.IsPlayer);
        Assert.False(Assert.Single(model.Standings, s => s.Name == "Max").IsPlayer);
        Assert.Equal(erdi.Rating, model.PlayerRating);
    }

    [Fact]
    public void Player_rating_is_null_when_the_player_has_no_race()
    {
        var model = DriverRatingCalculator.Build(
            [Race(Result("Max", 1))],
            playerName: "Erdi");

        Assert.Null(model.PlayerRating);
        Assert.DoesNotContain(model.Standings, s => s.IsPlayer);
    }

    [Fact]
    public void Empty_input_produces_empty_standings()
    {
        var model = DriverRatingCalculator.Build([]);

        Assert.Empty(model.Standings);
        Assert.Null(model.PlayerRating);
    }

    [Fact]
    public void Unclassified_dnf_does_not_participate()
    {
        var model = DriverRatingCalculator.Build(
            [Race(Result("Erdi", 1), Result("Max", 0))]);

        Assert.Single(model.Standings);
        Assert.Equal("Erdi", model.Standings[0].Name);
    }
}
