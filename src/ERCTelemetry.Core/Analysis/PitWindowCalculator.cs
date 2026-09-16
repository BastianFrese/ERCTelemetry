namespace ERCTelemetry.Core.Analysis;

/// <summary>Live pit-window advice for the player: "If you pit now, do you come out ahead
/// of the car in front?" — the undercut question. Pure and testable; the HUD renders the
/// result. All times in milliseconds, all laps relative to now.</summary>
public sealed record PitWindowAdvice(
    bool UndercutWorks,        // pitting now puts you ahead of the car ahead (before they pit)
    int? UndercutWindowLaps,   // laps you can still wait before the undercut stops working (null = never)
    bool OvercutWorks,         // staying out while the car ahead pits puts you ahead
    int GapMsToAhead,          // current gap, positive = behind
    int PitLossMs,             // net pit stop time loss (pit lane + in/out lap delta)
    int TyreDeltaMsPerLap,     // fresh-tyre advantage per lap over the car ahead's older tyres
    int LapsUntilAheadPits);   // estimated laps until the car ahead pits

public static class PitWindowCalculator
{
    /// <summary>Typical F1 pit lane time (ms) — the game's own pit window is per-track;
    /// this is the fallback when no better estimate exists.</summary>
    public const int DefaultPitLaneMs = 22_000;

    /// <summary>In-lap + out-lap time loss vs. a normal lap (ms).</summary>
    public const int DefaultInOutDeltaMs = 1_500;

    /// <summary>Lap-time cost per percent of tyre wear (ms) — used to turn a wear trend
    /// into a fresh-tyre delta. ~0.1 s per % is the classical rule of thumb.</summary>
    public const int MsPerWearPercent = 100;

    public static PitWindowAdvice Compute(
        int gapMsToAhead,        // positive = behind
        int pitLaneMs,
        int inOutDeltaMs,
        int tyreDeltaMsPerLap,   // fresh-tyre advantage per lap over the car ahead (0 = unknown)
        int lapsUntilAheadPits,  // estimated laps until the car ahead pits
        int gapChangeMsPerLap = 0) // how the gap moves per lap (positive = you lose time)
    {
        var pitLossMs = pitLaneMs + inOutDeltaMs;

        // Undercut: you pit now, lose pitLoss, then gain tyreDelta per lap over the car
        // ahead (who stays out) until they pit. You rejoin ahead if the total gain covers
        // the gap plus the pit loss.
        var works = tyreDeltaMsPerLap > 0 && lapsUntilAheadPits > 0 &&
            gapMsToAhead + pitLossMs < (long)tyreDeltaMsPerLap * lapsUntilAheadPits;

        int? windowLaps = null;
        if (works)
        {
            var numerator = (long)tyreDeltaMsPerLap * lapsUntilAheadPits - gapMsToAhead - pitLossMs;
            var denom = gapChangeMsPerLap + tyreDeltaMsPerLap;
            windowLaps = denom <= 0
                ? lapsUntilAheadPits // gap is stable or shrinking — you can wait until they pit
                : Math.Min(lapsUntilAheadPits, (int)Math.Floor((double)numerator / denom));
        }

        // Overcut: the car ahead pits now (loses pitLoss), you stay out; you rejoin ahead
        // when you eventually pit if your track position advantage beats their fresh-tyre
        // gain over the same horizon.
        var overcut = tyreDeltaMsPerLap > 0 && lapsUntilAheadPits > 0 &&
            pitLossMs > (long)tyreDeltaMsPerLap * lapsUntilAheadPits;

        return new PitWindowAdvice(
            works, windowLaps, overcut,
            gapMsToAhead, pitLossMs, tyreDeltaMsPerLap, lapsUntilAheadPits);
    }
}
