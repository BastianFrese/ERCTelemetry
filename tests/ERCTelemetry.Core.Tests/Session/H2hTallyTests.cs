using F1Game.UDP.Data;
using F1Game.UDP.Enums;
using F1Game.UDP.Packets;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

/// <summary>Head-to-head duel tally computed in the single writer: laps won on the faster
/// lap time, position wins at shared lap completions — scored PER PAIR, so a car that
/// beats the whole field shows one win per duel, not 19 lumped into a shared counter.
/// Reset on session change.</summary>
public class H2hTallyTests
{
    private const ulong SessionA = 1111;
    private const ulong SessionB = 2222;

    private static PacketHeader Header(PacketType type, ulong sessionUid, byte playerIndex = 0) => new()
    {
        PacketFormat = 2026,
        GameYear = 26,
        GameMajorVersion = 1,
        PacketVersion = 1,
        PacketType = type,
        SessionUID = sessionUid,
        PlayerCarIndex = playerIndex,
    };

    private static SessionDataPacket SessionPacket(ulong uid, byte playerIndex = 0) => new()
    {
        Header = Header(PacketType.Session, uid, playerIndex),
        SessionType = SessionType.Race,
        TotalLaps = 20,
        TrackLength = 5000,
        Weather = Weather.Clear,
    };

    private static LapDataPacket LapPacket(ulong uid,
        params (int Index, byte Position, byte Lap, uint LastLap)[] cars)
    {
        var laps = new LapData[22];
        foreach (var (index, position, lap, lastLap) in cars)
        {
            laps[index] = new LapData
            {
                CarPosition = position,
                CurrentLapNum = lap,
                LastLapTimeInMS = lastLap,
                CurrentLapTimeInMS = 1,
            };
        }

        return new LapDataPacket { Header = Header(PacketType.LapData, uid), LapData = laps };
    }

    private static H2hTally? Tally(TelemetrySnapshot snapshot, byte carIndex, byte opponentIndex) =>
        snapshot.H2h?.FirstOrDefault(t =>
            (t.CarIndex == carIndex && t.OpponentIndex == opponentIndex) ||
            (t.CarIndex == opponentIndex && t.OpponentIndex == carIndex));

    /// <summary>Reads a side of a pair tally in the given driver's perspective.</summary>
    private static (int LapsWon, int PositionWins) For(H2hTally tally, byte carIndex) =>
        tally.CarIndex == carIndex
            ? (tally.LapsWon, tally.PositionWins)
            : (tally.OpponentLapsWon, tally.OpponentPositionWins);

    [Fact]
    public void Laps_won_count_both_directions()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Lap 1 runs uncompleted (seeds the lap tracker), then: lap 1 car 0 faster,
        // lap 2 car 1 faster, lap 3 car 0 faster again.
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0), (1, 2, 1, 0)));
        store.Apply(LapPacket(SessionA, (0, 1, 2, 90_000), (1, 2, 2, 91_000)));
        store.Apply(LapPacket(SessionA, (0, 1, 3, 92_000), (1, 2, 3, 91_000)));
        store.Apply(LapPacket(SessionA, (0, 1, 4, 89_000), (1, 2, 4, 90_000)));

        var snapshot = store.BuildSnapshot();
        var tally = Assert.Single(snapshot.H2h!);
        Assert.Equal((byte)0, tally.CarIndex);
        Assert.Equal((byte)1, tally.OpponentIndex);
        Assert.Equal((2, 3), For(tally, 0)); // car 0: 2 lap wins, 3 position wins (P1 every lap)
        Assert.Equal((1, 0), For(tally, 1));
    }

    [Fact]
    public void Exact_lap_time_ties_score_nobody()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        store.Apply(LapPacket(SessionA, (0, 1, 1, 90_000), (1, 1, 1, 90_000)));

        Assert.Null(store.BuildSnapshot().H2h);
    }

    [Fact]
    public void Position_wins_go_to_the_better_race_position()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Lap 1 runs uncompleted (seeds the tracker); then lap 1: both finished, car 0
        // ahead, lap 2: car 1 ahead, lap 3: car 0 ahead again.
        store.Apply(LapPacket(SessionA, (0, 1, 1, 0), (1, 2, 1, 0)));
        store.Apply(LapPacket(SessionA, (0, 1, 2, 90_000), (1, 2, 2, 91_000)));
        store.Apply(LapPacket(SessionA, (0, 2, 3, 90_000), (1, 1, 3, 89_000)));
        store.Apply(LapPacket(SessionA, (0, 1, 4, 89_000), (1, 2, 4, 90_000)));

        var snapshot = store.BuildSnapshot();
        var tally = Assert.Single(snapshot.H2h!);
        // Equal lap counts (1:1) but car 0 held the better position on laps 1 and 3,
        // car 1 only on lap 2.
        Assert.Equal((2, 2), For(tally, 0)); // car 0: (laps, pos) = 2 laps, 2 position wins
        Assert.Equal((1, 1), For(tally, 1));
    }

    [Fact]
    public void Unknown_positions_score_nobody()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Position 0 and 255 are the packet's "unknown" markers.
        store.Apply(LapPacket(SessionA, (0, 0, 1, 90_000), (1, 255, 1, 91_000)));

        var h2h = store.BuildSnapshot().H2h!;
        var tally0 = Assert.Single(h2h); // one pair entry; car 1's side stays all zero
        Assert.Equal((byte)0, tally0.CarIndex);
        Assert.Equal((byte)1, tally0.OpponentIndex);
        Assert.Equal(1, tally0.LapsWon);
        Assert.Equal(0, tally0.PositionWins);
        Assert.Equal(0, tally0.OpponentLapsWon);
        Assert.Equal(0, tally0.OpponentPositionWins);
    }

    [Fact]
    public void Comparison_fires_once_per_lap_even_when_shared_packet()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Both cars complete lap 1 inside the same packet.
        store.Apply(LapPacket(SessionA, (0, 1, 1, 90_000), (1, 2, 1, 91_000)));

        var h2h = store.BuildSnapshot().H2h!;
        var tally0 = Assert.Single(h2h); // one pair entry; car 1's side stays all zero
        Assert.Equal((byte)0, tally0.CarIndex);
        Assert.Equal((byte)1, tally0.OpponentIndex);
        Assert.Equal(1, tally0.LapsWon);
        Assert.Equal(1, tally0.PositionWins);
        Assert.Equal(0, tally0.OpponentLapsWon);
        Assert.Equal(0, tally0.OpponentPositionWins);
    }

    [Fact]
    public void Only_pairs_that_completed_a_shared_lap_are_listed()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Three cars finish lap 1: car 0 beats both, car 1 beats car 2.
        store.Apply(LapPacket(SessionA, (0, 1, 1, 90_000), (1, 2, 1, 91_000), (2, 3, 1, 92_000)));

        var snapshot = store.BuildSnapshot();
        var h2h = snapshot.H2h!;
        // One entry per pair, NOT per car: 3 duels, and car 0's tally against car 1 is
        // exactly 1 lap win — its extra win over car 2 must not leak into that duel.
        Assert.Equal(3, h2h.Count);
        var vs1 = Tally(snapshot, 0, 1)!;
        Assert.Equal((1, 1), For(vs1, 0));
        Assert.Equal((0, 0), For(vs1, 1));
        Assert.Equal((1, 1), For(Tally(snapshot, 0, 2)!, 0));
        Assert.Equal((1, 1), For(Tally(snapshot, 1, 2)!, 1));
        Assert.Equal((0, 0), For(Tally(snapshot, 1, 2)!, 2));
    }

    [Fact]
    public void Snapshot_h2h_is_null_before_any_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        Assert.Null(store.BuildSnapshot().H2h);
    }

    [Fact]
    public void No_throw_when_only_one_car_completes_laps()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        for (byte lap = 1; lap <= 4; lap++)
        {
            store.Apply(LapPacket(SessionA, (0, 1, lap, 90_000)));
        }

        // No opponent: no tally, no exception.
        Assert.Null(store.BuildSnapshot().H2h);
    }

    [Fact]
    public void Forward_lap_jump_on_first_packet_does_not_score_a_phantom_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));

        // Both cars joined mid-race: the first packet reports lap 15 with carried-over
        // last-lap times — a reseed, not a completed lap.
        var joined = store.Apply(LapPacket(SessionA, (0, 1, 15, 90_000), (1, 2, 15, 91_000)));
        Assert.Empty(joined.OfType<LapCompleted>());
        var snapshot = store.BuildSnapshot();
        Assert.Null(snapshot.H2h); // no phantom tally entry
        Assert.Equal(0u, snapshot.Standings.Single(r => r.CarIndex == 0).BestLapTimeMs);

        // The next normal crossing (15 → 16) scores the duel normally.
        store.Apply(LapPacket(SessionA, (0, 1, 16, 88_000), (1, 2, 16, 89_000)));

        var tally = Assert.Single(store.BuildSnapshot().H2h!);
        Assert.Equal((byte)0, tally.CarIndex);
        Assert.Equal((byte)1, tally.OpponentIndex);
        Assert.Equal(1, tally.LapsWon);
        Assert.Equal(1, tally.PositionWins);
        Assert.Equal(0, tally.OpponentLapsWon);
        Assert.Equal(0, tally.OpponentPositionWins);
    }

    [Fact]
    public void Session_change_resets_the_tally()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionA));
        store.Apply(LapPacket(SessionA, (0, 1, 1, 90_000), (1, 2, 1, 91_000)));
        Assert.NotNull(store.BuildSnapshot().H2h);

        store.Apply(SessionPacket(SessionB));
        store.Apply(LapPacket(SessionB, (0, 1, 1, 95_000), (1, 2, 1, 94_000)));

        var snapshot = store.BuildSnapshot();
        // Fresh counts only: car 1's lap win and car 0's position win are from session B.
        var tally = Assert.Single(snapshot.H2h!);
        Assert.Equal((byte)0, tally.CarIndex);
        Assert.Equal((byte)1, tally.OpponentIndex);
        Assert.Equal((0, 1), For(tally, 0));
        Assert.Equal((1, 0), For(tally, 1));
    }
}