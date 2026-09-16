using System.Collections.ObjectModel;
using System.ComponentModel;
using ERCTelemetry.App.Clips;
using ERCTelemetry.App.Composition;
using ERCTelemetry.App.OverlayServer;
using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Discord;
using ERCTelemetry.Core.Llm;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Telemetry;
using ERCTelemetry.Core.Tts;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Drives the Settings tab: ports, theme, color scheme, rival preference,
/// tray & notification flags, in-game HUD widgets and driver renames. Save persists via
/// <see cref="AppSettingsService"/> and applies what can be applied live (theme
/// immediately, ports by restarting listener/overlay host).</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppServices _services;
    private readonly DebugViewModel _debug;
    private readonly OverlayWebHost _overlay;
    private readonly AppSettingsService _settings;
    private readonly TwitchChatHost _twitchChat;
    private readonly DiscordLoginHost _discord;
    private readonly LlmService? _llm;

    // String fields are seeded by LoadFrom() in the ctor; the initializers only satisfy
    // the nullable compiler (the ctor always overwrites them before any binding reads).
    private string _udpPort = string.Empty;
    private string _overlayPort = string.Empty;
    // The AppTheme setting is intentionally not surfaced in the UI (dark-only app);
    // Save() no longer writes Theme, so the persisted value round-trips untouched.
    private OverlayColorScheme _selectedColorScheme;
    private string _rivalDriverName = string.Empty;
    private bool _closeToTray;
    private bool _showNotifications;
    private bool _voiceAlertsEnabled;
    private string _voice = string.Empty;
    private VoiceLanguage _voiceLanguage;
    private bool _proximityAlertsEnabled;
    private bool _liveAnalysisEnabled;
    private bool _autoStartListening;
    private HudLayout _selectedHudLayout;
    private string _selectedHudPreset = string.Empty;
    private bool _hudShowTiming;
    private bool _hudShowDrive;
    private bool _hudShowStatus;
    private bool _hudShowRival;
    private string _hudRivalCycleSeconds = string.Empty;
    private bool _hudShowMap;
    private bool _hudShowWeather;
    private bool _hudShowRace;
    private bool _hudShowRadar;
    private bool _hudShowGmeter;
    private bool _hudShowTower;
    private bool _overlayH2hHeader;
    private bool _overlayH2hLaps;
    private bool _overlayH2hSectors;
    private bool _overlayH2hTyres;
    private bool _overlayH2hPits;
    private bool _overlayH2hTally;
    private bool _overlayComSession;
    private bool _overlayComBattles;
    private bool _overlayComMovers;
    private bool _overlayComFastest;
    private bool _overlayComPits;
    private bool _overlayComTyres;
    private bool _overlayComFlags;
    private bool _overlayComWeather;
    private bool _clipEnabled;
    private bool _clipOnlyPlayer;
    private int _clipMinSeverity;
    private string _clipPreRollSeconds = string.Empty;
    private string _clipPostRollSeconds = string.Empty;
    private string _clipFps = string.Empty;
    private string _clipAudioDevice = string.Empty;
    private int _clipMaxWidth = 1280;
    private bool _showDebugTab;
    private bool _forwardingEnabled;
    private bool _twitchEnabled;
    private string _twitchStatus = string.Empty;
    private bool _llmEnabled;
    private string _llmApiKey = string.Empty;
    private string _llmModel = string.Empty;
    private string _llmBaseUrl = string.Empty;
    private string _llmStatus = string.Empty;
    private string _ercApiUrl = string.Empty;
    private string _ercApiKey = string.Empty;
    private string _ercStatus = string.Empty;
    private string _discordStatus = string.Empty;
    private string _discordUserText = string.Empty;
    private readonly ObservableCollection<ForwardTargetRow> _forwardTargets = new();
    private string _saveStatus = string.Empty;
    private bool _dirty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(
        AppServices services,
        DebugViewModel debug,
        OverlayWebHost overlay,
        AppSettingsService settings,
        TwitchChatHost twitchChat,
        DiscordLoginHost discord)
    {
        _services = services;
        _debug = debug;
        _overlay = overlay;
        _settings = settings;
        _twitchChat = twitchChat;
        _discord = discord;
        _llm = services.Llm;

        LoadFrom(settings.Current);
        _twitchStatus = _twitchChat.Status;
        _llmStatus = _llm?.Status ?? "deaktiviert (Layer 1 aktiv)";
        RefreshDiscordState();
    }

    /// <summary>Loads every settings-tab field from a settings instance (constructor and
    /// reset-to-defaults). The preset backing field is set directly so no live HUD apply
    /// fires; ends with a clean dirty state.</summary>
    private void LoadFrom(AppSettings current)
    {
        UdpPort = current.UdpPort.ToString();
        OverlayPort = current.OverlayPort.ToString();
        SelectedColorScheme = current.ColorScheme;
        RivalDriverName = current.RivalDriverName ?? string.Empty;
        CloseToTray = current.CloseToTray;
        ShowNotifications = current.ShowNotifications;
        VoiceAlertsEnabled = current.VoiceAlertsEnabled;
        Voice = current.Voice ?? EdgeTtsVoices.DefaultVoice;
        VoiceLanguage = current.VoiceLanguage;
        ProximityAlertsEnabled = current.ProximityAlertsEnabled;
        LiveAnalysisEnabled = current.LiveAnalysisEnabled;
        AutoStartListening = current.AutoStartListening;
        SelectedHudLayout = current.HudLayout;
        _selectedHudPreset = HudPresets.IsKnown(current.HudPreset)
            ? current.HudPreset!
            : CustomPreset;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedHudPreset)));
        HudShowTiming = current.HudShowTiming;
        HudShowDrive = current.HudShowDrive;
        HudShowStatus = current.HudShowStatus;
        HudShowRival = current.HudShowRival;
        HudRivalCycleSeconds = current.HudRivalCycleSeconds.ToString();
        HudShowMap = current.HudShowMap;
        HudShowWeather = current.HudShowWeather;
        HudShowRace = current.HudShowRace;
        HudShowRadar = current.HudShowRadar;
        HudShowGmeter = current.HudShowGmeter;
        HudShowTower = current.HudShowTower;
        OverlayH2hHeader = current.OverlayH2hHeader;
        OverlayH2hLaps = current.OverlayH2hLaps;
        OverlayH2hSectors = current.OverlayH2hSectors;
        OverlayH2hTyres = current.OverlayH2hTyres;
        OverlayH2hPits = current.OverlayH2hPits;
        OverlayH2hTally = current.OverlayH2hTally;
        OverlayComSession = current.OverlayComSession;
        OverlayComBattles = current.OverlayComBattles;
        OverlayComMovers = current.OverlayComMovers;
        OverlayComFastest = current.OverlayComFastest;
        OverlayComPits = current.OverlayComPits;
        OverlayComTyres = current.OverlayComTyres;
        OverlayComFlags = current.OverlayComFlags;
        OverlayComWeather = current.OverlayComWeather;
        ClipEnabled = current.Clips.Enabled;
        ClipOnlyPlayer = current.Clips.OnlyPlayerCollisions;
        ClipMinSeverity = current.Clips.MinSeverity;
        ClipPreRollSeconds = current.Clips.PreRollSeconds.ToString();
        ClipPostRollSeconds = current.Clips.PostRollSeconds.ToString();
        ClipFps = current.Clips.Fps.ToString();
        SelectedAudioDevice = current.Clips.AudioDeviceName ?? string.Empty;
        ClipMaxWidth = current.Clips.MaxWidth;
        ShowDebugTab = current.ShowDebugTab;
        ForwardingEnabled = current.ForwardingEnabled;
        TwitchEnabled = current.TwitchEnabled;
        LlmEnabled = current.LlmEnabled;
        LlmApiKey = current.LlmApiKey ?? string.Empty;
        LlmModel = LlmConstants.FixedModel;
        LlmBaseUrl = current.LlmBaseUrl ?? string.Empty;
        ErcApiUrl = current.ErcApiUrl ?? string.Empty;
        ErcApiKey = current.ErcApiKey ?? string.Empty;
        _forwardTargets.Clear();
        if (current.ForwardTargets is { } targets)
        {
            foreach (var target in targets)
            {
                _forwardTargets.Add(ForwardTargetRow.From(target));
            }
        }

        ClearDirty();
    }

    /// <summary>Resets every settings-tab field to the app defaults (a fresh
    /// <see cref="AppSettings"/>). Does NOT persist — the user reviews and saves.
    /// Window/HUD positions are runtime state and stay untouched.</summary>
    public void ResetToDefaults()
    {
        LoadFrom(new AppSettings());
        LlmStatus = _llm?.Status ?? "deaktiviert (Layer 1 aktiv)";
        MarkDirty();
        SaveStatus = "Auf Standard zurückgesetzt — jetzt SPEICHERN drücken.";
    }

    public string UdpPort
    {
        get => _udpPort;
        set => Set(ref _udpPort, value);
    }

    public string OverlayPort
    {
        get => _overlayPort;
        set => Set(ref _overlayPort, value);
    }

    public IReadOnlyList<OverlayColorScheme> ColorSchemeOptions { get; } =
        (OverlayColorScheme[])Enum.GetValues(typeof(OverlayColorScheme));

    public OverlayColorScheme SelectedColorScheme
    {
        get => _selectedColorScheme;
        set => Set(ref _selectedColorScheme, value);
    }

    public string RivalDriverName
    {
        get => _rivalDriverName;
        set => Set(ref _rivalDriverName, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => Set(ref _closeToTray, value);
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => Set(ref _showNotifications, value);
    }

    /// <summary>Speak German voice alerts (tyre wear, rain, race-control events for the
    /// player) via Windows TTS. Off by default — opt-in here.</summary>
    public bool VoiceAlertsEnabled
    {
        get => _voiceAlertsEnabled;
        set => Set(ref _voiceAlertsEnabled, value);
    }

    /// <summary>Selected Microsoft Neural voice (Edge-TTS) for the speech alerts, e.g.
    /// "de-DE-KatjaNeural". The dropdown is editable, so any valid voice name can be typed.</summary>
    public string Voice
    {
        get => _voice;
        set => Set(ref _voice, value);
    }

    /// <summary>German Neural voices for the dropdown (voice names, editable).</summary>
    public IReadOnlyList<string> VoiceOptions { get; } =
        EdgeTtsVoices.German.Select(v => v.Name).ToList();

    /// <summary>Language of the spoken voice alerts — the AC-style overtake announcements
    /// switch between German and English; the rest of the alerts stay German.</summary>
    public VoiceLanguage VoiceLanguage
    {
        get => _voiceLanguage;
        set => Set(ref _voiceLanguage, value);
    }

    public IReadOnlyList<VoiceLanguage> VoiceLanguageOptions { get; } =
        (VoiceLanguage[])Enum.GetValues(typeof(VoiceLanguage));

    /// <summary>AC-style proximity spotter: directional calls (car left/right/behind/ahead),
    /// counts, distances and "clear" when a zone empties. Default on — the beginner's
    /// precision layer on top of the overtake alerts.</summary>
    public bool ProximityAlertsEnabled
    {
        get => _proximityAlertsEnabled;
        set => Set(ref _proximityAlertsEnabled, value);
    }

    /// <summary>Periodic live analysis (tyres / fuel / tempo trend / status update) spoken
    /// during the race. Off by default: the LLM formulation costs API money, and the
    /// Layer-1 template works without a key but still speaks every few laps.</summary>
    public bool LiveAnalysisEnabled
    {
        get => _liveAnalysisEnabled;
        set => Set(ref _liveAnalysisEnabled, value);
    }

    public bool AutoStartListening
    {
        get => _autoStartListening;
        set => Set(ref _autoStartListening, value);
    }

    public IReadOnlyList<HudLayout> HudLayoutOptions { get; } =
        (HudLayout[])Enum.GetValues(typeof(HudLayout));

    public HudLayout SelectedHudLayout
    {
        get => _selectedHudLayout;
        set => Set(ref _selectedHudLayout, value);
    }

    /// <summary>Raised right after a named HUD preset was applied — MainWindow uses it to
    /// push the new widget layout into the live HUD window.</summary>
    public event Action? HudPresetApplied;

    public IReadOnlyList<string> HudPresetOptions { get; } =
        [HudPresets.Race, HudPresets.Practice, HudPresets.Compact, CustomPreset];

    /// <summary>UI label for "no preset" (user-configured layout).</summary>
    public const string CustomPreset = "Benutzerdefiniert";

    public string SelectedHudPreset
    {
        get => _selectedHudPreset;
        set
        {
            if (!Set(ref _selectedHudPreset, value))
            {
                return;
            }

            // A preset applies live (like the theme preview) — the checkboxes below
            // follow the preset values and the Save button simply persists them.
            if (HudPresets.IsKnown(value))
            {
                ApplyPreset(value);
            }
        }
    }

    private void ApplyPreset(string preset)
    {
        _settings.Update(s => HudPresets.Apply(s, preset));
        SyncHudFlagsFromSettings();
        HudPresetApplied?.Invoke();
    }

    /// <summary>Re-reads the widget-group fields after a preset rewrote them so the
    /// checkboxes reflect the applied layout.</summary>
    private void SyncHudFlagsFromSettings()
    {
        var current = _settings.Current;
        HudShowTiming = current.HudShowTiming;
        HudShowDrive = current.HudShowDrive;
        HudShowStatus = current.HudShowStatus;
        HudShowRival = current.HudShowRival;
        HudShowMap = current.HudShowMap;
        HudShowWeather = current.HudShowWeather;
        HudShowRace = current.HudShowRace;
        HudShowRadar = current.HudShowRadar;
        HudShowGmeter = current.HudShowGmeter;
        HudShowTower = current.HudShowTower;
    }

    public bool HudShowTiming
    {
        get => _hudShowTiming;
        set => Set(ref _hudShowTiming, value);
    }

    public bool HudShowDrive
    {
        get => _hudShowDrive;
        set => Set(ref _hudShowDrive, value);
    }

    public bool HudShowStatus
    {
        get => _hudShowStatus;
        set => Set(ref _hudShowStatus, value);
    }

    public bool HudShowRival
    {
        get => _hudShowRival;
        set => Set(ref _hudShowRival, value);
    }

    public string HudRivalCycleSeconds
    {
        get => _hudRivalCycleSeconds;
        set => Set(ref _hudRivalCycleSeconds, value);
    }

    public bool HudShowMap
    {
        get => _hudShowMap;
        set => Set(ref _hudShowMap, value);
    }

    public bool HudShowWeather
    {
        get => _hudShowWeather;
        set => Set(ref _hudShowWeather, value);
    }

    public bool HudShowRace
    {
        get => _hudShowRace;
        set => Set(ref _hudShowRace, value);
    }

    public bool HudShowRadar
    {
        get => _hudShowRadar;
        set => Set(ref _hudShowRadar, value);
    }

    public bool HudShowGmeter
    {
        get => _hudShowGmeter;
        set => Set(ref _hudShowGmeter, value);
    }

    public bool HudShowTower
    {
        get => _hudShowTower;
        set => Set(ref _hudShowTower, value);
    }

    public bool OverlayH2hHeader
    {
        get => _overlayH2hHeader;
        set => Set(ref _overlayH2hHeader, value);
    }

    public bool OverlayH2hLaps
    {
        get => _overlayH2hLaps;
        set => Set(ref _overlayH2hLaps, value);
    }

    public bool OverlayH2hSectors
    {
        get => _overlayH2hSectors;
        set => Set(ref _overlayH2hSectors, value);
    }

    public bool OverlayH2hTyres
    {
        get => _overlayH2hTyres;
        set => Set(ref _overlayH2hTyres, value);
    }

    public bool OverlayH2hPits
    {
        get => _overlayH2hPits;
        set => Set(ref _overlayH2hPits, value);
    }

    public bool OverlayH2hTally
    {
        get => _overlayH2hTally;
        set => Set(ref _overlayH2hTally, value);
    }

    public bool OverlayComSession
    {
        get => _overlayComSession;
        set => Set(ref _overlayComSession, value);
    }

    public bool OverlayComBattles
    {
        get => _overlayComBattles;
        set => Set(ref _overlayComBattles, value);
    }

    public bool OverlayComMovers
    {
        get => _overlayComMovers;
        set => Set(ref _overlayComMovers, value);
    }

    public bool OverlayComFastest
    {
        get => _overlayComFastest;
        set => Set(ref _overlayComFastest, value);
    }

    public bool OverlayComPits
    {
        get => _overlayComPits;
        set => Set(ref _overlayComPits, value);
    }

    public bool OverlayComTyres
    {
        get => _overlayComTyres;
        set => Set(ref _overlayComTyres, value);
    }

    public bool OverlayComFlags
    {
        get => _overlayComFlags;
        set => Set(ref _overlayComFlags, value);
    }

    public bool OverlayComWeather
    {
        get => _overlayComWeather;
        set => Set(ref _overlayComWeather, value);
    }

    /// <summary>One severity option for the clip threshold combo (0=gering, 1=mittel, 2=hoch).</summary>
    public sealed record SeverityOption(int Value, string Label);

    public IReadOnlyList<SeverityOption> ClipSeverityOptions { get; } =
        [new(0, "Gering"), new(1, "Mittel"), new(2, "Hoch")];

    public bool ClipEnabled
    {
        get => _clipEnabled;
        set => Set(ref _clipEnabled, value);
    }

    public bool ClipOnlyPlayer
    {
        get => _clipOnlyPlayer;
        set => Set(ref _clipOnlyPlayer, value);
    }

    public int ClipMinSeverity
    {
        get => _clipMinSeverity;
        set => Set(ref _clipMinSeverity, value);
    }

    public string ClipPreRollSeconds
    {
        get => _clipPreRollSeconds;
        set => Set(ref _clipPreRollSeconds, value);
    }

    public string ClipPostRollSeconds
    {
        get => _clipPostRollSeconds;
        set => Set(ref _clipPostRollSeconds, value);
    }

    public string ClipFps
    {
        get => _clipFps;
        set => Set(ref _clipFps, value);
    }

    /// <summary>One audio-device option for the clip dropdown (Name = device friendly
    /// name, empty = default render device).</summary>
    public sealed record AudioDeviceOption(string Name, string Label);

    /// <summary>Render devices the clip audio can be captured from, plus the default.</summary>
    public IReadOnlyList<AudioDeviceOption> AudioDeviceOptions { get; } = BuildAudioDeviceOptions();

    private static IReadOnlyList<AudioDeviceOption> BuildAudioDeviceOptions()
    {
        var options = new List<AudioDeviceOption> { new("", "Standardgerät (Standard)") };
        foreach (var name in AudioLoopbackSource.EnumerateDeviceNames())
        {
            options.Add(new AudioDeviceOption(name, name));
        }

        return options;
    }

    /// <summary>Selected render device for the clip audio (empty = default).</summary>
    public string SelectedAudioDevice
    {
        get => _clipAudioDevice;
        set => Set(ref _clipAudioDevice, value);
    }

    /// <summary>One clip-resolution option (max width in px; the aspect ratio is kept).</summary>
    public sealed record ResolutionOption(int Value, string Label);

    /// <summary>Clip max-width options. 1280 is the default; the last entry matches the
    /// primary display so a clip can be captured at full screen resolution.</summary>
    public IReadOnlyList<ResolutionOption> ClipMaxWidthOptions { get; } =
    [
        new(1280, "1280 (Standard)"),
        new(1920, "1920"),
        new(2560, "2560"),
        new(3440, "3440 (Bildschirm)"),
    ];

    /// <summary>Selected clip max width in pixels.</summary>
    public int ClipMaxWidth
    {
        get => _clipMaxWidth;
        set => Set(ref _clipMaxWidth, value);
    }

    /// <summary>Show the Debug tab in the nav rail (packet table, replay, recording).
    /// Off by default — the main nav stays lean; power users enable it here.</summary>
    public bool ShowDebugTab
    {
        get => _showDebugTab;
        set => Set(ref _showDebugTab, value);
    }

    /// <summary>Master switch for UDP forwarding — when off, no datagram is re-sent.</summary>
    public bool ForwardingEnabled
    {
        get => _forwardingEnabled;
        set => Set(ref _forwardingEnabled, value);
    }

    /// <summary>Master switch for the Twitch chat bot — when off, no connection is kept.
    /// The channel and token are set by the login flow, not by hand.</summary>
    public bool TwitchEnabled
    {
        get => _twitchEnabled;
        set => Set(ref _twitchEnabled, value);
    }

    /// <summary>Live connection status of the chat bot (shown next to the login button).</summary>
    public string TwitchStatus
    {
        get => _twitchStatus;
        private set => SetStatus(ref _twitchStatus, value);
    }

    /// <summary>Re-reads the bot status after a connect/disconnect/login (host raises
    /// StatusChanged on its own thread — the UI thread marshals via the binding).</summary>
    public void RefreshTwitchStatus() => TwitchStatus = _twitchChat.Status;

    /// <summary>Shows a one-off message (e.g. the login result) in the status text.</summary>
    public void SetTwitchStatus(string message) => TwitchStatus = message;

    /// <summary>Master switch for the LLM layer (Layer 2) — when off, everything stays on
    /// the deterministic Layer 1 templates.</summary>
    public bool LlmEnabled
    {
        get => _llmEnabled;
        set => Set(ref _llmEnabled, value);
    }

    /// <summary>Ollama API key (cloud subscription). Stored in settings.json like the
    /// Twitch token — never hardcoded.</summary>
    public string LlmApiKey
    {
        get => _llmApiKey;
        set => Set(ref _llmApiKey, value);
    }

    /// <summary>Model name — always the fixed, cost-efficient model
    /// (<see cref="LlmConstants.FixedModel"/>). Kept so settings.json stays self-consistent
    /// after a save, but it is no longer user-editable and the service ignores any value
    /// that was hand-edited into the file.</summary>
    public string LlmModel
    {
        get => _llmModel;
        set => Set(ref _llmModel, value);
    }

    /// <summary>Ollama base URL (default <c>https://ollama.com</c>).</summary>
    public string LlmBaseUrl
    {
        get => _llmBaseUrl;
        set => Set(ref _llmBaseUrl, value);
    }

    /// <summary>Live state of the LLM layer (deactivated / missing key / missing model /
    /// ready with the model name).</summary>
    public string LlmStatus
    {
        get => _llmStatus;
        private set => SetStatus(ref _llmStatus, value);
    }

    /// <summary>Shows a one-off message (e.g. the connection-test result) in the status text.</summary>
    public void SetLlmStatus(string message) => LlmStatus = message;

    /// <summary>Base URL of the ERC race-result API (the /race and /leagues endpoints hang
    /// off it). Empty = built-in default <c>https://erdi-erc.de/api/telemetry</c>.</summary>
    public string ErcApiUrl
    {
        get => _ercApiUrl;
        set => Set(ref _ercApiUrl, value);
    }

    /// <summary>Personal send key issued by the ERC admin (X-Api-Key header; the server
    /// stores only its hash). Stored in settings.json like the Twitch token — never hardcoded.
    /// Empty = no prompt after a league race.</summary>
    public string ErcApiKey
    {
        get => _ercApiKey;
        set => Set(ref _ercApiKey, value);
    }

    /// <summary>Live state of the ERC upload (last connection-test / send result).</summary>
    public string ErcStatus
    {
        get => _ercStatus;
        private set => SetStatus(ref _ercStatus, value);
    }

    /// <summary>Shows a one-off message (e.g. the connection-test result) in the status text.</summary>
    public void SetErcStatus(string message) => ErcStatus = message;

    /// <summary>Live state of the Discord login (shown next to the login button).</summary>
    public string DiscordStatus
    {
        get => _discordStatus;
        private set => SetStatus(ref _discordStatus, value);
    }

    /// <summary>Who is logged in via Discord, e.g. "Eingeloggt als Max" / "Nicht eingeloggt".</summary>
    public string DiscordUserText
    {
        get => _discordUserText;
        private set => SetStatus(ref _discordUserText, value);
    }

    /// <summary>Re-reads the Discord login state from the host (login/logout events).</summary>
    public void RefreshDiscordState()
    {
        DiscordStatus = _discord.Status;
        DiscordUserText = _discord.User is { } u
            ? $"Eingeloggt als {u.GlobalName ?? u.Username}"
            : "Nicht eingeloggt";
    }

    /// <summary>Shows a one-off message (e.g. the login result) in the Discord status text.</summary>
    public void SetDiscordStatus(string message) => DiscordStatus = message;

    /// <summary>Editable forwarding targets (one row per destination app).</summary>
    public ObservableCollection<ForwardTargetRow> ForwardTargets => _forwardTargets;

    /// <summary>Adds a blank target row (defaults to loopback + a common tool port).</summary>
    public void AddForwardTarget()
    {
        _forwardTargets.Add(new ForwardTargetRow { Address = "127.0.0.1", Port = "20779", Enabled = true });
        MarkDirty();
    }

    public void RemoveForwardTarget(ForwardTargetRow row)
    {
        _forwardTargets.Remove(row);
        MarkDirty();
    }

    /// <summary>How many driver renames (display-name overrides) are currently active.</summary>
    public int NameOverrideCount => _settings.Current.DriverNameOverrides?.Count ?? 0;

    public string SaveStatus
    {
        get => _saveStatus;
        private set => SetStatus(ref _saveStatus, value);
    }

    /// <summary>Restores the original game names everywhere (standings, HUD, overlays,
    /// history); the rename dialog re-creates overrides on demand.</summary>
    public void ClearNameOverrides()
    {
        _settings.Update(s => s with { DriverNameOverrides = null });
        _services.SessionStore.SetNameOverrides(null);
        RefreshNameOverrideCount();
        MarkDirty();
    }

    /// <summary>Re-reads the override count after the rename dialog added/removed one.</summary>
    public void RefreshNameOverrideCount() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameOverrideCount)));

    public async Task SaveAsync()
    {
        if (!TryParsePort(UdpPort, out var udpPort))
        {
            SaveStatus = "Ungültiger UDP-Port — bitte eine Zahl zwischen 1 und 65535 eingeben.";
            return;
        }

        if (!TryParsePort(OverlayPort, out var overlayPort))
        {
            SaveStatus = "Ungültiger Overlay-Port — bitte eine Zahl zwischen 1 und 65535 eingeben.";
            return;
        }

        if (!int.TryParse(HudRivalCycleSeconds.Trim(), out var rivalCycleSeconds))
        {
            rivalCycleSeconds = 30;
        }
        else
        {
            // Like the ports: clamp at the boundary instead of persisting nonsense.
            rivalCycleSeconds = Math.Clamp(rivalCycleSeconds, 5, 300);
        }

        var clipPreRoll = ParseClamped(ClipPreRollSeconds, 1, 60, 10);
        var clipPostRoll = ParseClamped(ClipPostRollSeconds, 1, 60, 10);
        var clipFps = ParseClamped(ClipFps, 1, 60, 10);

        var previous = _settings.Current;
        var settings = previous with
        {
            UdpPort = udpPort,
            OverlayPort = overlayPort,
            ColorScheme = SelectedColorScheme,
            RivalDriverName = string.IsNullOrWhiteSpace(RivalDriverName) ? null : RivalDriverName.Trim(),
            CloseToTray = CloseToTray,
            ShowNotifications = ShowNotifications,
            VoiceAlertsEnabled = VoiceAlertsEnabled,
            Voice = string.IsNullOrWhiteSpace(Voice) ? null : Voice.Trim(),
            VoiceLanguage = VoiceLanguage,
            ProximityAlertsEnabled = ProximityAlertsEnabled,
            LiveAnalysisEnabled = LiveAnalysisEnabled,
            AutoStartListening = AutoStartListening,
            HudLayout = SelectedHudLayout,
            HudPreset = HudPresets.IsKnown(SelectedHudPreset) ? SelectedHudPreset : null,
            HudShowTiming = HudShowTiming,
            HudShowDrive = HudShowDrive,
            HudShowStatus = HudShowStatus,
            HudShowRival = HudShowRival,
            HudRivalCycleSeconds = rivalCycleSeconds,
            HudShowMap = HudShowMap,
            HudShowWeather = HudShowWeather,
            HudShowRace = HudShowRace,
            HudShowRadar = HudShowRadar,
            HudShowGmeter = HudShowGmeter,
            HudShowTower = HudShowTower,
            OverlayH2hHeader = OverlayH2hHeader,
            OverlayH2hLaps = OverlayH2hLaps,
            OverlayH2hSectors = OverlayH2hSectors,
            OverlayH2hTyres = OverlayH2hTyres,
            OverlayH2hPits = OverlayH2hPits,
            OverlayH2hTally = OverlayH2hTally,
            OverlayComSession = OverlayComSession,
            OverlayComBattles = OverlayComBattles,
            OverlayComMovers = OverlayComMovers,
            OverlayComFastest = OverlayComFastest,
            OverlayComPits = OverlayComPits,
            OverlayComTyres = OverlayComTyres,
            OverlayComFlags = OverlayComFlags,
            OverlayComWeather = OverlayComWeather,
            ShowDebugTab = ShowDebugTab,
            ForwardingEnabled = ForwardingEnabled,
            ForwardTargets = BuildForwardTargets(),
            TwitchEnabled = TwitchEnabled,
            // Channel + token are owned by the login flow (host), not the form — a save
            // must not wipe them with an empty form field.
            TwitchChannel = previous.TwitchChannel,
            LlmEnabled = LlmEnabled,
            LlmApiKey = string.IsNullOrWhiteSpace(LlmApiKey) ? null : LlmApiKey.Trim(),
            LlmModel = LlmConstants.FixedModel,
            LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? null : LlmBaseUrl.Trim(),
            ErcApiUrl = string.IsNullOrWhiteSpace(ErcApiUrl) ? null : ErcApiUrl.Trim().TrimEnd('/'),
            ErcApiKey = string.IsNullOrWhiteSpace(ErcApiKey) ? null : ErcApiKey.Trim(),
            Clips = new ClipSettings(
                Enabled: ClipEnabled,
                OnlyPlayerCollisions: ClipOnlyPlayer,
                MinSeverity: ClipMinSeverity,
                PreRollSeconds: clipPreRoll,
                PostRollSeconds: clipPostRoll,
                Fps: clipFps,
                AudioDeviceName: string.IsNullOrWhiteSpace(SelectedAudioDevice) ? null : SelectedAudioDevice.Trim(),
                MaxWidth: ClipMaxWidth),
        };

        // The drag-saved HUD position and widget layout survive a settings save — only
        // the settings-tab fields are rewritten here.
        _settings.Update(s => settings with { HudLeft = s.HudLeft, HudTop = s.HudTop });

        // The status reflects the just-saved settings (the LLM client rebuilds on demand).
        LlmStatus = _llm?.Status ?? "deaktiviert (Layer 1 aktiv)";

        var notes = new List<string>();

        // Restart on mismatch with the actually bound port, not just with the last saved
        // one — e.g. the listener was auto-started on the saved port but the user retyped
        // a different one in the field without having saved it before.
        if (udpPort != previous.UdpPort || _services.IsListening && _services.ListeningPort != udpPort)
        {
            RestartListener(udpPort, notes);
        }

        if (overlayPort != previous.OverlayPort)
        {
            await RestartOverlayAsync(overlayPort, notes, previous.OverlayPort);
        }

        if (_settings.LastSaveError is { } saveError)
        {
            notes.Add(saveError);
        }

        // Live overlay push on every successful save: block visibility rides on the config
        // message, the color scheme on the next state message (forced — a static session
        // never differs in the deep-compare, so it would keep the old scheme otherwise).
        _overlay.PushOverlayConfig();
        _overlay.ForceState();

        SaveStatus = notes.Count > 0
            ? "Einstellungen gespeichert · " + string.Join(" · ", notes)
            : "Einstellungen gespeichert.";
        ClearDirty();
    }

    private void RestartListener(int udpPort, List<string> notes)
    {
        if (!_services.IsListening)
        {
            notes.Add($"UDP-Port auf {udpPort} gesetzt (Listener gestoppt)");
            return;
        }

        _debug.StopListening();
        _debug.StartListening(udpPort.ToString());
        notes.Add(_services.IsListening
            ? $"Listener auf UDP {udpPort} neu gestartet"
            : "Listener-Neustart fehlgeschlagen (Port belegt?) — siehe Debug-Tab");
    }

    private async Task RestartOverlayAsync(int overlayPort, List<string> notes, int previousPort)
    {
        if (!_overlay.IsRunning)
        {
            // A failed bind (e.g. occupied port) left the overlay down — a save with any
            // port is the promised recovery path, so start it here instead of reporting
            // it as stopped forever.
            try
            {
                await _overlay.StartAsync(overlayPort);
                if (_overlay.IsRunning)
                {
                    notes.Add($"Overlay auf Port {overlayPort} gestartet");
                }
                else
                {
                    // StartAsync returns silently once the host was disposed — the caller
                    // must not report a success that never happened.
                    notes.Add("Overlay konnte nicht gestartet werden (Herunterfahren?)");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Overlay-Start fehlgeschlagen — Port {overlayPort} belegt ({ex.Message})");
            }

            return;
        }

        try
        {
            await _overlay.RestartAsync(overlayPort);
            if (!_overlay.IsRunning)
            {
                // StartAsync returns silently once the host was disposed — the caller
                // must not report a success that never happened.
                notes.Add("Overlay konnte nicht gestartet werden (Herunterfahren?)");
                return;
            }

            notes.Add($"Overlay auf Port {overlayPort} neu gestartet");
        }
        catch (Exception)
        {
            // Try to put the working server back on the old port instead of leaving the
            // user without an overlay after a failed port change.
            try
            {
                await _overlay.RestartAsync(previousPort);
                notes.Add($"Overlay-Neustart fehlgeschlagen — Port {overlayPort} belegt, {previousPort} behalten");
            }
            catch (Exception rollbackEx)
            {
                notes.Add($"Overlay-Neustart fehlgeschlagen — Port {overlayPort} belegt " +
                          $"({rollbackEx.Message}); mit freiem Port speichern");
            }
        }
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text.Trim(), out port) && port is >= 1 and <= 65535;

    /// <summary>Parses a clip length/fps field, clamping to the valid range; a non-numeric
    /// value falls back to the default (the Sanitized() pass would do the same).</summary>
    private static int ParseClamped(string text, int min, int max, int fallback) =>
        int.TryParse(text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>Builds the persisted target list from the editable rows: empty addresses
    /// are dropped, ports are clamped to 1–65535 (non-numeric → default 20777), capped at
    /// <see cref="AppSettings.MaxForwardTargets"/>.</summary>
    private IReadOnlyList<ForwardTarget>? BuildForwardTargets()
    {
        var targets = new List<ForwardTarget>(_forwardTargets.Count);
        foreach (var row in _forwardTargets)
        {
            if (targets.Count >= AppSettings.MaxForwardTargets)
            {
                break;
            }

            var address = row.Address?.Trim() ?? string.Empty;
            if (address.Length == 0)
            {
                continue;
            }

            var port = int.TryParse(row.Port?.Trim(), out var p) ? Math.Clamp(p, 1, 65535) : 20777;
            targets.Add(new ForwardTarget { Address = address, Port = port, Enabled = row.Enabled });
        }

        return targets.Count > 0 ? targets : null;
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        MarkDirty();
        return true;
    }

    /// <summary>True while any settings-tab field differs from the last saved state —
    /// drives the "Änderungen nicht gespeichert" hint in the save bar.</summary>
    public bool HasUnsavedChanges => _dirty;

    /// <summary>Short save-bar hint reflecting <see cref="HasUnsavedChanges"/>.</summary>
    public string SaveHint => _dirty ? "Änderungen nicht gespeichert" : "Alle Änderungen gespeichert";

    private void MarkDirty()
    {
        if (_dirty)
        {
            return;
        }

        _dirty = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnsavedChanges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveHint)));
    }

    /// <summary>Clears the dirty flag after a successful save (and after a load).</summary>
    private void ClearDirty()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnsavedChanges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveHint)));
    }

    /// <summary>Sets a status-only property (no dirty tracking) — used for the transient
    /// Twitch/LLM/save status texts.</summary>
    private void SetStatus(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }
}

/// <summary>Editable row for one forwarding target in the Settings tab. Mutable (INPC) so
/// the TextBox/CheckBox bindings update; converted to an immutable
/// <see cref="ForwardTarget"/> on Save.</summary>
public sealed class ForwardTargetRow : INotifyPropertyChanged
{
    private string _address = "127.0.0.1";
    private string _port = "20779";
    private bool _enabled = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Address
    {
        get => _address;
        set
        {
            if (_address == value)
            {
                return;
            }

            _address = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Address)));
        }
    }

    public string Port
    {
        get => _port;
        set
        {
            if (_port == value)
            {
                return;
            }

            _port = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Port)));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public static ForwardTargetRow From(ForwardTarget target) => new()
    {
        Address = target.Address,
        Port = target.Port.ToString(),
        Enabled = target.Enabled,
    };
}