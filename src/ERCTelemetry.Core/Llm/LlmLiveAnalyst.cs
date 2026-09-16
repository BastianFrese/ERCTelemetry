using System.Globalization;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 live analyst backed by an <see cref="OllamaClient"/>: turns the
/// structured digest + trigger reason into natural German coaching advice. Null on
/// failure — the caller falls back to the <see cref="LiveAnalysisTemplate"/>.</summary>
public sealed class LlmLiveAnalyst : ILiveAnalyst
{
    private readonly OllamaClient _client;

    public LlmLiveAnalyst(OllamaClient client) => _client = client;

    public async Task<string?> AnalyzeAsync(LiveAnalysisDigest digest, LiveAnalysisTriggerReason reason, CancellationToken ct)
    {
        var system =
            "Du bist ein deutscher F1-Fahrcoach für einen Neuling. Analysiere den folgenden " +
            "Rennzustand und gib 2–4 konkrete, freundliche Sätze: was ist wichtig (Reifen, " +
            "Tank, Tempo), was soll der Fahrer tun. Keine Wiederholungen, keine Anführungszeichen.";
        var user = DigestText(digest, reason);
        return await _client.CompleteAsync(system, user, ct).ConfigureAwait(false);
    }

    private static string DigestText(LiveAnalysisDigest d, LiveAnalysisTriggerReason reason)
    {
        var laps = string.Join(", ", d.LastLapTimesMs.Select(t => (t / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)));
        return $"Grund: {reason}. Runde {d.Lap}/{d.TotalLaps}, Position {d.Position}, " +
               $"Lücke zum Führenden {d.GapToLeaderMs} ms. Letzte Rundenzeiten (s): {laps}. " +
               $"Reifen: {d.TyreCompound}, {d.TyreAgeLaps} Runden alt, " +
               $"{d.TyreWearPercent.ToString("0", CultureInfo.InvariantCulture)} % Verschleiß. " +
               $"Tank: {d.FuelInTank.ToString("0.0", CultureInfo.InvariantCulture)} Liter, reicht für " +
               $"{d.FuelRemainingLaps.ToString("0.0", CultureInfo.InvariantCulture)} Runden, " +
               $"Verbrauch {d.FuelUsedLastLap.ToString("0.0", CultureInfo.InvariantCulture)} Liter pro Runde.";
    }
}
