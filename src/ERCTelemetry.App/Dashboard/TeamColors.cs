using System.Windows.Media;
using F1Game.UDP.Enums;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Static F1 team → brand-color map (2024-era liveries, the hues used on
/// erdi-erc.de). App-layer only — Core stays team-color-free. Brushes are frozen and
/// cached: the map never changes at runtime and rows re-query it at telemetry rate.</summary>
public static class TeamColors
{
    public static readonly Brush Fallback = Frozen(0xFF8B929C);

    private static readonly Dictionary<Team, Brush> Map = new()
    {
        [Team.Mercedes] = Frozen(0xFF27F4D2),
        [Team.Ferrari] = Frozen(0xFFE8002D),
        [Team.RedBullRacing] = Frozen(0xFF3671C6),
        [Team.Williams] = Frozen(0xFF64C4FF),
        [Team.AstonMartin] = Frozen(0xFF229971),
        [Team.Alpine] = Frozen(0xFFFF87BC),
        [Team.McLaren] = Frozen(0xFFFF8000),
        [Team.Sauber] = Frozen(0xFF52E252),
        [Team.RacingBulls] = Frozen(0xFF6692FF),
        [Team.Haas] = Frozen(0xFFB6BABD),
        // Year variants (F1 24 / F1 26 seasons in F1 26) reuse the current livery hues.
        [Team.Mercedes24] = Frozen(0xFF27F4D2),
        [Team.Ferrari24] = Frozen(0xFFE8002D),
        [Team.RedBullRacing24] = Frozen(0xFF3671C6),
        [Team.Williams24] = Frozen(0xFF64C4FF),
        [Team.AstonMartin24] = Frozen(0xFF229971),
        [Team.Alpine24] = Frozen(0xFFFF87BC),
        [Team.RacingBulls24] = Frozen(0xFF6692FF),
        [Team.Haas24] = Frozen(0xFFB6BABD),
        [Team.McLaren24] = Frozen(0xFFFF8000),
        [Team.Sauber24] = Frozen(0xFF52E252),
        [Team.Mercedes26] = Frozen(0xFF27F4D2),
        [Team.Ferrari26] = Frozen(0xFFE8002D),
        [Team.RedBullRacing26] = Frozen(0xFF3671C6),
        [Team.Williams26] = Frozen(0xFF64C4FF),
        [Team.AstonMartin26] = Frozen(0xFF229971),
        [Team.Alpine26] = Frozen(0xFFFF87BC),
        [Team.RacingBulls26] = Frozen(0xFF6692FF),
        [Team.Haas26] = Frozen(0xFFB6BABD),
        [Team.McLaren26] = Frozen(0xFFFF8000),
        // Sauber became Audi for 2026 — keep the green identity.
        [Team.Audi26] = Frozen(0xFF52E252),
    };

    /// <summary>Brand brush for a team; teams without an entry (my-team, classic, F2,
    /// unknown ids) get the neutral fallback so the grid never throws or renders black.</summary>
    public static Brush For(Team team) => Map.TryGetValue(team, out var brush) ? brush : Fallback;

    private static Brush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}