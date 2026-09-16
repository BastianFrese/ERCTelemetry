using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Session;

/// <summary>User-visible team labels. The F1 2024–2026 season packs append the season to
/// the F1 team enum members (Mercedes26, Sauber24, …), so <c>Team.ToString()</c> spills a
/// season number into every results table, session card and export. The database stores
/// the raw enum name (lossless, still <c>Enum.TryParse</c>-able); this class strips the
/// suffix only where a human reads the name.</summary>
public static class TeamName
{
    /// <summary>Enum → display label: "Mercedes26" → "Mercedes", plain members pass
    /// through unchanged, and a numeric (un-named) value keeps its number.</summary>
    public static string Display(this Team team) => Display(team.ToString());

    /// <summary>Stored team string → display label. Parses the raw name first so legacy
    /// rows ("Mercedes26") and future clean rows ("Mercedes") both resolve; an empty or
    /// un-recognized string passes through untouched.</summary>
    public static string Display(string? storedTeam)
    {
        if (string.IsNullOrEmpty(storedTeam))
        {
            return storedTeam ?? string.Empty;
        }

        if (!Enum.TryParse<Team>(storedTeam, out var team))
        {
            return storedTeam;
        }

        var name = team.ToString();
        var i = name.Length;
        while (i > 0 && char.IsAsciiDigit(name[i - 1]))
        {
            i--;
        }

        return i == 0 ? name : name[..i];
    }
}
