namespace ERCTelemetry.Core.Analysis;

/// <summary>One coaching tip from comparing two lap speed traces. <see cref="Kind"/> is
/// one of "loss"/"gain" (time over distance), "brake" (brake-point offset) or "apex"
/// (corner-exit speed difference) — the UI colors rows by it.</summary>
public sealed record TraceTip(string Kind, string Text);

/// <summary>One corner's incremental time loss from comparing two laps ("Timekiller").
/// <see cref="LossSeconds"/> is the extra time lap <c>A</c> spent inside the corner's
/// window vs. lap <c>B</c>; <see cref="AtPercent"/> anchors it on the track.</summary>
public sealed record CornerLoss(byte CornerNumber, float LossSeconds, float AtPercent, float OnsetDistance);

/// <summary>Compares two laps' speed traces: where lap A lost or gained time against
/// lap B (time-over-distance trapezoid integration) and how matched braking zones
/// differ (brake-point offset, apex speed). Tips are ordered most-significant first:
/// time tips, then brake tips, then apex tips. Thresholds are tuned for the recorder's
/// 20 m distance sampling.</summary>
public static class LapTraceComparer
{
    internal const int MinSamples = 8;
    internal const float MinOverlapMetres = 100f;
    internal const float MinBrakeDropKmh = 30f;
    internal const float MinBrakePointDeltaMetres = 15f;
    internal const float MinApexDeltaKmh = 5f;
    internal const float ZoneMatchMetres = 60f;
    internal const float MinSpeedKmh = 5f;
    internal const float MinTipDeltaSeconds = 0.05f;
    internal const int MaxCorners = 5;

    /// <summary>One point of the integrated time-over-distance curve.</summary>
    internal sealed record CurvePoint(float Distance, float TimeSeconds, float SpeedKmh);

    /// <summary>A braking zone: onset at the last speed peak before deceleration,
    /// apex at the zone's slowest point.</summary>
    internal sealed record BrakeZone(float OnsetDistance, float ApexDistance, float PeakSpeedKmh, float ApexSpeedKmh);

    public static IReadOnlyList<TraceTip> Compare(LapTrace a, LapTrace b)
    {
        var curveA = BuildTimeCurve(a.Samples);
        var curveB = BuildTimeCurve(b.Samples);
        if (curveA.Count < MinSamples || curveB.Count < MinSamples)
        {
            return [];
        }

        var windowStart = Math.Max(curveA[0].Distance, curveB[0].Distance);
        var windowEnd = Math.Min(curveA[^1].Distance, curveB[^1].Distance);
        var refLength = a.TrackLength > 0
            ? a.TrackLength
            : Math.Max(curveA[^1].Distance, curveB[^1].Distance);

        var tips = new List<TraceTip>();
        if (windowEnd - windowStart >= MinOverlapMetres)
        {
            AddTimeTips(a, b, curveA, curveB, windowStart, windowEnd, refLength, tips);
        }

        AddBrakeTips(a, b, curveA, curveB, refLength, tips);
        return tips;
    }

    /// <summary>Cumulative time-to-distance curve. Samples must advance in distance —
    /// replays/flashbacks can leave non-monotone samples in a stored trace, those are
    /// skipped. Speeds are clamped to ≥5 km/h so near-standstill doesn't explode dt.</summary>
    internal static List<CurvePoint> BuildTimeCurve(IReadOnlyList<LapTraceSample> samples)
    {
        var curve = new List<CurvePoint>(samples.Count);
        var time = 0f;
        var lastDistance = float.NegativeInfinity;
        foreach (var sample in samples)
        {
            if (sample.LapDistance <= lastDistance || !float.IsFinite(sample.LapDistance))
            {
                continue;
            }

            if (curve.Count > 0)
            {
                var v1 = Math.Max(curve[^1].SpeedKmh, MinSpeedKmh);
                var v2 = Math.Max((float)sample.Speed, MinSpeedKmh);
                time += 3.6f * 2f * (sample.LapDistance - lastDistance) / (v1 + v2);
            }

            lastDistance = sample.LapDistance;
            curve.Add(new CurvePoint(sample.LapDistance, time, sample.Speed));
        }

        return curve;
    }

    /// <summary>Interpolated curve time at an arbitrary distance (linear between the
    /// surrounding samples).</summary>
    internal static float TimeAt(List<CurvePoint> curve, float distance)
    {
        if (distance <= curve[0].Distance)
        {
            return curve[0].TimeSeconds;
        }

        for (var i = 1; i < curve.Count; i++)
        {
            if (curve[i].Distance >= distance)
            {
                var p0 = curve[i - 1];
                var p1 = curve[i];
                var span = p1.Distance - p0.Distance;
                var t = span > 0f ? (distance - p0.Distance) / span : 0f;
                return p0.TimeSeconds + (p1.TimeSeconds - p0.TimeSeconds) * t;
            }
        }

        return curve[^1].TimeSeconds;
    }

    /// <summary>Biggest sustained loss/gain of lap A vs. lap B inside the overlap window.
    /// The window's final crossing segment is excluded — it accumulates the whole lap's
    /// time difference and would always mask the local extremes.</summary>
    private static void AddTimeTips(LapTrace a, LapTrace b, List<CurvePoint> curveA, List<CurvePoint> curveB,
        float windowStart, float windowEnd, float refLength, List<TraceTip> tips)
    {
        var offsetA = TimeAt(curveA, windowStart);
        var offsetB = TimeAt(curveB, windowStart);
        var excludeAfter = windowEnd - 20f;
        var worstLoss = 0f;
        var worstGain = 0f;
        var lossPos = 0f;
        var gainPos = 0f;

        foreach (var point in curveA)
        {
            if (point.Distance <= windowStart + 1f || point.Distance >= excludeAfter)
            {
                continue;
            }

            var delta = (point.TimeSeconds - offsetA) - (TimeAt(curveB, point.Distance) - offsetB);
            if (delta > worstLoss)
            {
                worstLoss = delta;
                lossPos = point.Distance;
            }

            if (delta < worstGain)
            {
                worstGain = delta;
                gainPos = point.Distance;
            }
        }

        if (worstLoss > MinTipDeltaSeconds)
        {
            tips.Add(new TraceTip("loss",
                $"Runde {a.LapNumber} verliert bei {Percent(lossPos, refLength):0}% der Runde: " +
                $"+{worstLoss:0.0}s ggü. Runde {b.LapNumber}"));
        }

        if (worstGain < -MinTipDeltaSeconds)
        {
            tips.Add(new TraceTip("gain",
                $"Runde {a.LapNumber} gewinnt bei {Percent(gainPos, refLength):0}% der Runde: " +
                $"−{-worstGain:0.0}s ggü. Runde {b.LapNumber}"));
        }
    }

    /// <summary>Matches braking zones of both laps by onset (±60 m) and reports brake
    /// point offsets ≥15 m and apex speed differences ≥5 km/h.</summary>
    private static void AddBrakeTips(LapTrace a, LapTrace b, List<CurvePoint> curveA, List<CurvePoint> curveB,
        float refLength, List<TraceTip> tips)
    {
        var zonesA = FindBrakeZones(curveA);
        var zonesB = FindBrakeZones(curveB);
        if (zonesA.Count == 0 || zonesB.Count == 0)
        {
            return;
        }

        var brakeTips = new List<(float Magnitude, TraceTip Tip)>();
        var used = new HashSet<int>();
        foreach (var zoneA in zonesA)
        {
            var best = -1;
            var bestGap = ZoneMatchMetres;
            for (var j = 0; j < zonesB.Count; j++)
            {
                if (used.Contains(j))
                {
                    continue;
                }

                var gap = Math.Abs(zoneA.OnsetDistance - zonesB[j].OnsetDistance);
                if (gap <= bestGap)
                {
                    bestGap = gap;
                    best = j;
                }
            }

            if (best < 0)
            {
                continue;
            }

            used.Add(best);
            var zoneB = zonesB[best];
            CollectBrakeTip(a, b, zoneA, zoneB, refLength, brakeTips);
            CollectApexTip(a, b, zoneA, zoneB, refLength, brakeTips);
        }

        brakeTips.Sort((x, y) => y.Magnitude.CompareTo(x.Magnitude));
        tips.AddRange(brakeTips.Select(entry => entry.Tip));
    }

    private static void CollectBrakeTip(LapTrace a, LapTrace b, BrakeZone zoneA, BrakeZone zoneB,
        float refLength, List<(float, TraceTip)> tips)
    {
        var delta = zoneA.OnsetDistance - zoneB.OnsetDistance;
        if (Math.Abs(delta) < MinBrakePointDeltaMetres)
        {
            return;
        }

        var word = delta > 0 ? "später" : "früher";
        tips.Add((Math.Abs(delta), new TraceTip("brake",
            $"Bremspunkt bei {Percent(zoneA.OnsetDistance, refLength):0}%: " +
            $"Runde {a.LapNumber} bremst {Math.Abs(delta):0} m {word} als Runde {b.LapNumber}")));
    }

    private static void CollectApexTip(LapTrace a, LapTrace b, BrakeZone zoneA, BrakeZone zoneB,
        float refLength, List<(float, TraceTip)> tips)
    {
        var delta = zoneA.ApexSpeedKmh - zoneB.ApexSpeedKmh;
        if (Math.Abs(delta) < MinApexDeltaKmh)
        {
            return;
        }

        var word = delta < 0 ? "langsamer" : "schneller";
        tips.Add((Math.Abs(delta), new TraceTip("apex",
            $"Apex bei {Percent(zoneA.ApexDistance, refLength):0}%: " +
            $"Runde {a.LapNumber} ist {Math.Abs(delta):0} km/h {word} als Runde {b.LapNumber}")));
    }

    /// <summary>Braking zones from the speed curve: a local speed maximum followed by a
    /// ≥30 km/h drop forms a zone; the zone ends where the speed stops falling.</summary>
    internal static List<BrakeZone> FindBrakeZones(List<CurvePoint> curve)
    {
        var zones = new List<BrakeZone>();
        var i = 1;
        while (i < curve.Count - 1)
        {
            var isPeak = curve[i].SpeedKmh >= curve[i - 1].SpeedKmh && curve[i].SpeedKmh > curve[i + 1].SpeedKmh;
            if (!isPeak)
            {
                i++;
                continue;
            }

            var trough = i + 1;
            while (trough + 1 < curve.Count && curve[trough + 1].SpeedKmh <= curve[trough].SpeedKmh)
            {
                trough++;
            }

            if (curve[i].SpeedKmh - curve[trough].SpeedKmh >= MinBrakeDropKmh)
            {
                zones.Add(new BrakeZone(curve[i].Distance, curve[trough].Distance, curve[i].SpeedKmh, curve[trough].SpeedKmh));
                i = trough + 1;
            }
            else
            {
                i++;
            }
        }

        return zones;
    }

    /// <summary>Timekiller: ranks the corners of a lap pair by incremental time loss.
    /// The reference lap's (b) brake zones define the corner windows — each segment runs
    /// from one brake onset to the next, so knock-on effects stay inside their window.
    /// Losses below <see cref="MinTipDeltaSeconds"/> are dropped; result is sorted
    /// most-significant first, capped at <see cref="MaxCorners"/> entries. Unlike the
    /// cumulative time tips, no final-window exclusion — corner windows are bounded
    /// by the next brake onset, so the last corner keeps its full window.</summary>
    public static IReadOnlyList<CornerLoss> RankCornerLosses(LapTrace a, LapTrace b)
    {
        var curveA = BuildTimeCurve(a.Samples);
        var curveB = BuildTimeCurve(b.Samples);
        if (curveA.Count < MinSamples || curveB.Count < MinSamples)
        {
            return [];
        }

        var windowStart = Math.Max(curveA[0].Distance, curveB[0].Distance);
        var windowEnd = Math.Min(curveA[^1].Distance, curveB[^1].Distance);
        var refLength = a.TrackLength > 0
            ? a.TrackLength
            : Math.Max(curveA[^1].Distance, curveB[^1].Distance);
        if (windowEnd - windowStart < MinOverlapMetres)
        {
            return [];
        }

        // Zones on the reference lap define the corner windows.
        var zones = FindBrakeZones(curveB);
        if (zones.Count == 0)
        {
            return [];
        }

        var offsetA = TimeAt(curveA, windowStart);
        var offsetB = TimeAt(curveB, windowStart);
        var losses = new List<(float Loss, CornerLoss Entry)>();
        for (var i = 0; i < zones.Count; i++)
        {
            var start = Math.Max(zones[i].OnsetDistance, windowStart);
            // Segments are bounded by the next onset; the last corner's window runs to
            // the full window end (a final-crossing exclusion here would eat it).
            var end = i + 1 < zones.Count
                ? Math.Min(zones[i + 1].OnsetDistance, windowEnd)
                : windowEnd;
            if (end - start < MinOverlapMetres / 2)
            {
                continue; // degenerate segment (clamped into the window)
            }

            var delta = ((TimeAt(curveA, end) - offsetA) - (TimeAt(curveB, end) - offsetB))
                      - ((TimeAt(curveA, start) - offsetA) - (TimeAt(curveB, start) - offsetB));
            if (delta < MinTipDeltaSeconds)
            {
                continue;
            }

            losses.Add((delta, new CornerLoss(
                (byte)(i + 1),
                delta,
                Percent(zones[i].OnsetDistance, refLength),
                zones[i].OnsetDistance)));
        }

        return losses
            .OrderByDescending(entry => entry.Loss)
            .Take(MaxCorners)
            .Select(entry => entry.Entry)
            .ToList();
    }

    private static float Percent(float distance, float refLength) =>
        refLength > 0f ? Math.Clamp(distance / refLength * 100f, 0f, 100f) : 0f;
}