namespace ERCTelemetry.Core.Session;

/// <summary>Pure projection from per-car state arrays to the ordered leaderboard.</summary>
public static class StandingsCalculator
{
    /// <summary>Cars are ordered by the game's position field (authoritative for all
    /// session types); cars without timing data are skipped, positionless rows appended.
    /// <paramref name="sessionBestSectorMs"/> (S1/S2/S3, 0 = none seen) drives the
    /// green/purple sector marks.</summary>
    public static IReadOnlyList<StandingsRow> BuildStandings(
        IReadOnlyList<DriverEntry?> drivers,
        IReadOnlyList<CarTiming?> timings,
        IReadOnlyList<CarCondition?> conditions,
        IReadOnlyList<uint> bestLapMs,
        IReadOnlyList<ushort>? sessionBestSectorMs = null)
    {
        var rows = new List<StandingsRow>(TelemetryConstants.MaxCars);
        var tail = new List<StandingsRow>();
        var count = Math.Min(Math.Min(timings.Count, TelemetryConstants.MaxCars), drivers.Count);
        ushort sessionS1 = 0, sessionS2 = 0, sessionS3 = 0;
        if (sessionBestSectorMs is { Count: >= 3 } bests)
        {
            sessionS1 = bests[0];
            sessionS2 = bests[1];
            sessionS3 = bests[2];
        }

        for (byte i = 0; i < count; i++)
        {
            var timing = timings[i];
            if (timing is null)
            {
                continue;
            }

            var driver = drivers[i];
            var condition = i < conditions.Count ? conditions[i] : null;
            var s3 = DeriveSector3(timing);
            var bestLap = i < bestLapMs.Count ? bestLapMs[i] : 0u;
            var row = new StandingsRow(
                timing.Position,
                i,
                driver?.Name ?? $"Car {i + 1}",
                driver?.Team ?? default,
                driver?.RaceNumber ?? 0,
                timing.CurrentLapNum,
                timing.LastLapTimeMs,
                bestLap,
                timing.GapToLeaderMs,
                timing.GapToCarInFrontMs,
                timing.PitStatus,
                timing.Penalties,
                timing.ResultStatus,
                condition?.TyreCompound ?? default,
                condition?.TyreAgeLaps ?? 0,
                driver?.IsPlayer ?? false,
                timing.Sector1TimeMs,
                timing.Sector2TimeMs,
                s3,
                timing.GridPosition,
                timing.NumPitStops,
                TimingLapMs(timing),
                timing.LapValidity,
                timing.FuelUsedLastLap,
                timing.ErsUsedLastLapJ,
                MarkSector(timing.Sector1TimeMs, timing.BestSector1Ms, sessionS1),
                MarkSector(timing.Sector2TimeMs, timing.BestSector2Ms, sessionS2),
                MarkSector(s3, timing.BestSector3Ms, sessionS3),
                timing.BestSector1Ms,
                timing.BestSector2Ms,
                timing.BestSector3Ms,
                LapDeltaMs(timing),
                PredictedLapMs(timing, bestLap));

            if (timing.Position == 0)
            {
                tail.Add(row);
            }
            else
            {
                rows.Add(row);
            }
        }

        rows.Sort((a, b) => a.Position.CompareTo(b.Position));
        rows.AddRange(tail);
        return rows;
    }

    /// <summary>The packets carry no sector-3 time; it equals LastLap − S1 − S2 while S1/S2
    /// still belong to the last completed lap. Implausible values (0, negative, ≥60 s
    /// leftover) render as "unavailable".</summary>
    internal static ushort DeriveSector3(CarTiming timing) =>
        DeriveSector3(timing.LastLapTimeMs, timing.Sector1TimeMs, timing.Sector2TimeMs);

    internal static ushort DeriveSector3(uint lastLapMs, ushort s1Ms, ushort s2Ms)
    {
        if (lastLapMs == 0 || s1Ms == 0 || s2Ms == 0)
        {
            return (ushort)0;
        }

        var s3 = lastLapMs - s1Ms - s2Ms;
        return s3 > 0 && s3 < 60000 ? (ushort)s3 : (ushort)0;
    }

    /// <summary>Purple when the shown time beats-or-matches the session best (a driver's
    /// own session best IS the session best then), green when it matches only the
    /// driver's own best, none otherwise. 0 anywhere = never marked.</summary>
    private static SectorMark MarkSector(ushort shown, ushort ownBest, ushort sessionBest) =>
        shown == 0 || ownBest == 0 ? SectorMark.None
        : sessionBest > 0 && shown == sessionBest ? SectorMark.Purple
        : shown == ownBest ? SectorMark.Green
        : SectorMark.None;

    /// <summary>Keeps the smaller non-zero value. Returns the resulting best.</summary>
    internal static ushort TrackBest(ref ushort best, ushort candidate)
    {
        if (candidate > 0 && (best == 0 || candidate < best))
        {
            best = candidate;
        }

        return best;
    }

    /// <summary>Live lap delta against the driver's own best sectors, known from sector 2
    /// on: in S2 the completed S1 is compared, in S3 the completed S1+S2. 0 = unknown —
    /// in sector 1 there is no completed reference yet, and an exact 0.000 delta is the
    /// price of the sentinel.</summary>
    internal static int LapDeltaMs(CarTiming timing) =>
        timing.Sector switch
        {
            1 => Delta(timing.Sector1TimeMs, timing.BestSector1Ms),
            2 => Delta(
                timing.Sector1TimeMs + timing.Sector2TimeMs,
                timing.BestSector1Ms + timing.BestSector2Ms),
            _ => 0,
        };

    /// <summary>Where the current lap is heading: best lap plus the live sector delta —
    /// only meaningful while the delta itself is known (0 = unknown).</summary>
    internal static uint PredictedLapMs(CarTiming timing, uint bestLapMs)
    {
        var delta = LapDeltaMs(timing);
        return bestLapMs > 0 && delta != 0
            ? bestLapMs + (uint)Math.Max(0, delta)
            : 0u;
    }

    private static int Delta(int currentMs, int bestMs) =>
        currentMs > 0 && bestMs > 0 ? currentMs - bestMs : 0;

    /// <summary>Live current-lap clock: only meaningful for cars actively lapping — a
    /// pit stop or a finished/retired car would show an ever-growing stale clock.</summary>
    private static uint TimingLapMs(CarTiming t) => t.PitStatus == F1Game.UDP.Enums.PitStatus.None
        ? t.CurrentLapTimeMs
        : 0u;
}