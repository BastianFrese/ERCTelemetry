using ERCTelemetry.Core.TwitchChat;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class DriverNameMatcherTests
{
    private static readonly string[] Field = ["Manuel Hauser2", "Erdi10", "Erdi", "Max Verstappen"];

    [Fact]
    public void Substring_matches_partial_names()
    {
        // The idea's example: "hauser" must resolve "Manuel Hauser2" (no exact "Hauser"
        // in the field). "erdi" hits the exact "Erdi" first, then the prefix "Erdi10".
        Assert.Equal(["Manuel Hauser2"], DriverNameMatcher.Match("hauser", Field));
        Assert.Equal(["Erdi", "Erdi10"], DriverNameMatcher.Match("erdi", Field));
    }

    [Fact]
    public void Exact_match_beats_prefix_beats_substring()
    {
        // "Erdi" is an exact match and must win over the prefix match "Erdi10".
        Assert.Equal(["Erdi", "Erdi10"], DriverNameMatcher.Match("Erdi", Field));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal(["Max Verstappen"], DriverNameMatcher.Match("MAX", Field));
        Assert.Equal(["Manuel Hauser2"], DriverNameMatcher.Match("HAUSER", Field));
    }

    [Fact]
    public void Returns_all_matches_ordered_by_quality()
    {
        var matches = DriverNameMatcher.Match("erdi", Field);

        Assert.Equal(2, matches.Count);
        Assert.Equal("Erdi", matches[0]);   // exact
        Assert.Equal("Erdi10", matches[1]);  // prefix
    }

    [Fact]
    public void Empty_when_nothing_matches()
    {
        Assert.Empty(DriverNameMatcher.Match("leclerc", Field));
        Assert.Empty(DriverNameMatcher.Match("", Field));
        Assert.Empty(DriverNameMatcher.Match("  ", Field));
        Assert.Empty(DriverNameMatcher.Match("x", []));
    }
}
