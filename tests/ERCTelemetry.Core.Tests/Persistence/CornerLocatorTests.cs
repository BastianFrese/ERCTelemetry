using ERCTelemetry.Core.Persistence;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>Corner/sector attribution from a distance-around-lap stamp: sector
/// boundaries, corner-count table, percent/km rendering, unknown-value handling.</summary>
public sealed class CornerLocatorTests
{
    // Monza: 5793 m, 11 corners; sector boundaries as the game reports them.
    private const ushort MonzaLength = 5793;
    private const float MonzaSector2 = 1900f;
    private const float MonzaSector3 = 3800f;

    [Fact]
    public void Describe_renders_corner_sector_percent_and_km()
    {
        // 82 % of Monza is in sector 3.
        var text = CornerLocator.Describe(4750f, MonzaLength, MonzaSector2, MonzaSector3, "Monza");

        Assert.Contains("~Kurve 10", text);
        Assert.Contains("Sektor 3", text);
        Assert.Contains("82 %", text);
        Assert.Contains("4,8 km", text);
    }

    [Fact]
    public void Describe_omits_corner_when_track_is_unknown()
    {
        var text = CornerLocator.Describe(
            1000f, MonzaLength, MonzaSector2, MonzaSector3, "NotARealTrack");

        Assert.DoesNotContain("Kurve", text);
        Assert.Contains("Sektor 1", text);
    }

    [Fact]
    public void Describe_returns_dash_without_distance_stamp()
    {
        Assert.Equal("—", CornerLocator.Describe(-1f, MonzaLength, MonzaSector2, MonzaSector3, "Monza"));
        Assert.Equal("—", CornerLocator.Describe(-1f, 0, 0f, 0f, string.Empty));
    }

    [Theory]
    [InlineData(500f, 1)]    // before sector 2 start
    [InlineData(2500f, 2)]   // between the two boundaries
    [InlineData(5000f, 3)]   // after sector 3 start
    public void Sector_follows_the_stored_boundaries(float distance, int expected)
    {
        Assert.Equal(expected, CornerLocator.Sector(distance, MonzaSector2, MonzaSector3));
    }

    [Fact]
    public void Sector_is_unknown_without_boundaries_or_distance()
    {
        Assert.Equal(0, CornerLocator.Sector(1000f, 0f, 0f));
        Assert.Equal(0, CornerLocator.Sector(-1f, MonzaSector2, MonzaSector3));
    }

    [Fact]
    public void Corner_number_maps_fraction_over_the_turn_count()
    {
        Assert.Equal(1, CornerLocator.CornerNumber(0f, MonzaLength, "Monza")); // start line
        Assert.Equal(11, CornerLocator.CornerNumber(5700f, MonzaLength, "Monza")); // Parabolica
        Assert.Null(CornerLocator.CornerNumber(1000f, 0, "Monza")); // length unknown
        Assert.Null(CornerLocator.CornerNumber(1000f, MonzaLength, "UnknownTrack"));
    }

    [Fact]
    public void Corner_number_normalises_the_F1_prefix()
    {
        Assert.Equal(
            CornerLocator.CornerNumber(3000f, MonzaLength, "Monza"),
            CornerLocator.CornerNumber(3000f, MonzaLength, "F1_Monza"));
    }

    [Fact]
    public void Percent_clamps_to_0_100()
    {
        Assert.Equal(50.0, CornerLocator.Percent(MonzaLength / 2f, MonzaLength), 0.5);
        Assert.Equal(100.0, CornerLocator.Percent(2f * MonzaLength, MonzaLength)); // overshoot clamps
        Assert.Equal(0.0, CornerLocator.Percent(1000f, 0)); // length unknown
        Assert.Equal(0.0, CornerLocator.Percent(-1f, MonzaLength)); // unstamped
    }
}