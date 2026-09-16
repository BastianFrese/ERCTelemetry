namespace ERCTelemetry.Core.Tts;

/// <summary>Curated Microsoft Neural voices for the voice-alert dropdown. The settings
/// dropdown is editable, so any valid name can be typed; these are the tested German
/// voices on the Edge-TTS endpoint. The alerts are spoken in German, so non-German
/// voices are not offered.</summary>
public static class EdgeTtsVoices
{
    /// <summary>Default voice — a natural female German neural voice.</summary>
    public const string DefaultVoice = "de-DE-KatjaNeural";

    /// <summary>Voice name → display label, in dropdown order.</summary>
    public static IReadOnlyList<(string Name, string Label)> German { get; } =
    [
        (DefaultVoice, "Katja (weiblich, deutsch)"),
        ("de-DE-ConradNeural", "Conrad (männlich, deutsch)"),
        ("de-DE-FlorianMultilingualNeural", "Florian (männlich, mehrsprachig)"),
        ("de-AT-IngridNeural", "Ingrid (weiblich, österreichisch)"),
        ("de-AT-JonasNeural", "Jonas (männlich, österreichisch)"),
    ];
}
