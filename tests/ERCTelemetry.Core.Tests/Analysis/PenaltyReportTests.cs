using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>Penalty/warning grouping for the History rework's "Strafen & Verwarnungen"
/// section: lap grouping, within-lap distance ordering, place stamping via CornerLocator,
/// German singular-aware summary.</summary>
public sealed class PenaltyReportTests
{
    // Monza: 5793 m; sector boundaries as the game reports them (see CornerLocatorTests).
    private const ushort MonzaLength = 5793;
    private const float MonzaSector2 = 1800f;
    private const float MonzaSector3 = 3600f;

    private static RaceEventEntry Event(string type, int lap, float distance, long sequence = 0) =>
        new(DateTimeOffset.UtcNow, type, 0, "text", sequence, lap, LapDistance: distance);

    [Fact]
    public void Groups_per_lap_ascending_and_within_lap_by_distance()
    {
        var data = PenaltyReport.Build(
        [
            Event("Penalty", 5, 4000f, sequence: 1),
            Event("Warning", 3, 1000f, sequence: 2),
            Event("Penalty", 3, 4300f, sequence: 3),
            Event("Penalty", 3, -1f, sequence: 4),        // unknown distance sorts last
            Event("Overtake", 2, 2000f, sequence: 5),     // not an incident type
            Event("CornerCuttingWarning", 1, 500f, sequence: 6),
        ], MonzaLength, MonzaSector2, MonzaSector3, "Monza");

        Assert.Equal([1, 3, 5], data.Groups.Select(g => g.LapNumber));
        Assert.Equal(
            [1000f, 4300f, -1f],
            data.Groups.Single(g => g.LapNumber == 3).Incidents.Select(i => i.Event.LapDistance));
        Assert.All(data.Groups.SelectMany(g => g.Incidents), i => Assert.NotEqual("Overtake", i.Kind));
    }

    [Fact]
    public void Place_is_stamped_with_corner_sector_percent_and_km()
    {
        var data = PenaltyReport.Build(
            [Event("Penalty", 8, 4750f)], MonzaLength, MonzaSector2, MonzaSector3, "Monza");

        var place = data.Groups.Single().Incidents.Single().Place;
        Assert.Contains("~Kurve 10", place);
        Assert.Contains("Sektor 3", place);
        Assert.Contains("82 %", place);
        Assert.Contains("4,8 km", place);
    }

    [Fact]
    public void Unstamped_rows_show_a_dash()
    {
        var data = PenaltyReport.Build(
            [Event("Warning", 2, -1f)], 0, 0f, 0f, "UnknownTrack");

        Assert.Equal("—", data.Groups.Single().Incidents.Single().Place);
    }

    [Fact]
    public void Summary_counts_singular_and_plural()
    {
        var one = PenaltyReport.Build(
            [Event("Penalty", 1, 100f)], 0, 0f, 0f, string.Empty);
        Assert.Equal("1 Strafe", one.Summary);

        var mixed = PenaltyReport.Build(
        [
            Event("Penalty", 1, 100f),
            Event("Penalty", 2, 100f),
            Event("Penalty", 3, 100f),
            Event("Warning", 4, 100f),
            Event("CornerCuttingWarning", 5, 100f),
        ], 0, 0f, 0f, string.Empty);
        Assert.Equal("3 Strafen · 2 Verwarnungen", mixed.Summary);

        var none = PenaltyReport.Build(
            [Event("Overtake", 1, 100f)], 0, 0f, 0f, string.Empty);
        Assert.Equal("Keine Vorfälle", none.Summary);
    }

    [Fact]
    public void Empty_input_yields_no_groups()
    {
        var data = PenaltyReport.Build([], 0, 0f, 0f, string.Empty);

        Assert.Empty(data.Groups);
        Assert.Equal("Keine Vorfälle", data.Summary);
    }
}