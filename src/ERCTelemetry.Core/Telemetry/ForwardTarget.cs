namespace ERCTelemetry.Core.Telemetry;

/// <summary>One UDP forwarding destination — another telemetry app on this machine
/// (e.g. RaceLab, SimHub) that parses the same raw datagrams the game sends to this app.
/// Immutable record — change values via <c>with</c>.</summary>
public sealed record ForwardTarget
{
    /// <summary>Destination IP or hostname (loopback for apps on the same PC).</summary>
    public string Address { get; init; } = "127.0.0.1";

    /// <summary>Destination UDP port the receiving app listens on.</summary>
    public int Port { get; init; } = 20777;

    /// <summary>False = this target is skipped while forwarding stays on.</summary>
    public bool Enabled { get; init; } = true;
}
