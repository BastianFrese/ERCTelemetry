using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Builds the immutable <see cref="RaceReport"/> for one stored session by
/// pure projection over the telemetry database's read APIs — headless-testable and the
/// single data source for the in-app report tab and the HTML export. A session id the DB
/// doesn't know yields an empty report (no exception, Header null).</summary>
public static class RaceReportBuilder
{
    public static RaceReport Build(TelemetryDb db, long sessionId, byte? carA = null, byte? carB = null)
    {
        var session = db.GetSession(sessionId);
        if (session is null)
        {
            return new RaceReport(null, [], null, [], [], [], null, [], [], null, []);
        }

        var lapRows = db.GetLapRows(sessionId);
        var results = db.GetResults(sessionId);
        var driverNames = db.GetDriverNames(sessionId);
        // Timeline is scoped to the player: the game broadcasts every car's events, but
        // the report answers "what happened to the player" (255 = no player → unfiltered).
        var playerCarIndex = session.PlayerCarIndex;
        var events = db.GetEvents(sessionId, playerCarIndex);
        var overtakes = db.GetOvertakes(sessionId, playerCarIndex);
        var damageLog = db.GetDamageLog(sessionId);
        var positions = db.GetLapPositions(sessionId);
        var traceRefs = db.GetLapTraceSummaries(sessionId);

        // Player's best traced lap + the two duel drivers' best traced laps.
        var tracedLaps = CollectTraces(db, sessionId, traceRefs, carA, carB);

        var byCar = lapRows
            .GroupBy(l => l.CarIndex)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.LapNumber).ToList());
        var resultsByCar = results.ToDictionary(r => r.CarIndex);
        var damageByCar = damageLog
            .GroupBy(d => d.CarIndex)
            .ToDictionary(g => g.Key, g => g.Last().Damage);

        var allCars = byCar.Keys
            .Union(resultsByCar.Keys)
            .OrderBy(c => resultsByCar.TryGetValue(c, out var res) ? res.Position : 99)
            .ToList();

        var summaries = new List<PerDriverSummary>();
        foreach (var car in allCars)
        {
            var result = resultsByCar.GetValueOrDefault(car);
            var laps = byCar.GetValueOrDefault(car) ?? [];
            var pace = PaceAnalyzer.Analyze(ToLapCompleted(laps, driverNames), car);
            var (s1, s2, s3) = SectorStatsOf(laps);

            var grid = result?.GridPosition ?? (ushort)0;
            var finalPos = result?.Position ?? (byte)0;
            var penalties = Math.Max(
                laps.Count > 0 ? laps.Max(l => l.PenaltiesSeconds) : 0,
                result?.PenaltiesTime ?? 0);

            summaries.Add(new PerDriverSummary(
                car,
                result?.Name ?? driverNames.GetValueOrDefault(car, $"Car {car + 1}"),
                result?.Team ?? default,
                result?.RaceNumber ?? 0,
                finalPos,
                result?.Points ?? 0f,
                result?.ResultStatus.ToString() ?? "",
                result?.ResultReason ?? "",
                grid,
                result?.NumPitStops ?? (byte)0,
                laps.Count(l => l.LapTimeMs > 0),
                BestLapOf(laps),
                pace.AverageLapMs,
                pace.ConsistencySigmaMs,
                s1, s2, s3,
                laps.Count(l => l.IsValid == 1),
                laps.Count(l => l.Position == 1),
                grid > 0 && finalPos > 0 ? grid - finalPos : 0,
                penalties,
                laps.Sum(l => l.FuelUsed),
                laps.Sum(l => l.ErsUsedJ) / 1_000_000.0,
                result?.TotalRaceTimeSeconds ?? 0.0,
                pace.Stints,
                damageByCar.GetValueOrDefault(car)));
        }

        var classification = summaries
            .OrderBy(s => s.Position > 0 ? s.Position : 99)
            .ThenBy(s => s.BestLapMs == 0 ? uint.MaxValue : s.BestLapMs)
            .ToList();

        var player = summaries.FirstOrDefault(s => s.CarIndex == session.PlayerCarIndex);

        var lapSeries = summaries
            // allCars unions result cars with lap cars, so a car with a result but no
            // lap rows (DNS/DQ) would KeyNotFoundException a direct byCar index.
            .Select(s => new LapPointSeries(s.CarIndex, s.Name,
                ToLapPoints(byCar.GetValueOrDefault(s.CarIndex) ?? [])))
            .ToList();

        return new RaceReport(
            HeaderOf(session, driverNames.Count, lapRows.Count),
            classification,
            player,
            PaceAnalyzer.AnalyzeConsistency(ToLapCompleted(byCar.Values.SelectMany(x => x).ToList(), driverNames)),
            lapSeries,
            positions
                .Where(kv => kv.Value.Length > 0)
                .Select(kv => new PositionChunk(kv.Key, kv.Value.Length, kv.Value))
                .ToList(),
            BuildDuel(db, sessionId, carA, carB, summaries, byCar),
            damageLog,
            BuildTimeline(events, overtakes),
            db.GetSetup(sessionId, session.PlayerCarIndex),
            tracedLaps);
    }

    /// <summary>Converts stored rows to the record type PaceAnalyzer consumes.</summary>
    private static List<LapCompleted> ToLapCompleted(
        IReadOnlyList<TelemetryDb.StoredLapRow> rows,
        IReadOnlyDictionary<byte, string> driverNames)
    {
        var list = new List<LapCompleted>(rows.Count);
        foreach (var row in rows)
        {
            list.Add(new LapCompleted(
                row.CarIndex,
                driverNames.GetValueOrDefault(row.CarIndex, $"Car {row.CarIndex + 1}"),
                (byte)Math.Clamp(row.LapNumber, 0, byte.MaxValue),
                row.LapTimeMs,
                (ushort)Math.Clamp(row.Sector1Ms, 0, ushort.MaxValue),
                (ushort)Math.Clamp(row.Sector2Ms, 0, ushort.MaxValue),
                CompoundOf(row.Tyre),
                row.TyreAge,
                row.Position,
                (float)row.ErsUsedJ));
        }

        return list;
    }

    private static ActualCompound CompoundOf(string tyre) =>
        Enum.TryParse<ActualCompound>(tyre, ignoreCase: false, out var compound)
            ? compound
            : (ActualCompound)0;

    private static (SectorStats, SectorStats, SectorStats) SectorStatsOf(
        List<TelemetryDb.StoredLapRow> laps)
    {
        var s1 = StatsOf(laps.Select(l => l.Sector1Ms));
        var s2 = StatsOf(laps.Select(l => l.Sector2Ms));
        var s3 = StatsOf(laps.Select(l => l.Sector3Ms));
        return (s1, s2, s3);
    }

    private static SectorStats StatsOf(IEnumerable<uint> values)
    {
        var times = values.Where(v => v > 0).ToList();
        return new SectorStats(
            times.Count > 0 ? times.Min() : 0u,
            times.Count > 0 ? times.Average(v => (double)v) : 0.0,
            PaceAnalyzer.Sigma(times.Select(v => (double)v)));
    }

    private static uint BestLapOf(List<TelemetryDb.StoredLapRow> laps) =>
        laps.Where(l => l.LapTimeMs > 0).Select(l => l.LapTimeMs).DefaultIfEmpty(0u).Min();

    /// <summary>Lap number of the driver's fastest completed lap, or null when they have
    /// none. Lap traces are keyed by lap number — this must be driven off the lap that
    /// holds the fastest time, never the time value itself.</summary>
    private static byte? FastestLapNumber(List<TelemetryDb.StoredLapRow> laps)
    {
        TelemetryDb.StoredLapRow? best = null;
        foreach (var lap in laps)
        {
            if (lap.LapTimeMs > 0 && (best is null || lap.LapTimeMs < best.LapTimeMs))
            {
                best = lap;
            }
        }

        return best is null ? null : (byte)Math.Clamp(best.LapNumber, 0, byte.MaxValue);
    }

    private static List<LapPoint> ToLapPoints(List<TelemetryDb.StoredLapRow> laps)
    {
        var points = new List<LapPoint>(laps.Count);
        foreach (var row in laps)
        {
            points.Add(new LapPoint(
                row.LapNumber,
                row.LapTimeMs,
                row.Sector1Ms,
                row.Sector2Ms,
                row.Sector3Ms,
                row.IsValid,
                row.Tyre,
                row.Position));
        }

        return points;
    }

    private static List<TimelineEntry> BuildTimeline(
        IReadOnlyList<RaceEventEntry> events,
        IReadOnlyList<TelemetryDb.StoredOvertake> overtakes)
    {
        // The store also emits a German race-control event for the player's own overtakes
        // ("OVERHAUL! … du hast … überholt" in the events table) AND the game can broadcast
        // the same overtake again as an English "X passed Y" event — both entries have
        // CarIndex = mover and the same lap. Keys are accumulated here so a duplicate is
        // skipped while a synthesized English "X passed Y" entry that mirrors an event is
        // suppressed below.
        var eventOvertakes = new HashSet<(byte Car, int Lap)>();

        var timeline = new List<TimelineEntry>(events.Count + overtakes.Count);
        foreach (var e in events)
        {
            if (e.Type == "Overtake" && e.CarIndex is not null
                && eventOvertakes.Add((e.CarIndex!.Value, e.LapNumber)) is false)
            {
                continue;
            }

            timeline.Add(new TimelineEntry(
                e.Utc, e.Type, e.CarIndex, e.SecondCarIndex, e.Text, e.LapNumber, e.DetailValue));
        }

        foreach (var o in overtakes)
        {
            if (eventOvertakes.Contains((o.CarIndex, o.LapNumber)))
            {
                continue;
            }

            timeline.Add(new TimelineEntry(
                o.Utc, "Overtake", o.CarIndex, o.PassedCarIndex,
                $"{o.DriverName} passed {o.PassedDriverName}", o.LapNumber, 0));
        }

        return timeline.OrderBy(t => t.Utc).ThenBy(t => t.LapNumber).ToList();
    }

    private static DuelReport? BuildDuel(
        TelemetryDb db,
        long sessionId,
        byte? carA,
        byte? carB,
        List<PerDriverSummary> summaries,
        Dictionary<byte, List<TelemetryDb.StoredLapRow>> byCar)
    {
        if (carA is not { } carIndexA || carB is not { } carIndexB || carIndexA == carIndexB)
        {
            return null;
        }

        var summaryA = summaries.FirstOrDefault(s => s.CarIndex == carIndexA);
        var summaryB = summaries.FirstOrDefault(s => s.CarIndex == carIndexB);
        if (summaryA is null || summaryB is null)
        {
            return null;
        }

        var lapsA = byCar.GetValueOrDefault(carIndexA) ?? [];
        var lapsB = byCar.GetValueOrDefault(carIndexB) ?? [];
        // Traces are keyed by LAP NUMBER — BestLapOf returns the time in ms and must
        // never be cast into a lap number (90 000 → 144, matching no stored trace, which
        // silently disables every driver-duel lap comparison).
        var bestLapA = FastestLapNumber(lapsA);
        var bestLapB = FastestLapNumber(lapsB);
        var traceA = bestLapA is { } lapA ? db.GetLapTrace(sessionId, carIndexA, lapA) : null;
        var traceB = bestLapB is { } lapB ? db.GetLapTrace(sessionId, carIndexB, lapB) : null;

        return DriverDuel.Compare(
            summaryA, summaryB,
            ToLapPoints(lapsA),
            ToLapPoints(lapsB),
            traceA,
            traceB);
    }

    /// <summary>Loads the player's best-lap trace plus both duel drivers' best-lap traces.</summary>
    private static List<TracedLap> CollectTraces(
        TelemetryDb db,
        long sessionId,
        IReadOnlyList<TelemetryDb.LapTraceSummary> traceRefs,
        byte? carA,
        byte? carB)
    {
        var traces = new List<TracedLap>();
        var cars = new List<byte>();
        if (traceRefs.Count > 0)
        {
            cars.Add(traceRefs[0].CarIndex);
        }

        if (carA is { } a && !cars.Contains(a))
        {
            cars.Add(a);
        }

        if (carB is { } b && !cars.Contains(b))
        {
            cars.Add(b);
        }

        foreach (var car in cars)
        {
            var best = traceRefs
                .Where(r => r.CarIndex == car)
                .OrderBy(r => r.LapTimeMs == 0 ? uint.MaxValue : r.LapTimeMs)
                .FirstOrDefault();
            if (best is null)
            {
                continue;
            }

            var trace = db.GetLapTrace(sessionId, car, best.LapNumber);
            if (trace is not null)
            {
                traces.Add(new TracedLap(
                    car,
                    db.GetDriverNames(sessionId).GetValueOrDefault(car, $"Car {car + 1}"),
                    trace));
            }
        }

        return traces;
    }

    private static ReportHeader HeaderOf(TelemetryDb.SessionRow session, int numDrivers, int numLapsStored)
    {
        return new ReportHeader(
            session.Id,
            session.SessionType,
            session.Track,
            session.TotalLaps,
            session.TrackLength,
            session.GameMode,
            session.Weather,
            session.IsNetworkGame,
            session.TrackTemp,
            session.AirTemp,
            session.TimeOfDay,
            session.Formula,
            session.PitSpeedLimit,
            session.NumDrsZones,
            session.RuleSet,
            session.SessionDuration,
            session.StartedUtc,
            session.EndReason,
            numDrivers,
            numLapsStored);
    }
}
