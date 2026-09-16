using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Tests.Analysis;

using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using Xunit;

/// <summary>Tests for the 360° radar geometry: player-facing transform, radius
/// filtering, side classification, and the standings gap chain.</summary>
public sealed class BlindSpotCalculatorTests
{
    private const byte Player = 5;

    [Fact]
    public void Compute_transforms_world_positions_into_player_view()
    // Player at origin facing +Z: in this game's world the driver's right is -X
    // (up × forward = +X is the LEFT — using it mirrored the radar). Car behind,
    // world-X +5 -> left, long -30, lat -5. Car ahead, world-X -3 -> right,
    // long +5, lat +3.
    {
        var frame = Frame(0f, 1f);
        frame.X[3] = 5f; frame.Z[3] = -30f;
        frame.X[7] = -3f; frame.Z[7] = 5f;

        var result = BlindSpotCalculator.Compute(frame, Player, []);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result[0].CarIndex);
        Assert.True(result[0].IsLeft);
        Assert.Equal(-30f, result[0].LongitudinalMetres, 3);
        Assert.Equal(-5f, result[0].LateralMetres, 3);
        Assert.Equal(7, result[1].CarIndex);
        Assert.False(result[1].IsLeft);
        Assert.Equal(5f, result[1].LongitudinalMetres, 3);
        Assert.Equal(3f, result[1].LateralMetres, 3);
    }

    [Fact]
    public void Compute_keeps_cars_within_the_radar_radius()
    // 200 m radius in every direction: 50 m behind + 9 m lateral (√(50²+9²) ≈ 50.8 m)
    // is inside; 250 m ahead and 250 m behind are outside.
    {
        var frame = Frame(0f, 1f);
        frame.X[3] = 9f; frame.Z[3] = -50f;
        frame.X[4] = 0f; frame.Z[4] = 250f;
        frame.X[6] = 0f; frame.Z[6] = -250f;

        var result = BlindSpotCalculator.Compute(frame, Player, []);

        Assert.Single(result);
        Assert.Equal(3, result[0].CarIndex);
    }

    [Fact]
    public void Compute_skips_player_and_unreported_zero_slots()
    {
        var frame = Frame(0f, 1f);
        frame.X[3] = 5f; frame.Z[3] = -30f;

        var result = BlindSpotCalculator.Compute(frame, Player, []);

        Assert.Single(result);
        Assert.Equal(3, result[0].CarIndex);
    }

    [Fact]
    public void Compute_returns_empty_for_degenerate_player_heading()
    {
        var frame = Frame(0f, 0f);
        frame.X[3] = 5f; frame.Z[3] = -30f;

        var result = BlindSpotCalculator.Compute(frame, Player, []);

        Assert.Empty(result);
    }

    [Fact]
    public void Compute_returns_empty_without_positions_or_bad_player_index()
    {
        Assert.Empty(BlindSpotCalculator.Compute(null, Player, []));
        var frame = Frame(0f, 1f, count: 4);
        Assert.Empty(BlindSpotCalculator.Compute(frame, 200, []));
    }

    [Fact]
    public void Compute_attaches_gap_chain_from_standings()
    // pos2 (car 3) reports 500 ms to the player, pos3 (car 7) +300 more -> 800 ms.
    // Car 9 in the cone but not reachable through known gaps -> 0 (unknown).
    {
        var frame = Frame(0f, 1f);
        frame.X[3] = 5f; frame.Z[3] = -30f;
        frame.X[7] = -3f; frame.Z[7] = 5f;
        frame.X[9] = 1f; frame.Z[9] = -40f;
        var standings = new List<StandingsRow>
        {
            Row(1, 5, 0),
            Row(2, 3, 500),
            Row(3, 7, 300),
        };

        var result = BlindSpotCalculator.Compute(frame, Player, standings);

        Assert.Equal(500, Single(result, 3).GapMs);
        Assert.Equal(800, Single(result, 7).GapMs);
        Assert.Equal(0, Single(result, 9).GapMs);
    }

    [Fact]
    public void Gap_chain_breaks_on_unknown_gap_link()
    // pos2 reports 500, pos3 reports 0 (unknown) — cars behind that link get nothing.
    {
        var standings = new List<StandingsRow>
        {
            Row(1, 5, 0),
            Row(2, 3, 500),
            Row(3, 7, 0),
            Row(4, 9, 400),
        };

        var gaps = BlindSpotCalculator.GapToPlayerByCar(standings, Player);

        Assert.Equal(500, gaps[3]);
        Assert.False(gaps.ContainsKey((byte)7));
        Assert.False(gaps.ContainsKey((byte)9));
    }

    [Fact]
    public void Gap_chain_returns_empty_when_player_not_in_standings()
    {
        var gaps = BlindSpotCalculator.GapToPlayerByCar([Row(1, 2, 0)], Player);

        Assert.Empty(gaps);
    }

    private static BlindSpotCar Single(IReadOnlyList<BlindSpotCar> list, byte carIndex) =>
        list.Single(c => c.CarIndex == carIndex);

    /// <summary>Empty world grid with only the player's heading filled.</summary>
    private static MotionFrame Frame(float fwdX, float fwdZ, byte count = 22)
    {
        var x = new float[count];
        var z = new float[count];
        var fx = new float[count];
        var fz = new float[count];
        if (Player < count)
        {
            fx[Player] = fwdX;
            fz[Player] = fwdZ;
        }

        return new MotionFrame(count, x, z, fx, fz);
    }

    private static StandingsRow Row(byte position, byte carIndex, int gapToFront) =>
        new(position, carIndex, $"Driver {carIndex}", Team.McLaren, 10, 12,
            90_000, 89_000, 0, gapToFront, PitStatus.None, 0,
            ResultStatus.Active, ActualCompound.F1C3, 5, carIndex == Player);
}
