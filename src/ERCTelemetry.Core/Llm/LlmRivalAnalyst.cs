using System.Globalization;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 rival analyst backed by an <see cref="OllamaClient"/>: turns the
/// structured rival digest into natural German commentary. Null on failure — the caller
/// falls back to the <see cref="RivalTemplate"/>.</summary>
public sealed class LlmRivalAnalyst : IRivalAnalyst
{
    private readonly OllamaClient _client;

    public LlmRivalAnalyst(OllamaClient client) => _client = client;

    public async Task<string?> AnalyzeAsync(RivalDigest digest, CancellationToken ct)
    {
        var system =
            "Du bist ein deutscher F1-Rennkommentator für einen Neuling. Beschreibe in 1–2 " +
            "kurzen, freundlichen Sätzen, was der Rivale gerade gemacht hat und was das für " +
            "den Fahrer bedeutet. Keine Wiederholungen, keine Anführungszeichen.";
        var user = DigestText(digest);
        return await _client.CompleteAsync(system, user, ct).ConfigureAwait(false);
    }

    private static string DigestText(RivalDigest d)
    {
        var lastLap = (d.LastLapTimeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        var bestLap = (d.BestLapTimeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        var trend = (d.TrendDeltaMsPerLap / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        return $"Ereignis: {d.Event}. Rivale: {d.RivalName}, Runde {d.Lap}, " +
               $"Reifen {d.Compound} ({d.TyreAgeLaps} Runden alt), letzte Runde {lastLap} s, " +
               $"Bestzeit {bestLap} s, Lücke zum Spieler {d.GapToPlayerMs} ms, " +
               $"Tempo-Trend {trend} s pro Runde.";
    }
}
