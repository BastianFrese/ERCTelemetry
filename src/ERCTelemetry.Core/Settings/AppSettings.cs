using System.Text.Json.Serialization;
using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Telemetry;

namespace ERCTelemetry.Core.Settings;

/// <summary>App-wide look & behaviour choices, persisted as JSON in %LOCALAPPDATA%.
/// Immutable record — change values via <c>with</c> and hand the new instance to
/// <see cref="AppSettingsStore.Save"/>.</summary>
public sealed record AppSettings
{
    public AppSettings()
    {
    }

    [JsonConstructor]
    public AppSettings(
        int udpPort,
        int overlayPort,
        AppTheme theme,
        string? rivalDriverName,
        bool closeToTray,
        bool showNotifications,
        bool autoStartListening,
        bool hudShowTiming = true,
        bool hudShowDrive = true,
        bool hudShowStatus = true,
        HudLayout hudLayout = HudLayout.Compact,
        double hudLeft = -1,
        double hudTop = -1,
        OverlayColorScheme colorScheme = OverlayColorScheme.Classic,
        bool hudShowRival = true,
        int hudRivalCycleSeconds = 30,
        bool hudShowMap = false,
        bool hudShowWeather = true,
        bool hudShowRace = true,
        bool hudShowRadar = true,
        bool hudShowGmeter = false,
        IReadOnlyDictionary<string, string>? driverNameOverrides = null,
        IReadOnlyDictionary<string, double[]>? hudWidgetPositions = null,
        bool overlayH2hHeader = true,
        bool overlayH2hLaps = true,
        bool overlayH2hSectors = true,
        bool overlayH2hTyres = true,
        bool overlayH2hPits = true,
        bool overlayH2hTally = true,
        bool overlayComSession = true,
        bool overlayComBattles = true,
        bool overlayComMovers = true,
        bool overlayComFastest = true,
        bool overlayComPits = true,
        bool overlayComTyres = true,
        bool overlayComFlags = true,
        bool overlayComWeather = true,
        string? hudPreset = null,
        string? updateBaseUrl = null,
        ClipSettings? clips = null,
        string? shareBaseUrl = null,
        string? shareToken = null,
        string? ercApiUrl = null,
        string? ercApiKey = null,
        bool forwardingEnabled = false,
        IReadOnlyList<ForwardTarget>? forwardTargets = null,
        bool showDebugTab = false,
        double windowLeft = -1,
        double windowTop = -1,
        double windowWidth = 1120,
        double windowHeight = 700,
        bool voiceAlertsEnabled = false,
        bool hudShowTower = true,
        bool twitchEnabled = false,
        string? twitchChannel = null,
        string? twitchToken = null,
        bool llmEnabled = false,
        string? llmApiKey = null,
        string? llmModel = null,
        string? llmBaseUrl = null,
        string? voice = null,
        VoiceLanguage voiceLanguage = VoiceLanguage.German,
        bool proximityAlertsEnabled = true,
        bool liveAnalysisEnabled = false)
    {
        UdpPort = udpPort;
        OverlayPort = overlayPort;
        Theme = theme;
        RivalDriverName = rivalDriverName;
        CloseToTray = closeToTray;
        ShowNotifications = showNotifications;
        AutoStartListening = autoStartListening;
        HudShowTiming = hudShowTiming;
        HudShowDrive = hudShowDrive;
        HudShowStatus = hudShowStatus;
        HudLayout = hudLayout;
        HudLeft = hudLeft;
        HudTop = hudTop;
        ColorScheme = colorScheme;
        HudShowRival = hudShowRival;
        HudRivalCycleSeconds = hudRivalCycleSeconds;
        HudShowMap = hudShowMap;
        HudShowWeather = hudShowWeather;
        HudShowRace = hudShowRace;
        HudShowRadar = hudShowRadar;
        HudShowGmeter = hudShowGmeter;
        DriverNameOverrides = driverNameOverrides;
        HudWidgetPositions = hudWidgetPositions;
        OverlayH2hHeader = overlayH2hHeader;
        OverlayH2hLaps = overlayH2hLaps;
        OverlayH2hSectors = overlayH2hSectors;
        OverlayH2hTyres = overlayH2hTyres;
        OverlayH2hPits = overlayH2hPits;
        OverlayH2hTally = overlayH2hTally;
        OverlayComSession = overlayComSession;
        OverlayComBattles = overlayComBattles;
        OverlayComMovers = overlayComMovers;
        OverlayComFastest = overlayComFastest;
        OverlayComPits = overlayComPits;
        OverlayComTyres = overlayComTyres;
        OverlayComFlags = overlayComFlags;
        OverlayComWeather = overlayComWeather;
        HudPreset = hudPreset;
        UpdateBaseUrl = updateBaseUrl;
        Clips = clips ?? new ClipSettings();
        ShareBaseUrl = shareBaseUrl;
        ShareToken = shareToken;
        ErcApiUrl = ercApiUrl;
        ErcApiKey = ercApiKey;
        ForwardingEnabled = forwardingEnabled;
        ForwardTargets = forwardTargets;
        ShowDebugTab = showDebugTab;
        WindowLeft = windowLeft;
        WindowTop = windowTop;
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        VoiceAlertsEnabled = voiceAlertsEnabled;
        HudShowTower = hudShowTower;
        TwitchEnabled = twitchEnabled;
        TwitchChannel = twitchChannel;
        TwitchToken = twitchToken;
        LlmEnabled = llmEnabled;
        LlmApiKey = llmApiKey;
        LlmModel = llmModel;
        LlmBaseUrl = llmBaseUrl;
        Voice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
        VoiceLanguage = voiceLanguage;
        ProximityAlertsEnabled = proximityAlertsEnabled;
        LiveAnalysisEnabled = liveAnalysisEnabled;
    }

    /// <summary>UDP port the game streams telemetry to (game side must match).</summary>
    public int UdpPort { get; init; } = TelemetryConstants.DefaultUdpPort;

    /// <summary>Local loopback port of the OBS browser-source overlay web server.</summary>
    public int OverlayPort { get; init; } = TelemetryConstants.DefaultOverlayPort;

    /// <summary>Window theme — <see cref="AppTheme.System"/> follows the Windows app-dark setting.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Preferred rival for the team-telemetry comparison, matched by driver name
    /// when a session's participant list arrives. Null ⇒ auto (teammate).</summary>
    public string? RivalDriverName { get; init; }

    /// <summary>Closing the main window hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; init; }

    /// <summary>Show tray balloon notifications (session end, telemetry-format problems…).</summary>
    public bool ShowNotifications { get; init; } = true;

    /// <summary>Speak German voice alerts (tyre wear, rain, race-control events for the
    /// player) via Windows TTS. Off by default — opt-in in Settings → Verhalten.</summary>
    public bool VoiceAlertsEnabled { get; init; }

    /// <summary>Microsoft Neural voice for the speech alerts (Edge-TTS), e.g.
    /// "de-DE-KatjaNeural". Null/empty ⇒ the default Katja voice.</summary>
    public string? Voice { get; init; }

    /// <summary>Language of the spoken voice alerts — the AC-style overtake announcements
    /// switch between German and English; the rest of the alerts stay German.</summary>
    public VoiceLanguage VoiceLanguage { get; init; } = VoiceLanguage.German;

    /// <summary>AC-style proximity spotter: directional calls (car left/right/behind/ahead),
    /// counts, distances and "clear" when a zone empties. Default on — the beginner's
    /// precision layer on top of the overtake alerts.</summary>
    public bool ProximityAlertsEnabled { get; init; } = true;

    /// <summary>Periodic live analysis (tyres / fuel / tempo trend / status update) spoken
    /// during the race. Off by default: the LLM formulation costs API money, and the
    /// Layer-1 template works without a key but still speaks every few laps.</summary>
    public bool LiveAnalysisEnabled { get; init; }

    /// <summary>Start the UDP listener automatically at launch (default: on).</summary>
    public bool AutoStartListening { get; init; } = true;

    /// <summary>In-game HUD widget groups (see the overlay window). Defaults on —
    /// settings.json from an older version simply keeps all group visible.</summary>
    public bool HudShowTiming { get; init; } = true;

    /// <summary>Speed/gear/RPM/pedals/ERS block of the in-game HUD.</summary>
    public bool HudShowDrive { get; init; } = true;

    /// <summary>DRS · tyre · fuel block of the in-game HUD.</summary>
    public bool HudShowStatus { get; init; } = true;

    /// <summary>HUD size preset (compact = 1.0, large = 1.3 zoom).</summary>
    public HudLayout HudLayout { get; init; } = HudLayout.Compact;

    /// <summary>Saved HUD position; negative = auto spot (top-right). Pixels on the
    /// primary work area.</summary>
    public double HudLeft { get; init; } = -1;

    public double HudTop { get; init; } = -1;

    /// <summary>Overlay/HUD color scheme — <see cref="OverlayColorScheme.German"/> paints
    /// the in-game HUD and the browser overlay pages in black-red-gold.</summary>
    public OverlayColorScheme ColorScheme { get; init; } = OverlayColorScheme.Classic;

    /// <summary>Periodic rival details panel in the in-game HUD (auto-cycles in).</summary>
    public bool HudShowRival { get; init; } = true;

    /// <summary>How often the rival panel cycles in, in seconds (5–300).</summary>
    public int HudRivalCycleSeconds { get; init; } = 30;

    /// <summary>Track-minimap canvas in the in-game HUD (off by default — screen space).</summary>
    public bool HudShowMap { get; init; }

    /// <summary>Rain-forecast line in the in-game HUD (from the Session packet forecast).</summary>
    public bool HudShowWeather { get; init; } = true;

    /// <summary>Race-info widget of the in-game HUD (pit window, session clock, flags,
    /// invalid-badge, damage, tyre bank, traffic, fuel trend).</summary>
    public bool HudShowRace { get; init; } = true;

    /// <summary>Radar / blind-spot widget of the in-game HUD (cars alongside and
    /// just behind inside one second of race gap).</summary>
    public bool HudShowRadar { get; init; } = true;

    /// <summary>G-meter widget of the in-game HUD (crosshair dot for lateral and
    /// longitudinal forces on the player car).</summary>
    public bool HudShowGmeter { get; init; } = false;

    /// <summary>Timing-tower widget of the in-game HUD (field-wide mini sector marks
    /// green/purple per driver, like the F1 TV broadcast).</summary>
    public bool HudShowTower { get; init; } = true;

    /// <summary>Master switch for the Twitch chat bot — when off, no IRC connection is
    /// made even if a token is configured. Default off: chat commands are opt-in.</summary>
    public bool TwitchEnabled { get; init; }

    /// <summary>Twitch channel the bot joins (with or without leading '#'). The OAuth
    /// token must belong to this account — the bot posts replies as it.</summary>
    public string? TwitchChannel { get; init; }

    /// <summary>Twitch OAuth access token (chat:read + chat:edit). Stored locally; the
    /// bot authenticates with it over IRC.</summary>
    public string? TwitchToken { get; init; }

    /// <summary>Master switch for the optional LLM layer (AI-Kommentator, AI-Coach,
    /// Rennzusammenfassung). When off, only the deterministic Layer-1 templates run.
    /// Default off: the LLM is opt-in and never mandatory.</summary>
    public bool LlmEnabled { get; init; }

    /// <summary>Ollama API key (ollama.com/settings/keys). Stored locally; sent as a
    /// Bearer token to the configured base URL.</summary>
    public string? LlmApiKey { get; init; }

    /// <summary>Ollama model name. The LLM layer is locked to the fixed cost-efficient
    /// model (<c>deepseek-v4-flash:cloud</c>) — this field only preserves whatever was
    /// stored for schema compatibility, the service ignores any other value.</summary>
    public string? LlmModel { get; init; }

    /// <summary>Ollama base URL — "https://ollama.com" for the cloud API or
    /// "http://localhost:11434" for a local Ollama server.</summary>
    public string? LlmBaseUrl { get; init; }

    /// <summary>Display-name overrides keyed by the exact game-provided participant name
    /// (trimmed, match case-insensitive); empty value = no override for that key.
    /// Lets the user rename placeholder names (e.g. "Car 22") everywhere.</summary>
    public IReadOnlyDictionary<string, string>? DriverNameOverrides { get; init; }

    /// <summary>Per-widget HUD positions ([x, y] on the work area), keyed by widget id
    /// ("timing", "drive", "status", "map", "rival", "battles", "feed"). Missing key =
    /// widget sits at its auto-layout spot top-right.</summary>
    public IReadOnlyDictionary<string, double[]>? HudWidgetPositions { get; init; }

    /// <summary>Head-to-head overlay: duel title/header block (who is racing whom).</summary>
    public bool OverlayH2hHeader { get; init; } = true;

    /// <summary>Head-to-head overlay: last-lap comparison block.</summary>
    public bool OverlayH2hLaps { get; init; } = true;

    /// <summary>Head-to-head overlay: best-sector comparison block.</summary>
    public bool OverlayH2hSectors { get; init; } = true;

    /// <summary>Head-to-head overlay: tyre comparison block.</summary>
    public bool OverlayH2hTyres { get; init; } = true;

    /// <summary>Head-to-head overlay: pit-stop comparison block.</summary>
    public bool OverlayH2hPits { get; init; } = true;

    /// <summary>Head-to-head overlay: laps-won / position-wins tally block.</summary>
    public bool OverlayH2hTally { get; init; } = true;

    /// <summary>Commentary overlay: session info block (track, laps, session state).</summary>
    public bool OverlayComSession { get; init; } = true;

    /// <summary>Commentary overlay: running battle table block.</summary>
    public bool OverlayComBattles { get; init; } = true;

    /// <summary>Commentary overlay: position movers block (gainers/losers).</summary>
    public bool OverlayComMovers { get; init; } = true;

    /// <summary>Commentary overlay: fastest-lap feed block.</summary>
    public bool OverlayComFastest { get; init; } = true;

    /// <summary>Commentary overlay: pit-stop feed block.</summary>
    public bool OverlayComPits { get; init; } = true;

    /// <summary>Commentary overlay: tyre/strategy notes block.</summary>
    public bool OverlayComTyres { get; init; } = true;

    /// <summary>Commentary overlay: track-flags block.</summary>
    public bool OverlayComFlags { get; init; } = true;

    /// <summary>Commentary overlay: weather/forecast block.</summary>
    public bool OverlayComWeather { get; init; } = true;

    /// <summary>Last applied HUD layout preset (see <see cref="HudPresets"/>) — a display
    /// hint for the UI; null = user-configured ("Benutzerdefiniert").</summary>
    public string? HudPreset { get; init; }

    /// <summary>Base URL of the update server (manifest + Setup.exe live there), e.g.
    /// "https://erdi-erc.de/downloads". Null = built-in default from UpdateService.</summary>
    public string? UpdateBaseUrl { get; init; }

    /// <summary>Collision-clip recording (screen capture + FFmpeg encode). Defaults off —
    /// settings.json from an older version simply keeps recording disabled.</summary>
    public ClipSettings Clips { get; init; } = new();

    /// <summary>Base URL of the share server (session pages + clip uploads), e.g.
    /// "https://telemetrie.erdi-erc.de". Null = built-in default from ShareConstants.</summary>
    public string? ShareBaseUrl { get; init; }

    /// <summary>Upload token for the share server's /api/* endpoints. Must match the
    /// server's Share:Token config; null = sharing not configured.</summary>
    public string? ShareToken { get; init; }

    /// <summary>Base URL of the ERC race-result API (the /race and /leagues endpoints
    /// hang off it), e.g. "https://erdi-erc.de/api/telemetry". Null = built-in default
    /// from ErcConstants.</summary>
    public string? ErcApiUrl { get; init; }

    /// <summary>Personal send key for the ERC race-result API, issued by the ERC admin
    /// (X-Api-Key header; server stores only its hash). Stored locally; null = not
    /// configured — no prompt after a race.</summary>
    public string? ErcApiKey { get; init; }

    /// <summary>Master switch for UDP forwarding — when off, no datagram is re-sent even
    /// if <see cref="ForwardTargets"/> is populated. Default off: forwarding is opt-in.</summary>
    public bool ForwardingEnabled { get; init; }

    /// <summary>UDP destinations the raw telemetry datagrams are re-sent to (other apps on
    /// this machine, e.g. RaceLab/SimHub). Null = no targets configured.</summary>
    public IReadOnlyList<ForwardTarget>? ForwardTargets { get; init; }

    /// <summary>App version whose update-log section the user last saw shown automatically
    /// after an update — set when the post-update log window was presented; null = never.</summary>
    public string? LastSeenVersion { get; init; }

    /// <summary>Show the Debug tab in the nav rail (power-user/developer view with the
    /// packet table, replay and recording). Off by default so the main nav stays lean;
    /// toggle in Settings → Erweiterte Ansicht or via Ctrl+Shift+D.</summary>
    public bool ShowDebugTab { get; init; }

    /// <summary>Last main-window left edge (pixels on the virtual screen); -1 = not set
    /// (center on first launch). Restored on startup so the window keeps its place.</summary>
    public double WindowLeft { get; init; } = -1;

    /// <summary>Last main-window top edge; -1 = not set (center on first launch).</summary>
    public double WindowTop { get; init; } = -1;

    /// <summary>Last main-window width (default 1120).</summary>
    public double WindowWidth { get; init; } = 1120;

    /// <summary>Last main-window height (default 700).</summary>
    public double WindowHeight { get; init; } = 700;

    /// <summary>Returns a copy with out-of-range values reset to their defaults; a
    /// hand-edited settings.json can contain anything.</summary>
    public AppSettings Sanitized() => this with
    {
        UdpPort = UdpPort is >= 1 and <= 65535 ? UdpPort : TelemetryConstants.DefaultUdpPort,
        OverlayPort = OverlayPort is >= 1 and <= 65535 ? OverlayPort : TelemetryConstants.DefaultOverlayPort,
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        HudLayout = Enum.IsDefined(HudLayout) ? HudLayout : HudLayout.Compact,
        HudLeft = double.IsFinite(HudLeft) && HudLeft is >= -1 and <= 20000 ? HudLeft : -1,
        HudTop = double.IsFinite(HudTop) && HudTop is >= -1 and <= 20000 ? HudTop : -1,
        ColorScheme = Enum.IsDefined(ColorScheme) ? ColorScheme : OverlayColorScheme.Classic,
        HudRivalCycleSeconds = HudRivalCycleSeconds is >= 5 and <= 300 ? HudRivalCycleSeconds : 30,
        DriverNameOverrides = SanitizeNameOverrides(DriverNameOverrides),
        HudWidgetPositions = SanitizeWidgetPositions(HudWidgetPositions),
        HudPreset = HudPresets.IsKnown(HudPreset) ? HudPreset : null,
        Clips = Clips.Sanitized(),
        ShareToken = string.IsNullOrWhiteSpace(ShareToken) ? null : ShareToken.Trim(),
        ErcApiUrl = string.IsNullOrWhiteSpace(ErcApiUrl) ? null : ErcApiUrl.Trim().TrimEnd('/'),
        ErcApiKey = string.IsNullOrWhiteSpace(ErcApiKey) ? null : ErcApiKey.Trim(),
        ForwardingEnabled = ForwardingEnabled,
        ForwardTargets = SanitizeForwardTargets(ForwardTargets),
        WindowLeft = double.IsFinite(WindowLeft) && WindowLeft is >= -1 and <= 20000 ? WindowLeft : -1,
        WindowTop = double.IsFinite(WindowTop) && WindowTop is >= -1 and <= 20000 ? WindowTop : -1,
        WindowWidth = double.IsFinite(WindowWidth) && WindowWidth is >= 800 and <= 8000 ? WindowWidth : 1120,
        WindowHeight = double.IsFinite(WindowHeight) && WindowHeight is >= 500 and <= 6000 ? WindowHeight : 700,
        TwitchChannel = string.IsNullOrWhiteSpace(TwitchChannel) ? null : TwitchChannel.Trim().TrimStart('#'),
        TwitchToken = string.IsNullOrWhiteSpace(TwitchToken) ? null : TwitchToken.Trim(),
        LlmApiKey = string.IsNullOrWhiteSpace(LlmApiKey) ? null : LlmApiKey.Trim(),
        LlmModel = string.IsNullOrWhiteSpace(LlmModel) ? null : LlmModel.Trim(),
        LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? null : LlmBaseUrl.Trim().TrimEnd('/'),
        Voice = string.IsNullOrWhiteSpace(Voice) ? null : Voice.Trim(),
        VoiceLanguage = Enum.IsDefined(VoiceLanguage) ? VoiceLanguage : VoiceLanguage.German,
    };

    /// <summary>Only known widget ids with sane [x, y] pairs survive a hand-edited
    /// settings.json; unknown keys and junk values are dropped.</summary>
    private static IReadOnlyDictionary<string, double[]>? SanitizeWidgetPositions(
        IReadOnlyDictionary<string, double[]>? positions)
    {
        if (positions is null)
        {
            return null;
        }

        Dictionary<string, double[]> clean = new(StringComparer.Ordinal);
        foreach (var (widget, xy) in positions)
        {
            if (xy is not { Length: 2 } pair ||
                !double.IsFinite(xy[0]) || !double.IsFinite(xy[1]) ||
                xy[0] < 0 || xy[0] > 20000 || xy[1] < 0 || xy[1] > 20000 ||
                HudWidgetIds.All.Contains(widget) is false) // known ids only
            {
                continue;
            }

            clean[widget] = [xy[0], xy[1]];
        }

        return clean.Count > 0 ? clean : null;
    }

    private static IReadOnlyDictionary<string, string>? SanitizeNameOverrides(
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null)
        {
            return null;
        }

        Dictionary<string, string> clean = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (gameName, displayName) in overrides)
        {
            var key = gameName?.Trim() ?? string.Empty;
            var value = displayName?.Trim() ?? string.Empty;
            if (key.Length is 0 or > MaxNameOverrideLength || value.Length is 0 or > MaxNameOverrideLength)
            {
                continue;
            }

            clean[key] = value;
            if (clean.Count >= MaxNameOverrides)
            {
                break;
            }
        }

        return clean.Count > 0 ? clean : null;
    }

    /// <summary>Only valid forwarding targets survive a hand-edited settings.json: a
    /// non-empty address, a port in 1–65535 and at most <see cref="MaxForwardTargets"/>
    /// entries. Invalid entries are dropped, not clamped.</summary>
    private static IReadOnlyList<ForwardTarget>? SanitizeForwardTargets(
        IReadOnlyList<ForwardTarget>? targets)
    {
        if (targets is null)
        {
            return null;
        }

        var clean = new List<ForwardTarget>(Math.Min(targets.Count, MaxForwardTargets));
        foreach (var target in targets)
        {
            if (clean.Count >= MaxForwardTargets)
            {
                break;
            }

            var address = target.Address?.Trim() ?? string.Empty;
            if (address.Length == 0 || target.Port is < 1 or > 65535)
            {
                continue;
            }

            clean.Add(target with { Address = address });
        }

        return clean.Count > 0 ? clean : null;
    }

    /// <summary>Max number of stored forwarding targets.</summary>
    public const int MaxForwardTargets = 8;

    /// <summary>Max length of one override key (game name) or value (display name).</summary>
    public const int MaxNameOverrideLength = 40;

    /// <summary>Max number of stored name overrides.</summary>
    public const int MaxNameOverrides = 200;
}

/// <summary>In-game HUD size preset.</summary>
public enum HudLayout
{
    Compact = 0,
    Large = 1,
}

/// <summary>Stable ids of the in-game HUD widgets (key space for
/// <see cref="AppSettings.HudWidgetPositions"/>).</summary>
public static class HudWidgetIds
{
    public static readonly string[] All =
        ["timing", "drive", "status", "map", "rival", "battles", "feed", "race", "radar", "gmeter"];
}

/// <summary>Overlay/HUD color scheme — <see cref="Classic"/> keeps the original palette;
/// <see cref="German"/> is the black-red-gold scheme for the HUD and the browser overlay.</summary>
public enum OverlayColorScheme
{
    Classic = 0,
    German = 1,
}

/// <summary>Window theme choice — <see cref="System"/> follows the Windows app-dark setting.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}