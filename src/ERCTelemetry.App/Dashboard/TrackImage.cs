using System.Windows.Media;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Track presentation for the session cards: an embedded circuit image
/// (CC-BY-4.0 f1-circuits-svg, see Assets/Tracks/LICENSE.txt) with a stable fallback
/// monogram tile for tracks without an image, plus the short badge text for a session
/// type. Static only — the maps never change at runtime.</summary>
public static class TrackImage
{
    /// <summary>Pack URI of the embedded track image for a track enum name (with or
    /// without the "F1_" prefix); null when no image is embedded — cards then show the
    /// monogram tile instead of an empty field.</summary>
    public static string? ImageUri(string track)
    {
        var name = Label(track);
        return Embedded.Contains(name)
            ? $"pack://application:,,,/Assets/Tracks/{name}.png"
            : null;
    }

    /// <summary>Track enum names that have an embedded image (the F1_ prefix is
    /// normalized away before lookup).</summary>
    private static readonly HashSet<string> Embedded = new(StringComparer.Ordinal)
    {
        "Bahrain", "Baku", "Barcelona", "Buddh", "Catalunya", "Estoril", "Hockenheim",
        "Hungaroring", "Imola", "Interlagos", "Istanbul", "Jeddah", "Korea", "LasVegas",
        "Losail", "Madring", "Melbourne", "MexicoCity", "Miami", "Monaco", "Monte_Carlo",
        "Montreal", "Mugello", "Monza", "Nurburgring", "PaulRicard", "Portimao", "Qatar",
        "RedBullRing", "Ricard", "Sakhir", "SaoPaulo", "Sepang", "Shanghai", "Silverstone",
        "Sochi", "Spa", "Spielberg", "Suzuka", "Valencia", "Yas_Marina", "YasMarina",
        "Zandvoort",
    };

    /// <summary>Three-letter circuit code for the fallback tile (track enum names carry
    /// no game-provided short code, so a small table holds the well-known ones; anything
    /// else falls back to the first three letters).</summary>
    public static string Monogram(string track)
    {
        var name = Label(track);
        return Codes.TryGetValue(name, out var code)
            ? code
            : name.Length >= 3 ? name[..3].ToUpperInvariant() : name.ToUpperInvariant();
    }

    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Melbourne"] = "MEL", ["Sakhir"] = "BAH", ["Bahrain"] = "BAH",
        ["Jeddah"] = "JED", ["Shanghai"] = "SHG", ["Monaco"] = "MON",
        ["Monte_Carlo"] = "MON", ["Silverstone"] = "SIL", ["Imola"] = "IMO",
        ["Spa"] = "SPA", ["Zandvoort"] = "ZAN", ["Monza"] = "ITA",
        ["Baku"] = "BAK", ["Singapore"] = "SIN", ["Suzuka"] = "JPN",
        ["Yas_Marina"] = "ABU", ["YasMarina"] = "ABU", ["Miami"] = "MIA",
        ["Austin"] = "USA", ["MexicoCity"] = "MEX", ["SaoPaulo"] = "BRA",
        ["Interlagos"] = "BRA", ["LasVegas"] = "LVG", ["Losail"] = "QAT",
        ["Qatar"] = "QAT", ["Hungaroring"] = "HUN", ["Barcelona"] = "ESP",
        ["Catalunya"] = "ESP", ["Nurburgring"] = "GER", ["Istanbul"] = "TUR",
        ["Hockenheim"] = "GER", ["PaulRicard"] = "FRA", ["Ricard"] = "FRA",
        ["Mugello"] = "ITA", ["Portimao"] = "POR", ["RedBullRing"] = "AUT",
        ["Spielberg"] = "AUT", ["Sochi"] = "RUS", ["Estoril"] = "POR",
        ["Sepang"] = "MAL", ["Valencia"] = "ESP", ["Korea"] = "KOR",
        ["Buddh"] = "IND", ["Montreal"] = "CAN", ["Madring"] = "MAD",
    };

    /// <summary>Stable tile background for a track: a fixed hash hue at muted
    /// saturation/lightness so every track gets its own tint while the dark theme keeps
    /// its calm. Frozen brush — queried per card render.</summary>
    public static Brush MonogramBackground(string track)
    {
        var name = Label(track);
        var hue = 0;
        foreach (var c in name)
        {
            hue = (hue * 31 + c) % 360;
        }

        var brush = new SolidColorBrush(ColorFromHsl(hue, 0.38, 0.30));
        brush.Freeze();
        return brush;
    }

    private static Color ColorFromHsl(int hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = (hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Track enum name without the "F1_" prefix — the display label.</summary>
    public static string Label(string track) =>
        track.StartsWith("F1_", StringComparison.Ordinal) ? track[3..] : track;

    /// <summary>Uppercase badge text for the card's session type: multi-word enum names
    /// ("ShortQualifying") read "SHORT QUALIFYING".</summary>
    public static string SessionTypeBadge(string sessionType)
    {
        var sb = new System.Text.StringBuilder(sessionType.Length + 4);
        foreach (var c in sessionType.Replace("_", " "))
        {
            if (char.IsUpper(c) && sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
            {
                sb.Append(' ');
            }

            sb.Append(c);
        }

        return sb.ToString().ToUpperInvariant();
    }
}