using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Tracks the rival the player is racing against (the car directly ahead, falling
/// back to the car directly behind) and reports only notable actions: a pit stop, a tyre
/// change, or a new best lap. Stateful (single consumer): keeps a rolling window of the
/// rival's last completed laps for the tempo trend and a cooldown so a rival improving
/// their best lap repeatedly cannot spam. Resets on a session change. Pure,
/// headless-testable.</summary>
public sealed class RivalDigestBuilder
{
    /// <summary>Laps to stay silent after an announcement before a fast lap can fire again.
    /// Pit stops and tyre changes are rare and always fire.</summary>
    public const byte CooldownLaps = 3;

    private const int WindowSize = 3;

    private readonly List<uint> _lapTimes = new(WindowSize);
    private uint _lastLapTimeMs;
    private byte _lastPitStops;
    private ActualCompound _lastCompound;
    private uint _lastBestLapMs;
    private byte _lastRivalLap;
    private byte _lastRivalCarIndex = byte.MaxValue;
    private byte _cooldown;
    private ulong _sessionUid;

    /// <summary>Returns a digest for a notable rival action, or null to stay silent.</summary>
    public RivalDigest? Update(TelemetrySnapshot snapshot)
    {
        var player = snapshot.Standings.FirstOrDefault(r => r.IsPlayer);
        if (player is null)
        {
            return null;
        }

        var rival = snapshot.Standings.FirstOrDefault(r => r.Position == player.Position - 1)
                 ?? snapshot.Standings.FirstOrDefault(r => r.Position == player.Position + 1);
        if (rival is null)
        {
            return null;
        }

        // All per-rival baselines are keyed to a specific car. When the player passes (or
        // is passed) the rival switches to a new car whose pit stops / compound / best lap
        // differ — carrying the old car's baselines over would fire a false PitStop /
        // TyreChange alert and a bogus tempo trend, and P4↔P5 position jitter would spam.
        // Re-seed on any rival change, like the session change below.
        var rivalChanged = rival.CarIndex != _lastRivalCarIndex;
        if (snapshot.Meta is { } meta && meta.SessionUid != _sessionUid)
        {
            _sessionUid = meta.SessionUid;
            rivalChanged = true;
        }

        if (rivalChanged)
        {
            _lastRivalCarIndex = rival.CarIndex;
            _lapTimes.Clear();
            _lastLapTimeMs = 0;
            _lastPitStops = rival.NumPitStops;
            _lastCompound = rival.TyreCompound;
            _lastBestLapMs = rival.BestLapTimeMs;
            _lastRivalLap = rival.CurrentLapNum;
            _cooldown = 0;
            return null;
        }

        // Rolling window of the rival's last completed laps (for the tempo trend).
        if (rival.LastLapTimeMs > 0 && rival.LastLapTimeMs != _lastLapTimeMs)
        {
            _lastLapTimeMs = rival.LastLapTimeMs;
            _lapTimes.Add(rival.LastLapTimeMs);
            if (_lapTimes.Count > WindowSize)
            {
                _lapTimes.RemoveAt(0);
            }
        }

        // The cooldown counts down as the rival's lap advances.
        if (rival.CurrentLapNum != _lastRivalLap)
        {
            _lastRivalLap = rival.CurrentLapNum;
            if (_cooldown > 0)
            {
                _cooldown--;
            }
        }

        var pitStopped = rival.NumPitStops > _lastPitStops;
        var tyreChanged = rival.TyreCompound != _lastCompound;
        var fastLap = rival.BestLapTimeMs > 0 && rival.BestLapTimeMs < _lastBestLapMs;

        // Stored state always reflects the current snapshot, so each event fires once.
        _lastPitStops = rival.NumPitStops;
        _lastCompound = rival.TyreCompound;
        _lastBestLapMs = rival.BestLapTimeMs;

        var gapToPlayerMs = rival.Position < player.Position
            ? player.GapToCarInFrontMs
            : rival.GapToCarInFrontMs;

        if (pitStopped)
        {
            _cooldown = CooldownLaps;
            return Digest(rival, RivalEvent.PitStop, gapToPlayerMs);
        }

        if (tyreChanged)
        {
            _cooldown = CooldownLaps;
            return Digest(rival, RivalEvent.TyreChange, gapToPlayerMs);
        }

        if (_cooldown == 0 && fastLap)
        {
            _cooldown = CooldownLaps;
            return Digest(rival, RivalEvent.FastLap, gapToPlayerMs);
        }

        return null;
    }

    private RivalDigest Digest(StandingsRow rival, RivalEvent ev, int gapToPlayerMs) =>
        new(rival.Name, ev, rival.CurrentLapNum, rival.TyreCompound, rival.TyreAgeLaps,
            rival.LastLapTimeMs, rival.BestLapTimeMs, gapToPlayerMs, TrendDelta());

    /// <summary>Per-lap trend from the window: the last lap vs. the first, divided by the
    /// laps between them. Needs at least 3 laps; otherwise 0 (unknown).</summary>
    private float TrendDelta()
    {
        if (_lapTimes.Count < 3)
        {
            return 0f;
        }

        // Cast to int first: uint subtraction wraps on a negative delta (faster laps).
        return ((int)_lapTimes[^1] - (int)_lapTimes[0]) / (float)(_lapTimes.Count - 1);
    }
}
