namespace ERCTelemetry.Core.Session;

/// <summary>Player-vs-rival comparison used by the "team telemetry" panel.</summary>
public sealed record TelemetryComparison(
    string PlayerName,
    string RivalName,
    int SpeedDeltaKph,
    float ThrottleDelta,
    float BrakeDelta,
    float ErsStoreDelta, // joules
    float FuelDelta,
    int BestLapDeltaMs,
    string? Note)
{
    /// <summary>ERS storage delta as a percent-of-store readout (−100…+100) for the
    /// dashboard panel — same normalization as <see cref="PlayerFrame.ErsPercent"/>.</summary>
    public float ErsDeltaPercent => ErsStoreDelta / TelemetryConstants.MaxErsJoules * 100f;
}

/// <summary>Pure functions computing deltas between two driver frames.</summary>
public static class TelemetryComparer
{
    public static TelemetryComparison Compare(PlayerFrame player, PlayerFrame rival)
    {
        var note = player.CarIndex == rival.CarIndex
            ? "No rival selected — showing the same driver twice."
            : null;

        return new TelemetryComparison(
            player.Name,
            rival.Name,
            player.Speed - rival.Speed,
            player.Throttle - rival.Throttle,
            player.Brake - rival.Brake,
            player.ErsStoreEnergy - rival.ErsStoreEnergy,
            player.FuelInTank - rival.FuelInTank,
            0,
            note);
    }

    public static TelemetryComparison WithLapDelta(
        this TelemetryComparison comparison, uint playerBestLapMs, uint rivalBestLapMs)
    {
        var delta = playerBestLapMs == 0 || rivalBestLapMs == 0
            ? 0
            : (int)(rivalBestLapMs - playerBestLapMs); // positive = player is faster
        return comparison with { BestLapDeltaMs = delta };
    }
}