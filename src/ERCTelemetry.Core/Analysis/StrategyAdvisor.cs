namespace ERCTelemetry.Core.Analysis;

/// <summary>Player-car strategy advice derived per snapshot: how many laps the fitted tyres
/// have left (wear trend), which lap the pit stop is suggested for, and a fuel-corrected
/// target lap time. Negative/zero values mean "not known yet".</summary>
public sealed record StrategyAdvice(
    int TyreLapsLeft,       // −1 = no valid wear trend yet
    ushort SuggestedPitLap, // 0 = none suggested
    uint TargetLapMs);      // 0 = needs a best lap + fuel reading first

public static class StrategyAdvisor
{
    /// <summary>Lap-time penalty per litre of fuel on board — the classical rule of thumb
    /// (~0.03–0.04 s per lap per litre); ERCTelemetry uses 35 ms.</summary>
    public const double FuelPenaltyMsPerLitre = 35.0;

    /// <summary>Wear-per-lap below this is treated as "no usable trend" (measurement noise).</summary>
    public const float MinWearPerLap = 0.05f;

    public static StrategyAdvice Compute(
        uint bestLapMs,
        float fuelInTank,
        float fuelPerLap,     // litres burnt on the last completed lap (context only)
        float wearNow,        // worst-wheel wear %, right now
        float wearPerLap,     // trend since the stint's tyre change (0 = no trend yet)
        byte currentLap)
    {
        // Fuel-corrected target: what the driver should currently be lapping at.
        var target = 0u;
        if (bestLapMs > 0 && fuelInTank > 0)
        {
            target = bestLapMs + (uint)Math.Round(fuelInTank * FuelPenaltyMsPerLitre);
        }

        var tyreLapsLeft = -1;
        var suggestedPitLap = 0;
        if (wearPerLap >= MinWearPerLap)
        {
            tyreLapsLeft = (int)((100f - wearNow) / wearPerLap);
            if (tyreLapsLeft < 0)
            {
                tyreLapsLeft = 0;
            }

            if (currentLap > 0)
            {
                // Aim one lap before the wear runs out, never below the current lap.
                suggestedPitLap = (int)Math.Min(
                    999, currentLap + Math.Max(1, tyreLapsLeft - 1));
            }
        }

        return new StrategyAdvice(tyreLapsLeft, (ushort)suggestedPitLap, target);
    }
}