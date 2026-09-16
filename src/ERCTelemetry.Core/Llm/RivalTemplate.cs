using ERCTelemetry.Core.Analysis;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-1 rival analyst: deterministic German sentences for each notable rival
/// action. Works offline, no API key needed — the free baseline the LLM layer polishes on
/// top.</summary>
public sealed class RivalTemplate : IRivalAnalyst
{
    public Task<string?> AnalyzeAsync(RivalDigest digest, CancellationToken ct)
    {
        var text = digest.Event switch
        {
            RivalEvent.PitStop => PitStopText(digest),
            RivalEvent.TyreChange => TyreChangeText(digest),
            _ => FastLapText(digest),
        };
        return Task.FromResult<string?>(text);
    }

    private static string PitStopText(RivalDigest d) =>
        $"{d.RivalName} ist in Runde {d.Lap} reingekommen und hat {CompoundName(d.Compound)} aufgezogen.";

    private static string TyreChangeText(RivalDigest d) =>
        $"{d.RivalName} hat auf {CompoundName(d.Compound)} gewechselt.";

    private static string FastLapText(RivalDigest d) =>
        $"{d.RivalName} fährt eine schnelle Runde — neue Bestzeit.";

    private static string CompoundName(ActualCompound compound) => compound switch
    {
        ActualCompound.F1C1 or ActualCompound.F1C0 or ActualCompound.F2Hard => "Hart",
        ActualCompound.F1C2 or ActualCompound.F2Medium => "Medium",
        ActualCompound.F1C3 or ActualCompound.F1C6 or ActualCompound.F2Soft or ActualCompound.F2SuperSoft => "Weich",
        ActualCompound.F1C4 or ActualCompound.F1Inter => "Intermediates",
        ActualCompound.F1C5 or ActualCompound.F1Wet or ActualCompound.F2Wet => "Regen",
        ActualCompound.F1ClassicDry => "Trockenreifen",
        ActualCompound.F1ClassicWet => "Regenreifen",
        _ => "neue Reifen",
    };
}
