namespace ERCTelemetry.Core.Settings;

/// <summary>Stable ids of the overlay visibility blocks — the key space shared by the
/// settings booleans (<see cref="AppSettings"/>), the <c>config</c> wire message and the
/// overlay pages. Never rename a key: pages match on these exact strings.</summary>
public static class OverlayBlocks
{
    /// <summary>Head-to-head overlay: duel title/header block.</summary>
    public const string H2hHeader = "h2h.header";

    /// <summary>Head-to-head overlay: last-lap comparison block.</summary>
    public const string H2hLaps = "h2h.laps";

    /// <summary>Head-to-head overlay: best-sector comparison block.</summary>
    public const string H2hSectors = "h2h.sectors";

    /// <summary>Head-to-head overlay: tyre comparison block.</summary>
    public const string H2hTyres = "h2h.tyres";

    /// <summary>Head-to-head overlay: pit-stop comparison block.</summary>
    public const string H2hPits = "h2h.pits";

    /// <summary>Head-to-head overlay: laps-won / position-wins tally block.</summary>
    public const string H2hTally = "h2h.tally";

    /// <summary>Commentary overlay: session info block.</summary>
    public const string ComSession = "com.session";

    /// <summary>Commentary overlay: running battle table block.</summary>
    public const string ComBattles = "com.battles";

    /// <summary>Commentary overlay: position movers block.</summary>
    public const string ComMovers = "com.movers";

    /// <summary>Commentary overlay: fastest-lap feed block.</summary>
    public const string ComFastest = "com.fastest";

    /// <summary>Commentary overlay: pit-stop feed block.</summary>
    public const string ComPits = "com.pits";

    /// <summary>Commentary overlay: tyre/strategy notes block.</summary>
    public const string ComTyres = "com.tyres";

    /// <summary>Commentary overlay: track-flags block.</summary>
    public const string ComFlags = "com.flags";

    /// <summary>Commentary overlay: weather/forecast block.</summary>
    public const string ComWeather = "com.weather";

    /// <summary>Maps the settings onto the block-id dictionary pushed with the
    /// <c>config</c> message — every id is always present, value = visible or not.</summary>
    public static IReadOnlyDictionary<string, bool> FromSettings(AppSettings settings) =>
        new Dictionary<string, bool>
        {
            [H2hHeader] = settings.OverlayH2hHeader,
            [H2hLaps] = settings.OverlayH2hLaps,
            [H2hSectors] = settings.OverlayH2hSectors,
            [H2hTyres] = settings.OverlayH2hTyres,
            [H2hPits] = settings.OverlayH2hPits,
            [H2hTally] = settings.OverlayH2hTally,
            [ComSession] = settings.OverlayComSession,
            [ComBattles] = settings.OverlayComBattles,
            [ComMovers] = settings.OverlayComMovers,
            [ComFastest] = settings.OverlayComFastest,
            [ComPits] = settings.OverlayComPits,
            [ComTyres] = settings.OverlayComTyres,
            [ComFlags] = settings.OverlayComFlags,
            [ComWeather] = settings.OverlayComWeather,
        };
}