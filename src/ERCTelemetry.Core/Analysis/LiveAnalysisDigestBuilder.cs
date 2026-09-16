using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Builds the <see cref="LiveAnalysisDigest"/> from periodic snapshots. Stateful
/// (single consumer): keeps a rolling window of the player's last completed lap times so
/// the trend has history — the snapshot only carries the most recent lap. Resets on a
/// session change. Pure, headless-testable; the App feeds it snapshots and the trigger
/// decides when to speak.</summary>
public sealed class LiveAnalysisDigestBuilder
{
    /// <summary>Lap-time trend below this (ms per lap) is treated as "stable" — noise.</summary>
    public const float TrendThresholdMsPerLap = 200f;

    private const int WindowSize = 5;

    private readonly List<uint> _lapTimes = new(WindowSize);
    private uint _lastLapTimeMs;
    private ulong _sessionUid;

    /// <summary>Builds the digest for the current snapshot. Returns null before the first
    /// session (no Meta) or when the player has no standings row yet.</summary>
    public LiveAnalysisDigest? Build(TelemetrySnapshot snapshot)
    {
        if (snapshot.Meta is not { } meta)
        {
            return null;
        }

        var row = snapshot.Standings.FirstOrDefault(r => r.IsPlayer);
        if (row is null)
        {
            return null;
        }

        if (_sessionUid != meta.SessionUid)
        {
            _sessionUid = meta.SessionUid;
            _lapTimes.Clear();
            _lastLapTimeMs = 0;
        }

        // A new completed lap (LastLapTimeMs changed) appends to the rolling window.
        if (row.LastLapTimeMs > 0 && row.LastLapTimeMs != _lastLapTimeMs)
        {
            _lastLapTimeMs = row.LastLapTimeMs;
            _lapTimes.Add(row.LastLapTimeMs);
            if (_lapTimes.Count > WindowSize)
            {
                _lapTimes.RemoveAt(0);
            }
        }

        var (trend, delta) = ComputeTrend();
        var tyreWear = snapshot.Tyres?.FirstOrDefault(t => t.CarIndex == meta.PlayerCarIndex)?.WearPercent ?? 0f;
        var fuelInTank = snapshot.Player?.FuelInTank ?? 0f;
        var fuelRemainingLaps = snapshot.Player?.FuelRemainingLaps ?? 0f;
        var lapsToGo = Math.Max(0, meta.TotalLaps - row.CurrentLapNum);

        return new LiveAnalysisDigest(
            row.CurrentLapNum,
            meta.TotalLaps,
            row.Position,
            row.GapToLeaderMs,
            _lapTimes.ToArray(),
            row.BestLapTimeMs,
            trend,
            delta,
            row.TyreCompound,
            row.TyreAgeLaps,
            tyreWear,
            fuelInTank,
            fuelRemainingLaps,
            row.FuelUsedLastLap,
            fuelInTank - row.FuelUsedLastLap * lapsToGo);
    }

    /// <summary>Per-lap trend from the window: the last lap vs. the first, divided by the
    /// laps between them. Needs at least 3 laps; below the threshold it is "stable".</summary>
    private (LapTrend Trend, float DeltaMsPerLap) ComputeTrend()
    {
        if (_lapTimes.Count < 3)
        {
            return (LapTrend.Stable, 0f);
        }

        // Cast to int first: uint subtraction wraps on a negative delta (faster laps).
        var delta = ((int)_lapTimes[^1] - (int)_lapTimes[0]) / (float)(_lapTimes.Count - 1);
        var trend = delta > TrendThresholdMsPerLap ? LapTrend.Slower
            : delta < -TrendThresholdMsPerLap ? LapTrend.Faster
            : LapTrend.Stable;
        return (trend, delta);
    }
}
