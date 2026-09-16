using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

/// <summary>The F1 2026 season pack appends "26" to the team enum members; the results,
/// cards, exports and overlays must show "Mercedes", not "Mercedes26".</summary>
public sealed class TeamNameTests
{
    [Theory]
    [InlineData(Team.Mercedes26, "Mercedes")]
    [InlineData(Team.Williams26, "Williams")]
    [InlineData(Team.RedBullRacing26, "RedBullRacing")]
    [InlineData(Team.Cadillac26, "Cadillac")]
    [InlineData(Team.Mercedes, "Mercedes")] // plain member passes through
    [InlineData(Team.Konnersport, "Konnersport")] // no season suffix
    public void Display_strips_the_season_suffix_from_enum_members(Team team, string expected)
    {
        Assert.Equal(expected, team.Display());
    }

    [Theory]
    [InlineData("Mercedes26", "Mercedes")]
    [InlineData("AstonMartin26", "AstonMartin")]
    [InlineData("Mercedes", "Mercedes")]
    [InlineData("26", "26")] // un-named numeric value is kept, never emptied
    [InlineData("", "")]
    public void Display_handles_stored_team_strings(string stored, string expected)
    {
        Assert.Equal(expected, TeamName.Display(stored));
    }
}
