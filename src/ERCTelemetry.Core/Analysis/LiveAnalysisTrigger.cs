using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Why the live analysis wants to speak right now.</summary>
public enum LiveAnalysisTriggerReason { TyreWear, FuelShortfall, TempoTrend, StatusUpdate }

/// <summary>Decides WHEN the live analysis speaks — the anti-spam gate. Stateful (single
/// consumer): each warning fires once per stint (reset on compound change), the tempo
/// trend re-arms when the slowdown ends, and a status update runs every
/// <see cref="StatusIntervalLaps"/> laps. Pure, headless-testable.</summary>
public sealed class LiveAnalysisTrigger
{
    /// <summary>Wear % at which the pit-window warning fires (once per stint).</summary>
    public const float TyreWearThresholdPercent = 60f;

    /// <summary>Per-lap slowdown (ms) at which the tempo-trend warning fires.</summary>
    public const float TempoTrendThresholdMsPerLap = 300f;

    /// <summary>Laps between periodic status updates.</summary>
    public const int StatusIntervalLaps = 5;

    private ActualCompound _lastCompound;
    private bool _tyreWarned;
    private bool _fuelWarned;
    private bool _trendWarned;
    private int _lastStatusLap;

    /// <summary>Returns the reason to speak now, or null to stay silent. The first
    /// matching reason wins — a tyre warning outranks the periodic status update.</summary>
    public LiveAnalysisTriggerReason? Evaluate(LiveAnalysisDigest digest)
    {
        // A compound change starts a new stint: the stint-scoped warnings re-arm.
        if (digest.TyreCompound != _lastCompound)
        {
            _lastCompound = digest.TyreCompound;
            _tyreWarned = false;
            _fuelWarned = false;
        }

        if (digest.TyreWearPercent >= TyreWearThresholdPercent && !_tyreWarned)
        {
            _tyreWarned = true;
            return LiveAnalysisTriggerReason.TyreWear;
        }

        if (digest.ProjectedFuelAtEnd < 0 && !_fuelWarned)
        {
            _fuelWarned = true;
            return LiveAnalysisTriggerReason.FuelShortfall;
        }

        // Re-arm the trend warning once the slowdown ends.
        if (digest.Trend != LapTrend.Slower)
        {
            _trendWarned = false;
        }

        if (digest.Trend == LapTrend.Slower &&
            digest.TrendDeltaMsPerLap >= TempoTrendThresholdMsPerLap &&
            !_trendWarned)
        {
            _trendWarned = true;
            return LiveAnalysisTriggerReason.TempoTrend;
        }

        if (digest.Lap > 0 && digest.Lap - _lastStatusLap >= StatusIntervalLaps)
        {
            _lastStatusLap = digest.Lap;
            return LiveAnalysisTriggerReason.StatusUpdate;
        }

        return null;
    }

    public void Reset()
    {
        _lastCompound = default;
        _tyreWarned = false;
        _fuelWarned = false;
        _trendWarned = false;
        _lastStatusLap = 0;
    }
}
