namespace ERCTelemetry.Core.Settings;

/// <summary>Named HUD layout presets. Applying one rewrites the widget-group flags and
/// resets the per-widget positions to the auto stack (top-right) — the fastest way to
/// switch between a race day, a practice session and a minimal display. Manual toggles
/// afterwards simply mark the layout "Benutzerdefiniert" in the UI; the settings stay
/// user-controlled and the preset is never re-enforced.</summary>
public static class HudPresets
{
    public const string Race = "Race";
    public const string Practice = "Practice";
    public const string Compact = "Compact";

    public static readonly string[] All = [Race, Practice, Compact];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name);

    /// <summary>Returns a copy of <paramref name="settings"/> with the widget groups of
    /// <paramref name="preset"/> and positions cleared to the auto stack. Unknown preset
    /// names return the settings unchanged (a hand-edited settings.json is not a command).</summary>
    public static AppSettings Apply(AppSettings settings, string preset) => preset switch
    {
        Race => Apply(settings,
            timing: true, drive: true, status: true, rival: true,
            map: false, weather: true, race: true, radar: true, gmeter: true, name: Race),
        Practice => Apply(settings,
            timing: true, drive: true, status: true, rival: false,
            map: true, weather: true, race: false, radar: true, gmeter: false, name: Practice),
        Compact => Apply(settings,
            timing: true, drive: true, status: false, rival: false,
            map: false, weather: false, race: false, radar: false, gmeter: false, name: Compact),
        _ => settings,
    };

    private static AppSettings Apply(
        AppSettings settings,
        bool timing, bool drive, bool status, bool rival,
        bool map, bool weather, bool race, bool radar, bool gmeter, string name) => settings with
    {
        HudShowTiming = timing,
        HudShowDrive = drive,
        HudShowStatus = status,
        HudShowRival = rival,
        HudShowMap = map,
        HudShowWeather = weather,
        HudShowRace = race,
        HudShowRadar = radar,
        HudShowGmeter = gmeter,
        HudWidgetPositions = null, // preset = fresh auto stack, no per-widget offsets
        HudPreset = name,
    };
}