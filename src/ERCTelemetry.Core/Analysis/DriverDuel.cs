namespace ERCTelemetry.Core.Analysis;

/// <summary>Head-to-head comparison of two drivers: lap wins by lap-number join (only
/// laps both completed, faster lap wins, ties count neither), sector wins per sector (ties
/// count neither, 0-time = not seen), Best/Ø/σ deltas (A − B, negative = A faster), stints
/// joined by stint index, and the trace duel (tips + corner losses) only when both drivers
/// have a stored trace.</summary>
public static class DriverDuel
{
    /// <summary>Compares driver A vs. B. Trace tips/corner losses stay empty when either
    /// trace is missing (traces exist for the player only).</summary>
    public static DuelReport Compare(
        PerDriverSummary a,
        PerDriverSummary b,
        IReadOnlyList<LapPoint> lapsA,
        IReadOnlyList<LapPoint> lapsB,
        LapTrace? traceA,
        LapTrace? traceB)
    {
        var byLapA = lapsA.Where(l => l.LapTimeMs > 0).ToDictionary(l => l.LapNumber);
        var byLapB = lapsB.Where(l => l.LapTimeMs > 0).ToDictionary(l => l.LapNumber);

        var pairs = new List<DuelLapPair>();
        var lapWinsA = 0;
        var lapWinsB = 0;
        foreach (var (lapNumber, lapA) in byLapA)
        {
            if (!byLapB.TryGetValue(lapNumber, out var lapB))
            {
                continue;
            }

            pairs.Add(new DuelLapPair(lapNumber, lapA.LapTimeMs, lapB.LapTimeMs));
            if (lapA.LapTimeMs < lapB.LapTimeMs)
            {
                lapWinsA++;
            }
            else if (lapB.LapTimeMs < lapA.LapTimeMs)
            {
                lapWinsB++;
            }
        }

        // Sector wins over the joined laps; ties and unseen sectors (0) count neither side.
        var s1a = 0; var s1b = 0;
        var s2a = 0; var s2b = 0;
        var s3a = 0; var s3b = 0;
        foreach (var pair in pairs)
        {
            if (!byLapA.TryGetValue(pair.LapNumber, out var la) ||
                !byLapB.TryGetValue(pair.LapNumber, out var lb))
            {
                continue;
            }

            if (la.S1Ms > 0 && lb.S1Ms > 0 && la.S1Ms != lb.S1Ms)
            {
                if (la.S1Ms < lb.S1Ms) s1a++; else s1b++;
            }

            if (la.S2Ms > 0 && lb.S2Ms > 0 && la.S2Ms != lb.S2Ms)
            {
                if (la.S2Ms < lb.S2Ms) s2a++; else s2b++;
            }

            if (la.S3Ms > 0 && lb.S3Ms > 0 && la.S3Ms != lb.S3Ms)
            {
                if (la.S3Ms < lb.S3Ms) s3a++; else s3b++;
            }
        }

        // Stints joined by stint index; the shorter stint list gets null padders.
        var maxStints = Math.Max(a.Stints.Count, b.Stints.Count);
        var stintPairs = new List<StintPair>();
        for (var i = 0; i < maxStints; i++)
        {
            var stintA = i < a.Stints.Count ? a.Stints[i] : null;
            var stintB = i < b.Stints.Count ? b.Stints[i] : null;
            if (stintA is null && stintB is null)
            {
                continue;
            }

            stintPairs.Add(new StintPair(i + 1, stintA, stintB));
        }

        // Trace duel: both traces must exist (traces are recorded for the player only).
        List<TraceTip>? tips = traceA is not null && traceB is not null
            ? [.. LapTraceComparer.Compare(traceA, traceB)]
            : [];
        List<CornerLoss>? losses = traceA is not null && traceB is not null
            ? [.. LapTraceComparer.RankCornerLosses(traceA, traceB)]
            : [];

        return new DuelReport(
            a, b,
            lapWinsA, lapWinsB,
            pairs,
            new SectorWinTally(s1a, s1b, s2a, s2b, s3a, s3b),
            (double)a.BestLapMs - b.BestLapMs,
            a.AverageLapMs - b.AverageLapMs,
            a.ConsistencySigmaMs - b.ConsistencySigmaMs,
            stintPairs,
            tips,
            losses);
    }
}