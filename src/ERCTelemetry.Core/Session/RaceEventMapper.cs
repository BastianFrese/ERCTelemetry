using ERCTelemetry.Core.Clips;
using F1Game.UDP.Enums;
using F1Game.UDP.Events;

namespace ERCTelemetry.Core.Session;

/// <summary>Turns raw Event packets into rendered race-control entries with driver names.</summary>
public static class RaceEventMapper
{
    public static RaceEventEntry? Map(EventDetails details, IReadOnlyList<DriverEntry?> drivers) =>
        details switch
        {
            _ when details.TryGetFastestLapEvent(out var fastest) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "FastestLap", fastest.VehicleIdx,
                $"{Name(drivers, fastest.VehicleIdx)} — fastest lap {FormatTime(fastest.LapTime)}"),

            _ when details.TryGetRaceWinnerEvent(out var winner) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "RaceWinner", winner.VehicleIdx,
                $"{Name(drivers, winner.VehicleIdx)} wins the race!"),

            _ when details.TryGetRetirementEvent(out var retirement) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "Retirement", retirement.VehicleIdx,
                $"{Name(drivers, retirement.VehicleIdx)} retired ({retirement.Reason})"),

            // Warnings are their own incident kind (league requirement: Verwarnungen and
            // Strafen must be reviewable separately, per lap and corner).
            _ when details.TryGetPenaltyEvent(out var penalty) => new RaceEventEntry(
                DateTimeOffset.UtcNow,
                penalty.PenaltyType is PenaltyType.Warning ||
                penalty.InfringementType is InfringementType.MultipleWarnings
                    ? "Warning"
                    : "Penalty",
                penalty.VehicleIdx,
                $"{Name(drivers, penalty.VehicleIdx)}: {penalty.PenaltyType}" +
                (penalty.Time > 0 ? $" ({penalty.Time}s)" : string.Empty) +
                $" · {penalty.InfringementType}",
                LapNumber: penalty.LapNum), // the game reports the lap itself

            _ when details.TryGetOvertakeEvent(out var overtake) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "Overtake", overtake.OvertakingVehicleIdx,
                $"{Name(drivers, overtake.OvertakingVehicleIdx)} passed " +
                $"{Name(drivers, overtake.BeingOvertakenVehicleIdx)}"),

            _ when details.TryGetTeamMateInPitsEvent(out var pit) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "TeamMateInPits", pit.VehicleIdx,
                $"{Name(drivers, pit.VehicleIdx)} in the pits"),

            _ when details.TryGetSpeedTrapEvent(out var trap) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "SpeedTrap", trap.VehicleIdx,
                $"{Name(drivers, trap.VehicleIdx)} — {trap.Speed} km/h"),

            _ when details.TryGetSafetyCarEvent(out var sc) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "SafetyCar", null, $"Safety car: {sc.EventType} ({sc.SafetyCarType})"),

            _ when details.TryGetCollisionEvent(out var collision) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "Collision", collision.Vehicle1Index,
                $"{Name(drivers, collision.Vehicle1Index)} collision with " +
                (collision.Vehicle2Index == 255 ? "the environment" : Name(drivers, collision.Vehicle2Index)) +
                $" (impact {CollisionSeverity.Label(collision.Severity)})",
                SecondCarIndex: collision.Vehicle2Index == 255 ? null : collision.Vehicle2Index,
                DetailValue: collision.Severity),

            _ when details.TryGetStartLightsEvent(out var lights) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "StartLights", null, $"Start lights: {lights.NumLights} on"),

            // The lights-out moment is a SEPARATE event (LGOT) from the countdown
            // (STLG, sent once per light as they come on 5→1). F1Game.UDP has no
            // LightsOutEvent struct — it is detected via the EventType alone.
            _ when details.EventType == EventType.LightsOut => new RaceEventEntry(
                DateTimeOffset.UtcNow, "LightsOut", null, "Lights out"),

            _ when details.TryGetDrsDisabledEvent(out var drs) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "DrsDisabled", null, $"DRS disabled ({drs.Reason})"),

            _ when details.TryGetDriveThroughPenaltyServedEvent(out var served) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "DriveThroughServed", served.VehicleIdx,
                $"{Name(drivers, served.VehicleIdx)} served a drive-through"),

            _ when details.TryGetStopGoPenaltyServedEvent(out var stopGo) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "StopGoServed", stopGo.VehicleIdx,
                $"{Name(drivers, stopGo.VehicleIdx)} served a stop-go ({stopGo.StopTime}s)"),

            _ when details.TryGetFlashbackEvent(out var flashback) => new RaceEventEntry(
                DateTimeOffset.UtcNow, "Flashback", null,
                $"Flashback to {TimeSpan.FromSeconds(flashback.FlashbackSessionTime):hh\\:mm\\:ss}"),

            _ => null, // Buttons: not overlay-worthy
        };

    private static string Name(IReadOnlyList<DriverEntry?> drivers, byte carIndex) =>
        carIndex < drivers.Count && drivers[carIndex] is { } driver
            ? driver.Name
            : $"Car {carIndex + 1}";

    /// <summary>Formats a lap time in seconds as m:ss.mmm for event text.</summary>
    public static string FormatTime(float seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }
}