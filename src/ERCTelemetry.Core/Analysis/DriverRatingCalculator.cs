using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Local ELO-style driver rating across all stored Race sessions. Every driver
/// starts at 1500; for each race, every pair of classified drivers is compared by
/// finishing position — the expected score comes from the rating difference, the actual
/// score is 1 for the driver ahead, 0 for the one behind, and a K-factor moves both
/// ratings. Deterministic, offline, headless-testable. The Discord leaderboard (IDEAS.md)
/// can later reuse the same model server-side.</summary>
public static class DriverRatingCalculator
{
    private const double InitialRating = 1500;
    private const double KFactor = 32;
    private const double RatingDivisor = 400;

    public static RatingModel Build(
        IReadOnlyList<ChampionshipRace> races,
        string? playerName = null)
    {
        var ratings = new Dictionary<string, DriverAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var race in races)
        {
            var classified = race.Results.Where(r => r.Position > 0).ToList();
            // Every classified driver gets a race count — even a solo finisher appears
            // on the leaderboard (at the initial rating).
            foreach (var r in classified)
            {
                GetOrAdd(ratings, r).AddRace(r);
            }

            for (var i = 0; i < classified.Count; i++)
            {
                for (var j = i + 1; j < classified.Count; j++)
                {
                    // a finished ahead of b (positions are 1-based, sorted ascending)
                    UpdatePair(ratings, classified[i], classified[j]);
                }
            }
        }

        var standings = ratings.Values
            .OrderByDescending(d => d.Rating)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select((d, i) => new DriverRating(
                i + 1, d.Name, d.Team, d.Races, d.Rating, d.Wins, d.BestPosition,
                IsPlayer: playerName is not null &&
                          string.Equals(d.Name, playerName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var player = standings.FirstOrDefault(s => s.IsPlayer);
        return new RatingModel(standings, player?.Rating);
    }

    /// <summary>One ELO update for a pair where <paramref name="a"/> finished ahead of
    /// <paramref name="b"/>: both ratings move by K × (actual − expected).</summary>
    private static void UpdatePair(
        Dictionary<string, DriverAccumulator> ratings,
        ChampionshipResult a,
        ChampionshipResult b)
    {
        var accA = GetOrAdd(ratings, a);
        var accB = GetOrAdd(ratings, b);

        var expectedA = 1.0 / (1.0 + Math.Pow(10, (accB.Rating - accA.Rating) / RatingDivisor));
        var expectedB = 1.0 - expectedA;

        accA.Rating += KFactor * (1.0 - expectedA);
        accB.Rating += KFactor * (0.0 - expectedB);
    }

    private static DriverAccumulator GetOrAdd(
        Dictionary<string, DriverAccumulator> ratings,
        ChampionshipResult r)
    {
        if (!ratings.TryGetValue(r.Name, out var acc))
        {
            acc = new DriverAccumulator(r.Name, r.Team);
            ratings[r.Name] = acc;
        }

        return acc;
    }

    /// <summary>Per-driver running state: current rating, race count, wins and best
    /// position. The team shown is the one from the driver's first classified race.</summary>
    private sealed class DriverAccumulator
    {
        public DriverAccumulator(string name, Team team)
        {
            Name = name;
            Team = team.ToString();
        }

        public string Name { get; }
        public string Team { get; }
        public double Rating { get; set; } = InitialRating;
        public int Races { get; private set; }
        public int Wins { get; private set; }
        public byte BestPosition { get; private set; } = byte.MaxValue;

        public void AddRace(ChampionshipResult r)
        {
            Races++;
            if (r.Position == 1)
            {
                Wins++;
            }

            if (r.Position < BestPosition)
            {
                BestPosition = r.Position;
            }
        }
    }
}

/// <summary>One row of the local ELO leaderboard. <see cref="IsPlayer"/> marks the
/// player's own row so the UI can highlight it.</summary>
public sealed record DriverRating(
    int Rank,
    string Name,
    string Team,
    int Races,
    double Rating,
    int Wins,
    byte BestPosition,
    bool IsPlayer);

/// <summary>The full local rating model: the leaderboard plus the player's own rating
/// (null when the player has no classified race yet).</summary>
public sealed record RatingModel(
    IReadOnlyList<DriverRating> Standings,
    double? PlayerRating);
