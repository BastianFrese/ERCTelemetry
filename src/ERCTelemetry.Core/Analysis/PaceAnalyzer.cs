using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>One tyre stint of a driver: span, pace, degradation and consistency,
/// computed from the stored <see cref="LapCompleted"/> rows (headless-testable).</summary>
public sealed record StintSummary(
    int StintNumber,
    string Tyre,               // short compound label ("C3", "INT", …)
    byte AgeAtStart,           // tyre age in laps when the stint began
    int StartLap,
    int EndLap,
    int Laps,                  // racing laps counted (0-time and outlier laps excluded)
    uint BestLapMs,
    double AverageLapMs,
    double DegradationMsPerLap,
    double ConsistencySigmaMs);

/// <summary>Pace picture of one driver across a whole session.</summary>
public sealed record PaceReport(
    IReadOnlyList<StintSummary> Stints,
    uint BestLapMs,
    double AverageLapMs,
    double ConsistencySigmaMs);

/// <summary>Cross-driver consistency picture of one session: one row per driver with
/// stored laps. Score = 100·(1 − σ/avg) over racing laps; NaN when the driver has
/// fewer than two racing laps (no meaningful spread yet).</summary>
public sealed record ConsistencyRow(
    byte CarIndex,
    string DriverName,
    int RacingLaps,
    uint BestLapMs,
    double AverageLapMs,
    double ConsistencySigmaMs,
    double Score);

/// <summary>Derives race-pace analytics (stints, degradation slope, consistency σ)
/// from stored laps. Pure functions over <see cref="LapCompleted"/> — no state.</summary>
public static class PaceAnalyzer
{
    /// <summary>Laps slower than this factor over the stint median are outliers
    /// (in-laps, out-laps, fuel-saving, traffic) and excluded from slope/σ.</summary>
    private const double OutlierFactor = 1.05;

    public static PaceReport Analyze(IReadOnlyList<LapCompleted> allLaps, byte carIndex)
    {
        var laps = allLaps
            .Where(l => l.CarIndex == carIndex && l.LapTimeMs > 0)
            .OrderBy(l => l.LapNumber)
            .ToList();

        var stints = new List<StintSummary>();
        var stint = new List<LapCompleted>();
        var stintStart = 0;
        var ageAtStart = (byte)0;

        foreach (var lap in laps)
        {
            // A stint ends when the compound changes or the tyre age jumps backwards
            // (= a fresh/other set was fitted mid-session).
            if (stint.Count > 0 &&
                lap.TyreCompound == stint[^1].TyreCompound &&
                lap.TyreAgeLaps >= stint[^1].TyreAgeLaps)
            {
                stint.Add(lap);
                continue;
            }

            if (stint.Count > 0)
            {
                stints.Add(BuildStint(stints.Count + 1, stint, stintStart, ageAtStart));
            }

            stint = [lap];
            stintStart = lap.LapNumber;
            ageAtStart = lap.TyreAgeLaps;
        }

        if (stint.Count > 0)
        {
            stints.Add(BuildStint(stints.Count + 1, stint, stintStart, ageAtStart));
        }

        // Overall numbers exclude outlier laps so in/out-laps don't pollute the average.
        var median = MedianOf(laps.Select(l => (double)l.LapTimeMs));
        var racing = laps.Where(l => !IsOutlier(l.LapTimeMs, median)).ToList();

        return new PaceReport(
            stints,
            laps.Count > 0 ? laps.Min(l => l.LapTimeMs) : 0u,
            racing.Count > 0 ? racing.Average(l => (double)l.LapTimeMs) : 0.0,
            Sigma(racing.Select(l => (double)l.LapTimeMs)));
    }

    /// <summary>Consistency comparison of ALL drivers in a session, sorted by best lap.
    /// Uses the same outlier rule as the per-driver report: laps over 5 % above the
    /// driver's median (in-laps, out-laps, traffic) are excluded from average and σ.</summary>
    public static IReadOnlyList<ConsistencyRow> AnalyzeConsistency(IReadOnlyList<LapCompleted> allLaps)
    {
        return allLaps
            .Where(l => l.LapTimeMs > 0)
            .GroupBy(l => l.CarIndex)
            .Select(group =>
            {
                var laps = group.OrderBy(l => l.LapNumber).ToList();
                var median = MedianOf(laps.Select(l => (double)l.LapTimeMs));
                var racing = laps.Where(l => !IsOutlier(l.LapTimeMs, median)).ToList();
                var times = racing.Select(l => (double)l.LapTimeMs).ToList();
                var average = times.Count > 0 ? times.Average() : 0.0;
                var sigma = Sigma(times);
                var score = times.Count >= 2 && average > 0
                    ? Math.Clamp(100.0 * (1.0 - sigma / average), 0.0, 100.0)
                    : double.NaN;
                return new ConsistencyRow(
                    group.Key,
                    laps[0].DriverName,
                    times.Count,
                    laps.Min(l => l.LapTimeMs),
                    average,
                    sigma,
                    score);
            })
            .OrderBy(row => row.BestLapMs == 0 ? uint.MaxValue : row.BestLapMs)
            .ToList();
    }

    private static StintSummary BuildStint(
        int number, IReadOnlyList<LapCompleted> laps, int startLap, byte ageAtStart)
    {
        var median = MedianOf(laps.Select(l => (double)l.LapTimeMs));
        var racing = laps.Where(l => !IsOutlier(l.LapTimeMs, median)).ToList();
        var times = racing.Select(l => (double)l.LapTimeMs).ToList();

        return new StintSummary(
            number,
            TyreLabel(laps[0].TyreCompound),
            ageAtStart,
            startLap,
            (int)laps.Max(l => l.LapNumber),
            racing.Count,
            racing.Count > 0 ? racing.Min(l => l.LapTimeMs) : (uint)0,
            times.Count > 0 ? times.Average() : 0.0,
            SlopePerLap(racing),
            times.Count > 0 ? Sigma(times) : 0.0);
    }

    /// <summary>Least-squares slope of lap time over lap number (ms per lap). Positive =
    /// the tyres get slower — the degradation rate of the stint.</summary>
    private static double SlopePerLap(IReadOnlyList<LapCompleted> laps)
    {
        if (laps.Count < 2)
        {
            return 0.0;
        }

        var meanX = laps.Average(l => (double)l.LapNumber);
        var meanY = laps.Average(l => (double)l.LapTimeMs);
        double sxx = 0, sxy = 0;
        foreach (var lap in laps)
        {
            var dx = lap.LapNumber - meanX;
            sxx += dx * dx;
            sxy += dx * (lap.LapTimeMs - meanY);
        }

        return sxx == 0 ? 0.0 : sxy / sxx;
    }

    /// <summary>Sample standard deviation (σ) of the given lap times; 0 with <2 samples.</summary>
    public static double Sigma(IEnumerable<double> values)
    {
        var list = values as IReadOnlyList<double> ?? values.ToList();
        if (list.Count < 2)
        {
            return 0.0;
        }

        var mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }

    private static bool IsOutlier(uint lapMs, double median) =>
        median > 0 && lapMs > median * OutlierFactor;

    private static double MedianOf(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0
            ? 0.0
            : sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    private static string TyreLabel(ActualCompound compound) => compound switch
    {
        ActualCompound.F1Wet => "WET",
        ActualCompound.F1Inter => "INT",
        _ when compound.ToString().StartsWith("F1C", StringComparison.Ordinal)
            => "C" + compound.ToString()[3..],
        _ => compound.ToString(),
    };
}