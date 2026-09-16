using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.Core.VoiceAlerts;

/// <summary>AC-style proximity spotter: announces the cars around the player by direction
/// (left/right/behind/ahead), count and distance, plus "clear" when a zone empties — the
/// precision a beginner needs to learn where the field is. Stateful (single consumer):
/// tracks which cars are in which zone with a debounce so position jitter never causes
/// chatter. Pure, headless-testable — the App feeds it periodic snapshots and speaks the
/// returned strings. Language follows <see cref="Language"/>.</summary>
public sealed class ProximitySpotter
{
    // Zone geometry (metres, relative to the player's heading). Alongside takes priority:
    // a car at −8 m / 4 m is "left", not "behind".
    private const float AlongsideLateralMetres = 2f;  // |lateral| ≥ this = beside you
    private const float AlongsideRangeMetres = 10f;   // |longitudinal| ≤ this = beside you
    private const float BehindRangeMetres = 2f;       // longitudinal < −this = behind
    private const float AheadRangeMetres = 2f;        // longitudinal > this = ahead
    private const float ZoneLateralMetres = 8f;      // |lateral| < this for behind/ahead

    /// <summary>Max distance (metres) at which a car is announced at all — the beginner
    /// learns about the cars right around them, not the field 200 m away.</summary>
    private const float SpotterRangeMetres = 20f;

    /// <summary>Consecutive checks a car must be in a zone before it is announced —
    /// filters out position jitter at the zone boundary.</summary>
    private const int DebounceChecks = 2;

    /// <summary>Distance steps (metres) at which a closing car is re-announced, so the
    /// beginner learns the gap shrinking (20 → 10 → 5 m).</summary>
    private static readonly int[] DistanceSteps = [20, 10, 5];

    private enum Zone { Left, Right, Behind, Ahead }

    private readonly HashSet<byte>[] _confirmed = [new(), new(), new(), new()];
    private readonly Dictionary<byte, int>[] _pending = [new(), new(), new(), new()];
    private readonly bool[] _wasNonEmpty = [false, false, false, false];
    private readonly int[] _nextDistanceStep = [0, 0, 0, 0]; // per zone: next step to announce
    private byte _playerIndex = 255; // 255 = unknown (no session yet)

    /// <summary>Language of the announcements. Set by the App from the settings before each
    /// update; defaults to German.</summary>
    public VoiceLanguage Language { get; set; } = VoiceLanguage.German;

    /// <summary>Classifies the cars around the player and returns the announcements for
    /// this check (usually 0–2). A car must be in a zone for <see cref="DebounceChecks"/>
    /// consecutive checks before it is announced; a zone that empties announces "clear".
    /// Returns an empty list before the first Motion packet.</summary>
    public IReadOnlyList<string> Update(
        MotionFrame? positions,
        byte playerIndex,
        IReadOnlyList<StandingsRow> standings)
    {
        var alerts = new List<string>(2);
        if (positions is null)
        {
            return alerts;
        }

        if (_playerIndex != playerIndex)
        {
            _playerIndex = playerIndex;
            Reset();
        }

        var cars = BlindSpotCalculator.Compute(positions, playerIndex, standings);
        var inZone = new HashSet<byte>[4];
        for (var z = 0; z < 4; z++)
        {
            inZone[z] = new HashSet<byte>();
        }

        foreach (var car in cars)
        {
            if (Classify(car.LateralMetres, car.LongitudinalMetres) is { } zone)
            {
                inZone[(int)zone].Add(car.CarIndex);
            }
        }

        for (var z = 0; z < 4; z++)
        {
            var zone = (Zone)z;
            var confirmed = _confirmed[z];
            var pending = _pending[z];

            // Cars that left the zone drop out of confirmed + pending immediately.
            foreach (var car in confirmed.ToList())
            {
                if (!inZone[z].Contains(car))
                {
                    confirmed.Remove(car);
                }
            }

            foreach (var car in pending.Keys.ToList())
            {
                if (!inZone[z].Contains(car))
                {
                    pending.Remove(car);
                }
            }

            // New cars in the zone count consecutive checks before being announced.
            var entered = new List<byte>();
            foreach (var car in inZone[z])
            {
                if (confirmed.Contains(car))
                {
                    continue;
                }

                pending.TryGetValue(car, out var count);
                pending[car] = count + 1;
                if (count + 1 >= DebounceChecks)
                {
                    pending.Remove(car);
                    confirmed.Add(car);
                    entered.Add(car);
                }
            }

            // A zone that was announced and is now empty says "clear".
            if (_wasNonEmpty[z] && confirmed.Count == 0)
            {
                alerts.Add(ClearText(zone));
                _nextDistanceStep[z] = 0;
            }

            _wasNonEmpty[z] = confirmed.Count > 0;

            if (entered.Count > 0)
            {
                AddEntryAlert(alerts, zone, cars, confirmed);
            }

            // A closing car re-announces when it crosses the next distance step.
            if (zone is Zone.Behind or Zone.Ahead && confirmed.Count > 0)
            {
                var closest = ClosestDistance(zone, cars, confirmed);
                var step = _nextDistanceStep[z];
                if (step > 0 && closest < step)
                {
                    alerts.Add(DistanceText(zone, confirmed.Count, step));
                    _nextDistanceStep[z] = NextLowerStep(step);
                }
            }
        }

        return alerts;
    }

    private void AddEntryAlert(
        List<string> alerts,
        Zone zone,
        IReadOnlyList<BlindSpotCar> cars,
        HashSet<byte> confirmed)
    {
        if (zone is Zone.Behind or Zone.Ahead)
        {
            var closest = ClosestDistance(zone, cars, confirmed);
            _nextDistanceStep[(int)zone] = ArmStep(closest);
            alerts.Add(DistanceText(zone, confirmed.Count, RoundDistance(closest)));
            return;
        }

        alerts.Add(ZoneText(zone, confirmed.Count));
    }

    private static Zone? Classify(float lateral, float longitudinal)
    {
        // Only cars within the spotter's range matter — a car 150 m behind is not a
        // reaction the beginner needs to learn yet.
        if (MathF.Sqrt(lateral * lateral + longitudinal * longitudinal) > SpotterRangeMetres)
        {
            return null;
        }

        if (MathF.Abs(lateral) >= AlongsideLateralMetres && MathF.Abs(longitudinal) <= AlongsideRangeMetres)
        {
            return lateral < 0 ? Zone.Left : Zone.Right;
        }

        if (longitudinal < -BehindRangeMetres && MathF.Abs(lateral) < ZoneLateralMetres)
        {
            return Zone.Behind;
        }

        if (longitudinal > AheadRangeMetres && MathF.Abs(lateral) < ZoneLateralMetres)
        {
            return Zone.Ahead;
        }

        return null;
    }

    /// <summary>Distance of the closest confirmed car in the zone (positive metres).</summary>
    private static float ClosestDistance(Zone zone, IReadOnlyList<BlindSpotCar> cars, HashSet<byte> confirmed)
    {
        var best = float.MaxValue;
        foreach (var car in cars)
        {
            if (!confirmed.Contains(car.CarIndex))
            {
                continue;
            }

            var dist = zone == Zone.Behind ? -car.LongitudinalMetres : car.LongitudinalMetres;
            if (dist < best)
            {
                best = dist;
            }
        }

        return best;
    }

    /// <summary>Rounds a distance to the nearest 5 m (min 5) for the spoken call — the
    /// spotter's 20 m range is too tight for 10 m steps.</summary>
    private static int RoundDistance(float metres) =>
        Math.Max(5, (int)Math.Round(metres / 5.0, MidpointRounding.AwayFromZero) * 5);

    /// <summary>Arms the highest distance step strictly below the current distance — the
    /// next step the closing car will cross.</summary>
    private static int ArmStep(float distance)
    {
        foreach (var step in DistanceSteps)
        {
            if (distance > step)
            {
                return step;
            }
        }

        return 0;
    }

    private static int NextLowerStep(int step)
    {
        for (var i = 0; i < DistanceSteps.Length; i++)
        {
            if (DistanceSteps[i] == step)
            {
                return i + 1 < DistanceSteps.Length ? DistanceSteps[i + 1] : 0;
            }
        }

        return 0;
    }

    private string ZoneText(Zone zone, int count)
    {
        var english = Language == VoiceLanguage.English;
        return zone switch
        {
            Zone.Left => count == 1 ? (english ? "Car left" : "Auto links")
                : $"{count} {(english ? "cars left" : "Autos links")}",
            Zone.Right => count == 1 ? (english ? "Car right" : "Auto rechts")
                : $"{count} {(english ? "cars right" : "Autos rechts")}",
            Zone.Behind => count == 1 ? (english ? "Car behind" : "Auto hinter dir")
                : $"{count} {(english ? "cars behind" : "Autos hinter dir")}",
            _ => count == 1 ? (english ? "Car ahead" : "Auto vor dir")
                : $"{count} {(english ? "cars ahead" : "Autos vor dir")}",
        };
    }

    private string DistanceText(Zone zone, int count, int metres)
    {
        var english = Language == VoiceLanguage.English;
        return $"{ZoneText(zone, count)}, {metres} {(english ? "metres" : "Meter")}";
    }

    private string ClearText(Zone zone)
    {
        var english = Language == VoiceLanguage.English;
        return zone switch
        {
            Zone.Left => english ? "Clear left" : "Links frei",
            Zone.Right => english ? "Clear right" : "Rechts frei",
            Zone.Behind => english ? "Clear behind" : "Hinten frei",
            _ => english ? "Clear ahead" : "Vorn frei",
        };
    }

    /// <summary>Clears all zone state — called when the player car index changes and by
    /// the App when the session ends, so a finished race cannot keep announcing stale
    /// zones (the game keeps sending motion packets during the cool-down lap).</summary>
    public void Reset()
    {
        foreach (var set in _confirmed)
        {
            set.Clear();
        }

        foreach (var dict in _pending)
        {
            dict.Clear();
        }

        Array.Clear(_wasNonEmpty);
        Array.Clear(_nextDistanceStep);
    }
}
