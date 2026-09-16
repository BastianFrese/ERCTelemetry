using System.Globalization;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-1 live analyst: deterministic German sentences for each trigger reason.
/// Works offline, no API key needed — the free baseline the LLM layer polishes on top.</summary>
public sealed class LiveAnalysisTemplate : ILiveAnalyst
{
    public Task<string?> AnalyzeAsync(LiveAnalysisDigest digest, LiveAnalysisTriggerReason reason, CancellationToken ct)
    {
        var text = reason switch
        {
            LiveAnalysisTriggerReason.TyreWear => TyreText(digest),
            LiveAnalysisTriggerReason.FuelShortfall => FuelText(digest),
            LiveAnalysisTriggerReason.TempoTrend => TrendText(digest),
            _ => StatusText(digest),
        };
        return Task.FromResult<string?>(text);
    }

    private static string TyreText(LiveAnalysisDigest d)
    {
        var wear = d.TyreWearPercent.ToString("0", CultureInfo.InvariantCulture);
        return $"Reifen bei {wear} Prozent Verschleiß — das Boxenfenster öffnet sich. " +
               "Überlege, wann du reinkommst.";
    }

    private static string FuelText(LiveAnalysisDigest d)
    {
        var burn = d.FuelUsedLastLap.ToString("0.0", CultureInfo.InvariantCulture);
        return $"Du verbrauchst {burn} Liter pro Runde — der Tank reicht nicht bis zum Ende. " +
               "Sparmodus oder früher Boxenstopp.";
    }

    private static string TrendText(LiveAnalysisDigest d)
    {
        var delta = (d.TrendDeltaMsPerLap / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        return $"Deine letzten Runden werden {delta} Sekunden pro Runde langsamer — " +
               "die Reifen bauen ab.";
    }

    private static string StatusText(LiveAnalysisDigest d)
    {
        var fuelLaps = d.FuelRemainingLaps.ToString("0", CultureInfo.InvariantCulture);
        var wear = d.TyreWearPercent.ToString("0", CultureInfo.InvariantCulture);
        return $"Runde {d.Lap} von {d.TotalLaps}, Position {d.Position}. " +
               $"Reifen bei {wear} Prozent, Tank reicht für {fuelLaps} Runden.";
    }
}
