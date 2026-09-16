namespace ERCTelemetry.Core.Analysis;

/// <summary>One adjacent-position swap: <see cref="MoverIndex"/> gained one position by
/// passing <see cref="VictimIndex"/> (who fell from the mover's old slot).</summary>
public sealed record OvertakeSwap(byte MoverIndex, byte VictimIndex, byte NewPosition);

/// <summary>Detects overtakes as exact adjacent two-car swaps between consecutive
/// standings snapshots. Restricting to +1/−1 pairs keeps pit-stop cascades and
/// retirements (which shift many positions at once) out of the overtake feed — those
/// are position changes, not passing manoeuvres. Pure functions, no state.</summary>
public static class OvertakeDetector
{
    /// <summary>Compares the position arrays of two consecutive LapData packets. A swap
    /// is mover i: before[i] → after[i] = before[i] − 1 combined with victim j:
    /// before[j] = before[i] − 1 → after[j] = before[i]. Position 0 marks unseen slots
    /// and never counts (race start populates the grid without an overtake).</summary>
    public static IReadOnlyList<OvertakeSwap> FindSwaps(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var swaps = new List<OvertakeSwap>();
        var count = Math.Min(before.Length, after.Length);
        for (var i = 0; i < count; i++)
        {
            var oldPos = before[i];
            if (oldPos == 0 || oldPos - after[i] != 1)
            {
                continue;
            }

            for (var j = 0; j < count; j++)
            {
                if (j == i || before[j] != oldPos - 1 || after[j] != oldPos)
                {
                    continue;
                }

                swaps.Add(new OvertakeSwap((byte)i, (byte)j, after[i]));
                break;
            }
        }

        return swaps;
    }
}