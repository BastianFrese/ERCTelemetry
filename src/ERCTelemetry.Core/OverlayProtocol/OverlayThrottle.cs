using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.OverlayProtocol;

/// <summary>Rate limiter + change detector for the two overlay data streams. A frozen UI or
/// slow OBS browser source must not get flooded: player ≤30 msg/s, state ≤5 msg/s and only
/// when its (slow-changing) content actually differs from the last accepted snapshot.</summary>
public sealed class OverlayThrottle
{
    /// <summary>Minimum interval between player-stream messages (~30 Hz).</summary>
    public static readonly TimeSpan PlayerInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    /// <summary>Minimum interval between state-stream messages (5 Hz).</summary>
    public static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(1000.0 / 5);

    /// <summary>Minimum interval between minimap-stream messages (10 Hz).</summary>
    public static readonly TimeSpan MapInterval = TimeSpan.FromMilliseconds(1000.0 / 10);

    // Lock instead of racy atomics: losing a ForceState() between connection and pump
    // threads would permanently starve a fresh client of its state sync on a static session.
    private readonly object _gate = new();
    private DateTimeOffset _lastPlayer = DateTimeOffset.MinValue;
    private DateTimeOffset _lastState = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMap = DateTimeOffset.MinValue;
    private TelemetrySnapshot? _lastAcceptedState;
    private bool _forceState;

    /// <summary>True when enough time has passed for the next ~30 Hz player message.</summary>
    public bool ShouldSendPlayer(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _lastPlayer < PlayerInterval)
            {
                return false;
            }

            _lastPlayer = now;
            return true;
        }
    }

    /// <summary>True when a full state sync is wanted (a client just connected) or the
    /// snapshot's session/standings/points content changed and the 5 Hz window has elapsed.
    /// Accepting a send also arms the window and records what was last delivered.</summary>
    public bool ShouldSendState(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = !snapshot.DeepEqualsForOverlay(_lastAcceptedState);
            if (_forceState || (changed && now - _lastState >= StateInterval))
            {
                _lastState = now;
                _lastAcceptedState = snapshot;
                _forceState = false;
                return true;
            }

            return false;
        }
    }

    /// <summary>True when the 10 Hz minimap window has elapsed. Positions move constantly
    /// while driving; the "nothing to send" case is handled by the caller (null frame).</summary>
    public bool ShouldSendMap(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _lastMap < MapInterval)
            {
                return false;
            }

            _lastMap = now;
            return true;
        }
    }

    /// <summary>Makes the next state message go out immediately regardless of the 5 Hz timer
    /// (used when a client connects and must not wait for slow-changing content to differ).</summary>
    public void ForceState()
    {
        lock (_gate)
        {
            _forceState = true;
        }
    }
}

/// <summary>Value comparison of the overlay-relevant (slow) parts of a snapshot. Player/rival
/// frames move at 60 Hz and are deliberately excluded — they stream at 30 Hz via the player
/// message, not the state message.</summary>
public static class OverlayStateComparer
{
    public static bool DeepEqualsForOverlay(
        this TelemetrySnapshot snapshot, TelemetrySnapshot? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(snapshot, other))
        {
            return true;
        }

        return Equals(snapshot.Meta, other.Meta)
               && SequenceEqual(snapshot.Drivers, other.Drivers)
               && SequenceEqual(snapshot.Standings, other.Standings)
               && SequenceEqual(snapshot.FinalResults, other.FinalResults)
               && Equals(snapshot.Strategy, other.Strategy)
               && Equals(snapshot.Fuel, other.Fuel)
               && SequenceEqual(snapshot.Tyres ?? Array.Empty<TyreStatus>(),
                   other.Tyres ?? Array.Empty<TyreStatus>())
               && SequenceEqual(snapshot.H2h ?? Array.Empty<H2hTally>(),
                   other.H2h ?? Array.Empty<H2hTally>());
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
        where T : IEquatable<T>
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }
}