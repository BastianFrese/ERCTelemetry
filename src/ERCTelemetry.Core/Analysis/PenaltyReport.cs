using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Analysis;

/// <summary>One rendered incident row of the History rework's "Strafen &amp; Verwarnungen"
/// section. <see cref="Place"/> is the distance-stamped corner/sector/percent/km
/// description ("~Kurve 12 · Sektor 3 · 82 % · 4,1 km") or "—" for rows stored before
/// the stamp existed; <see cref="Kind"/> mirrors the event type ("Penalty" / "Warning"
/// / "CornerCuttingWarning") for the UI badge.</summary>
public sealed record PenaltyIncident(
    RaceEventEntry Event,
    string Kind,     // "Penalty" | "Warning" | "CornerCuttingWarning"
    string Place);   // CornerLocator description or "—"

/// <summary>All incidents of one lap, lap-ascending, within a lap by distance.</summary>
public sealed record PenaltyLapGroup(int LapNumber, IReadOnlyList<PenaltyIncident> Incidents);

/// <summary>Result of <see cref="PenaltyReport.Build"/>: lap groups plus a one-line
/// German summary ("3 Strafen · 5 Verwarnungen").</summary>
public sealed record PenaltyReportData(IReadOnlyList<PenaltyLapGroup> Groups, string Summary);

/// <summary>Groups penalty/warning events per lap and stamps each with its place on the
/// track via <see cref="CornerLocator.Describe"/>. Pure and headless-testable.</summary>
public static class PenaltyReport
{
    /// <summary>Event types that count as incidents. "Served" events (DriveThroughServed,
    /// StopGoServed) are confirmations, not new incidents, and stay out.</summary>
    private static readonly string[] IncidentTypes = ["Penalty", "Warning", "CornerCuttingWarning"];

    public static PenaltyReportData Build(
        IReadOnlyList<RaceEventEntry> events,
        ushort trackLength,
        float sector2Start,
        float sector3Start,
        string track)
    {
        var incidents = events
            .Where(e => IncidentTypes.Contains(e.Type))
            .Select(e => new PenaltyIncident(
                e,
                e.Type,
                CornerLocator.Describe(e.LapDistance, trackLength, sector2Start, sector3Start, track)))
            .OrderBy(e => e.Event.LapNumber)
            .ThenBy(e => e.Event.LapDistance < 0 ? float.MaxValue : e.Event.LapDistance)
            .ThenBy(e => e.Event.Sequence)
            .ToList();

        var groups = incidents
            .GroupBy(i => i.Event.LapNumber)
            .Select(g => new PenaltyLapGroup(g.Key, g.ToList()))
            .ToList();

        var penalties = incidents.Count(i => i.Kind == "Penalty");
        var warnings = incidents.Count - penalties;
        var parts = new List<string>(2);
        if (penalties > 0)
        {
            parts.Add(Count(penalties, "Strafe", "Strafen"));
        }

        if (warnings > 0)
        {
            parts.Add(Count(warnings, "Verwarnung", "Verwarnungen"));
        }

        return new PenaltyReportData(groups, parts.Count > 0 ? string.Join(" · ", parts) : "Keine Vorfälle");
    }

    /// <summary>German plural-aware count: "1 Strafe" but "3 Strafen".</summary>
    private static string Count(int n, string singular, string plural) =>
        n == 1 ? $"1 {singular}" : $"{n} {plural}";
}