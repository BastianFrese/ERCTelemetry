using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Persistence;

/// <summary>Compares two stored sessions driver-by-driver. Pure projection over stored
/// laps — no database access, fully testable.</summary>
public static class SessionComparer
{
    public sealed record BestTimes(
        uint LapMs,
        ushort S1Ms,
        ushort S2Ms,
        ushort S3Ms);

    /// <summary>Times 0 mean "not in this session"; deltas only meaningful when HasBoth.</summary>
    public sealed record DriverCompareRow(
        string Name,
        BestTimes A,
        BestTimes B,
        bool HasBoth);

    /// <summary>Driver key: the stored driver name (stable per person across sessions of
    /// the same lobby; car indices differ between sessions). One row per name.</summary>
    public static IReadOnlyList<DriverCompareRow> Compare(
        IReadOnlyList<LapCompleted> lapsA,
        IReadOnlyList<LapCompleted> lapsB,
        IReadOnlyDictionary<byte, string> namesA,
        IReadOnlyDictionary<byte, string> namesB)
    {
        var aggregates = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);
        Accumulate(lapsA, namesA, true, aggregates);
        Accumulate(lapsB, namesB, false, aggregates);

        return aggregates.Values
            .OrderByDescending(x => x.A[0] > 0 && x.B[0] > 0)
            .ThenBy(x => x.Name)
            .Select(a => new DriverCompareRow(
                a.Name,
                new BestTimes(a.A[0], (ushort)a.A[1], (ushort)a.A[2], (ushort)a.A[3]),
                new BestTimes(a.B[0], (ushort)a.B[1], (ushort)a.B[2], (ushort)a.B[3]),
                a.A[0] > 0 && a.B[0] > 0))
            .ToArray();
    }

    private sealed class Aggregate
    {
        public string Name = string.Empty;
        public readonly uint[] A = new uint[4];
        public readonly uint[] B = new uint[4];
    }

    private static void Accumulate(
        IReadOnlyList<LapCompleted> laps,
        IReadOnlyDictionary<byte, string> names,
        bool a,
        Dictionary<string, Aggregate> aggregates)
    {
        foreach (var lap in laps)
        {
            if (lap.LapTimeMs == 0)
            {
                continue;
            }

            var name = names.GetValueOrDefault(lap.CarIndex, $"Car {lap.CarIndex + 1}");
            if (!aggregates.TryGetValue(name, out var agg))
            {
                agg = new Aggregate { Name = name };
                aggregates[name] = agg;
            }

            var target = a ? agg.A : agg.B;
            if (target[0] == 0 || lap.LapTimeMs < target[0])
            {
                target[0] = lap.LapTimeMs;
            }

            if (lap.Sector1TimeMs > 0 && (target[1] == 0 || lap.Sector1TimeMs < target[1]))
            {
                target[1] = lap.Sector1TimeMs;
            }

            if (lap.Sector2TimeMs > 0 && (target[2] == 0 || lap.Sector2TimeMs < target[2]))
            {
                target[2] = lap.Sector2TimeMs;
            }

            // Per-lap derived S3 (same guard as live timing): only when S1+S2 belong to
            // the stored lap time; the best S3 is a per-lap minimum, not a sector sum.
            var rawS3 = lap.LapTimeMs - (uint)lap.Sector1TimeMs - (uint)lap.Sector2TimeMs;
            if (lap.Sector1TimeMs > 0 && lap.Sector2TimeMs > 0 &&
                rawS3 > 0 && rawS3 < 60000 && (target[3] == 0 || rawS3 < target[3]))
            {
                target[3] = (ushort)rawS3;
            }
        }
    }
}