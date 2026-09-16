namespace ERCTelemetry.Core.Analysis;

/// <summary>Fuel-to-finish advice for the player car, race sessions only. Shortfall &gt; 0
/// means the tank will not cover the remaining laps — the driver must save fuel or pit.
/// Shortfall &lt; 0 is spare fuel in laps. LapsToGo 0 = no race lap counter;
/// ShortfallLaps null = no fuel reading yet.</summary>
public sealed record FuelAdvice(
    int LapsToGo,                 // race laps remaining after the current one (0 = unknown)
    float FuelLapsRemaining,      // game-reported laps of fuel on board
    int? ShortfallLaps,           // laps the tank is short of the finish (negative = spare)
    float LitresToFuel);          // litres to add (shortfall × fuel-per-lap, 0 = nothing needed)

public static class FuelCalculator
{
    /// <summary>Laps of fuel short of the finish; null without a lap counter. The game's
    /// FuelRemainingLaps is used as-is (it already accounts for consumption), with a
    /// half-lap grace before declaring a shortfall — the last lap can be coasted.</summary>
    public static int? Shortfall(int lapsToGo, float fuelLapsRemaining) =>
        lapsToGo <= 0 ? null : (int)Math.Ceiling(lapsToGo - fuelLapsRemaining - 0.5f);

    /// <summary>Litres to add: shortfall laps × last lap's burn (0 when the shortfall is
    /// negative/spare or the burn is unknown).</summary>
    public static float Litres(int shortfallLaps, float fuelPerLap) =>
        shortfallLaps <= 0 ? 0f : shortfallLaps * Math.Max(fuelPerLap, 0f);

    public static FuelAdvice? Compute(
        byte totalLaps, byte currentLap, float fuelLapsRemaining, float fuelPerLap)
    {
        if (totalLaps <= 0 || currentLap <= 0)
        {
            return null; // not a lap-based session (or no lap counter yet)
        }

        var lapsToGo = Math.Max(0, totalLaps - currentLap);
        var shortfall = Shortfall(lapsToGo, fuelLapsRemaining);
        return new FuelAdvice(
            lapsToGo,
            fuelLapsRemaining,
            shortfall,
            shortfall is { } laps ? Litres(laps, fuelPerLap) : 0f);
    }
}