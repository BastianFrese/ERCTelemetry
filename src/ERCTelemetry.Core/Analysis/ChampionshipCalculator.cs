using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Mini-championship across stored Race sessions: driver standings, constructor
/// standings and the player's form curve. Pure aggregation — the DB hands over the raw
/// results, this class turns them into the leaderboard (points desc, then wins, then best
/// position).</summary>
public static class ChampionshipCalculator
{
    public static ChampionshipModel Build(
        IReadOnlyList<ChampionshipRace> races,
        IReadOnlyList<PlayerRaceResult> playerRaces)
    {
        var drivers = new Dictionary<string, DriverAccumulator>(StringComparer.OrdinalIgnoreCase);
        var constructors = new Dictionary<Team, ConstructorAccumulator>();

        foreach (var race in races)
        {
            foreach (var r in race.Results)
            {
                if (r.Position == 0)
                {
                    continue; // unclassified DNF — no points, no race count
                }

                if (!drivers.TryGetValue(r.Name, out var d))
                {
                    d = new DriverAccumulator(r.Name);
                    drivers[r.Name] = d;
                }
                d.Add(r);

                if (!constructors.TryGetValue(r.Team, out var c))
                {
                    c = new ConstructorAccumulator(r.Team);
                    constructors[r.Team] = c;
                }
                c.Add(r);
            }
        }

        var driverStandings = drivers.Values
            .OrderByDescending(d => d.Points)
            .ThenByDescending(d => d.Wins)
            .ThenBy(d => d.BestPosition)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select((d, i) => new DriverStanding(
                i + 1, d.Name, d.Team, d.Races, d.Points, d.Wins, d.Podiums, d.BestPosition))
            .ToList();

        var constructorStandings = constructors.Values
            .OrderByDescending(c => c.Points)
            .ThenByDescending(c => c.Wins)
            .ThenBy(c => c.Team.ToString(), StringComparer.Ordinal)
            .Select((c, i) => new ConstructorStanding(
                i + 1, c.Team, c.Races, c.Points, c.Wins))
            .ToList();

        var form = playerRaces
            .Select(p => new FormPoint(p.Track, p.StartedUtc, p.Position, p.Points))
            .ToList();

        return new ChampionshipModel(driverStandings, constructorStandings, form);
    }

    /// <summary>Per-driver running totals; the team shown is the one the driver raced
    /// most often (ties keep the first-seen team).</summary>
    private sealed class DriverAccumulator
    {
        private readonly Dictionary<Team, int> _teamCounts = new();
        private Team _currentTeam;

        public DriverAccumulator(string name) => Name = name;

        public string Name { get; }
        public int Races { get; private set; }
        public double Points { get; private set; }
        public int Wins { get; private set; }
        public int Podiums { get; private set; }
        public byte BestPosition { get; private set; } = byte.MaxValue;
        public string Team => _currentTeam.ToString();

        public void Add(ChampionshipResult r)
        {
            Races++;
            Points += r.Points;
            if (r.Position == 1)
            {
                Wins++;
            }

            if (r.Position <= 3)
            {
                Podiums++;
            }

            if (r.Position < BestPosition)
            {
                BestPosition = r.Position;
            }

            _teamCounts.TryGetValue(r.Team, out var count);
            _teamCounts[r.Team] = count + 1;
            if (Races == 1 || count + 1 > _teamCounts[_currentTeam])
            {
                _currentTeam = r.Team;
            }
        }
    }

    private sealed class ConstructorAccumulator
    {
        public ConstructorAccumulator(Team team) => Team = team;

        public Team Team { get; }
        public int Races { get; private set; }
        public double Points { get; private set; }
        public int Wins { get; private set; }

        public void Add(ChampionshipResult r)
        {
            Races++;
            Points += r.Points;
            if (r.Position == 1)
            {
                Wins++;
            }
        }
    }
}

/// <summary>One stored, finalized Race session with its classified results.</summary>
public sealed record ChampionshipRace(
    long SessionId,
    string Track,
    string StartedUtc,
    IReadOnlyList<ChampionshipResult> Results);

/// <summary>One driver's classification in one race.</summary>
public sealed record ChampionshipResult(
    string Name,
    Team Team,
    byte Position,
    float Points);

/// <summary>The player's own classification in one race (chronological — the form curve).</summary>
public sealed record PlayerRaceResult(
    string Track,
    string StartedUtc,
    byte Position,
    float Points);

/// <summary>The full mini-championship: two leaderboards plus the player's form curve.</summary>
public sealed record ChampionshipModel(
    IReadOnlyList<DriverStanding> DriverStandings,
    IReadOnlyList<ConstructorStanding> ConstructorStandings,
    IReadOnlyList<FormPoint> FormCurve);

/// <summary>One row of the driver championship.</summary>
public sealed record DriverStanding(
    int Rank,
    string Name,
    string Team,
    int Races,
    double Points,
    int Wins,
    int Podiums,
    byte BestPosition);

/// <summary>One row of the constructor championship.</summary>
public sealed record ConstructorStanding(
    int Rank,
    Team Team,
    int Races,
    double Points,
    int Wins);

/// <summary>One point of the player's form curve (chronological).</summary>
public sealed record FormPoint(
    string Track,
    string StartedUtc,
    byte Position,
    double Points);
