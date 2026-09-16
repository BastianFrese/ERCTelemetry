using System.Globalization;

namespace ERCTelemetry.Core.Persistence;

/// <summary>Maps a distance-around-lap stamp (stored on penalty/warning events) to a
/// human-readable place: sector from the stored sector-boundary distances, approximate
/// corner number from a static per-track turn count. Pure and headless-testable — the
/// UI never invents a corner number for an unknown track.</summary>
public static class CornerLocator
{
    // The app is German-only — format the rendered place deterministically (km decimals,
    // percent) instead of riding the machine's current culture.
    private static readonly CultureInfo Text = CultureInfo.GetCultureInfo("de-DE");
    /// <summary>Turn count of each circuit's main layout, keyed by the track enum name
    /// with any "F1_" prefix already stripped (the locator normalises both shapes).
    /// Approximate by design — labels are shown with a "~" qualifier in the UI.</summary>
    private static readonly Dictionary<string, int> Corners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bahrain"] = 15,
        ["Sakhir"] = 15,
        ["Jeddah"] = 27,
        ["Melbourne"] = 14,
        ["Shanghai"] = 16,
        ["Montreal"] = 14,
        ["Silverstone"] = 18,
        ["Imola"] = 19,
        ["Monaco"] = 19,
        ["Monte_Carlo"] = 19,
        ["Spa"] = 19,
        ["Zandvoort"] = 14,
        ["Monza"] = 11,
        ["Baku"] = 20,
        ["Singapore"] = 19,
        ["Suzuka"] = 18,
        ["Yas_Marina"] = 16,
        ["YasMarina"] = 16,
        ["Miami"] = 19,
        ["Austin"] = 20,
        ["MexicoCity"] = 17,
        ["SaoPaulo"] = 15,
        ["Interlagos"] = 15,
        ["LasVegas"] = 17,
        ["Losail"] = 16,
        ["Qatar"] = 16,
        ["Hungaroring"] = 14,
        ["Barcelona"] = 14,
        ["Catalunya"] = 14,
        ["Nurburgring"] = 15,
        ["Istanbul"] = 14,
        ["Hockenheim"] = 17,
        ["PaulRicard"] = 15,
        ["Ricard"] = 15,
        ["Mugello"] = 15,
        ["Portimao"] = 15,
        ["RedBullRing"] = 10,
        ["Spielberg"] = 10,
        ["Sochi"] = 18,
        ["Estoril"] = 16,
        ["Sepang"] = 15,
        ["Valencia"] = 25,
        ["Korea"] = 18,
        ["Buddh"] = 16,
    };

    /// <summary>Renders the place of an incident, e.g. "~Kurve 12 · Sektor 3 · 82 % · 4,1 km"
    /// or, when the track has no corner entry, "Sektor 3 · 82 % · 4,1 km". Returns "—"
    /// when the event carried no distance (rows stored before the stamp existed).</summary>
    public static string Describe(float lapDistance, ushort trackLength,
        float sector2Start, float sector3Start, string track)
    {
        if (lapDistance < 0f)
        {
            return "—";
        }

        var parts = new List<string>(4);
        if (CornerNumber(lapDistance, trackLength, track) is { } corner)
        {
            parts.Add($"~Kurve {corner}");
        }

        if (Sector(lapDistance, sector2Start, sector3Start) is { } sector and > 0)
        {
            parts.Add($"Sektor {sector}");
        }

        parts.Add(Percent(lapDistance, trackLength).ToString("0", Text) + " %");
        if (trackLength > 0)
        {
            parts.Add((lapDistance / 1000.0).ToString("0.0", Text) + " km");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>1|2|3 from the stored sector-boundary distances; 0 when the boundaries
    /// were never captured (both 0 means unknown, not "everything is sector 1").</summary>
    public static int Sector(float lapDistance, float sector2Start, float sector3Start)
    {
        if (lapDistance < 0f || sector2Start <= 0f || sector3Start <= 0f)
        {
            return 0;
        }

        return lapDistance < sector2Start ? 1
            : lapDistance < sector3Start ? 2
            : 3;
    }

    /// <summary>Approximate corner number from the distance fraction over the track's
    /// turn count. Null when the track is unknown to the table or the distance/length
    /// pair is unusable — never invent a corner for an unmapped circuit.</summary>
    public static int? CornerNumber(float lapDistance, ushort trackLength, string track)
    {
        if (lapDistance < 0f || trackLength <= 0 ||
            !Corners.TryGetValue(Normalize(track), out var count))
        {
            return null;
        }

        var fraction = Math.Clamp(lapDistance / trackLength, 0f, 1f);
        return Math.Clamp((int)Math.Ceiling(fraction * count), 1, count);
    }

    /// <summary>Distance around the lap as a 0..100 percent value (0 when the length is
    /// unknown or the distance is unstamped).</summary>
    public static double Percent(float lapDistance, ushort trackLength)
    {
        return trackLength <= 0 || lapDistance < 0f
            ? 0.0
            : Math.Clamp(lapDistance / trackLength, 0f, 1f) * 100.0;
    }

    private static string Normalize(string track)
    {
        return track.StartsWith("F1_", StringComparison.OrdinalIgnoreCase) ? track[3..] : track;
    }
}