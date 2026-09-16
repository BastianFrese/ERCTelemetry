using ERCTelemetry.Core.Analysis;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Adjacent position-swap detection: single swap, both directions, multi-swap
/// packets, and the guards (position 0 slots, multi-position jumps, unchanged standings).</summary>
public sealed class OvertakeDetectorTests
{
    [Fact]
    public void FindSwaps_detects_adjacent_swap()
    {
        Span<byte> before = [3, 1, 2]; // car 0 P3, car 1 P1, car 2 P2
        Span<byte> after = [3, 2, 1];

        var swaps = OvertakeDetector.FindSwaps(before, after);

        var swap = Assert.Single(swaps);
        Assert.Equal(2, swap.MoverIndex);  // car 2: P2 → P1
        Assert.Equal(1, swap.VictimIndex); // car 1: P1 → P2
        Assert.Equal(1, swap.NewPosition);
    }

    [Fact]
    public void FindSwaps_finds_every_swap_in_a_multi_swap_packet()
    {
        // Positions 3↔4 and 6↔7 swap simultaneously (independent track sections).
        Span<byte> before = [1, 2, 3, 4, 5, 6, 7, 8];
        Span<byte> after = [1, 2, 4, 3, 5, 7, 6, 8];

        var swaps = OvertakeDetector.FindSwaps(before, after);

        Assert.Equal(2, swaps.Count);
        Assert.Contains(swaps, s => s.MoverIndex == 3 && s.VictimIndex == 2 && s.NewPosition == 3);
        Assert.Contains(swaps, s => s.MoverIndex == 6 && s.VictimIndex == 5 && s.NewPosition == 6);
    }

    [Fact]
    public void FindSwaps_ignores_multi_position_jumps_and_zero_slots()
    {
        // car 0 jumps P6 → P2 (retirement cascade), car 7 joins late with position 1
        // straight from an unseen (0) slot.
        Span<byte> before = [6, 2, 1, 3, 4, 5, 0, 0];
        Span<byte> after = [2, 1, 3, 4, 5, 7, 6, 1];

        Assert.Empty(OvertakeDetector.FindSwaps(before, after));
    }

    [Fact]
    public void FindSwaps_returns_empty_for_unchanged_standings()
    {
        Span<byte> before = [1, 2, 3];
        Span<byte> after = [1, 2, 3];

        Assert.Empty(OvertakeDetector.FindSwaps(before, after));
    }

    [Fact]
    public void FindSwaps_fires_only_for_adjacent_pairs_in_a_reversal()
    {
        // Positions reversed wholesale: 1↔4 is a multi-position jump (ignored), but
        // 2↔3 is a genuine adjacent swap and still fires.
        Span<byte> before = [1, 2, 3, 4];
        Span<byte> after = [4, 3, 2, 1];

        var swaps = OvertakeDetector.FindSwaps(before, after);

        var swap = Assert.Single(swaps);
        Assert.Equal(2, swap.MoverIndex);
        Assert.Equal(1, swap.VictimIndex);
        Assert.Equal(2, swap.NewPosition);
    }
}