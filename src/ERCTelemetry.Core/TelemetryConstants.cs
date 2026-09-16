namespace ERCTelemetry.Core;

/// <summary>Shared protocol constants for the F1 25/26 (2026 Season Pack) telemetry stream.</summary>
public static class TelemetryConstants
{
    /// <summary>Default UDP port the F1 games send telemetry to.</summary>
    public const int DefaultUdpPort = 20777;

    /// <summary>Default local port for the OBS browser-source overlay web server.</summary>
    public const int DefaultOverlayPort = 8090;

    /// <summary>Maximum number of cars the game reports per packet.</summary>
    public const int MaxCars = 22;

    /// <summary>Packet format identifier of the 2026 Season Pack layout that F1Game.UDP 26.x parses.</summary>
    public const int ExpectedPacketFormat = 2026;

    /// <summary>Electric energy store capacity in joules (~4 MJ); ERS values from CarStatus
    /// packets are normalized against this for percentage displays.</summary>
    public const float MaxErsJoules = 4_000_000f;
}