namespace ERCTelemetry.Core.Analysis;

using ERCTelemetry.Core.Session;

/// <summary>One car inside the player's radar cone: alongside or just behind/ahead.
/// <see cref="LateralMetres"/> is positive when the car is to the player's right,
/// <see cref="LongitudinalMetres"/> is negative when it is behind. <see cref="GapMs"/>
/// is the race time gap to the player (0 = unknown, e.g. outside a race).</summary>
public sealed record BlindSpotCar(
    byte CarIndex,
    bool IsLeft,
    float LateralMetres,
    float LongitudinalMetres,
    int GapMs);

/// <summary>360° proximity-radar geometry (Assetto-Corsa style): transforms the other
/// cars' world positions into the player's view (forward/lateral components from the
/// player's Motion forward vector) and keeps every car within the radar radius, in all
/// directions. Pure math, no state.</summary>
public static class BlindSpotCalculator
{
    /// <summary>How far the radar reaches in every direction (metres) — the AC-style
    /// circular radar shows cars up to this distance around the player.</summary>
    internal const float RadarRangeMetres = 200f;
    /// <summary>Alert window: the classic "within one second" battle gap.</summary>
    public const int AlertGapMs = 1000;
    /// <summary>Below this forward-vector length the heading is not usable yet.</summary>
    internal const float MinForwardLength = 0.05f;

    /// <summary>All cars inside the player's radar radius, nearest-relevant first.
    /// Cars with an unreported (0,0) Motion slot are skipped, as is the player itself.
    /// Returns an empty list before the first Motion packet or with a degenerate
    /// player heading.</summary>
    public static IReadOnlyList<BlindSpotCar> Compute(
        MotionFrame? positions,
        byte playerIndex,
        IReadOnlyList<StandingsRow> standings)
    {
        if (positions is null || playerIndex >= positions.Count)
        {
            return [];
        }

        var fx = positions.ForwardX[playerIndex];
        var fz = positions.ForwardZ[playerIndex];
        var length = MathF.Sqrt(fx * fx + fz * fz);
        if (length < MinForwardLength)
        {
            return [];
        }

        fx /= length;
        fz /= length;
        // Driver's right in the game's world. A live check showed the common formula
        // right = up × forward = (fz, −fx) actually points LEFT here (the radar was
        // horizontally mirrored), so right = forward × up = (−fz, fx). Facing +Z the
        // right vector is −X.
        var rx = -fz;
        var rz = fx;

        var px = positions.X[playerIndex];
        var pz = positions.Z[playerIndex];
        var gaps = GapToPlayerByCar(standings, playerIndex);

        var result = new List<BlindSpotCar>();
        for (byte i = 0; i < positions.Count; i++)
        {
            // The player itself and unreported slots (zeroed by the game) are skipped.
            if (i == playerIndex || (positions.X[i] == 0 && positions.Z[i] == 0))
            {
                continue;
            }

            var dx = positions.X[i] - px;
            var dz = positions.Z[i] - pz;
            var longitudinal = dx * fx + dz * fz;
            var lateral = dx * rx + dz * rz;

            if (longitudinal * longitudinal + lateral * lateral > RadarRangeMetres * RadarRangeMetres)
            {
                continue;
            }

            gaps.TryGetValue(i, out var gap);
            result.Add(new BlindSpotCar(i, lateral < 0, lateral, longitudinal, gap));
        }

        // Closest threat first: most behind matters more than most ahead.
        result.Sort((a, b) => a.LongitudinalMetres.CompareTo(b.LongitudinalMetres));
        return result;
    }

    /// <summary>Time gap of each car to the player, walked along the standings chain:
    /// the car directly behind the player reports its gap to the car in front (the
    /// player), the next one sums both hops. Gaps ≤ 0 mean "unknown" and break the
    /// chain — anything further back is dropped. Returns an empty map when no gaps
    /// are known (quali/practice).</summary>
    internal static Dictionary<byte, int> GapToPlayerByCar(IReadOnlyList<StandingsRow> standings, byte playerIndex)
    {
        var playerPos = 0;
        while (playerPos < standings.Count && standings[playerPos].CarIndex != playerIndex)
        {
            playerPos++;
        }

        var gaps = new Dictionary<byte, int>();
        if (playerPos >= standings.Count)
        {
            return gaps;
        }

        var cumulative = 0;
        for (var pos = playerPos + 1; pos < standings.Count; pos++)
        {
            var row = standings[pos];
            if (row.GapToCarInFrontMs <= 0)
            {
                break; // unknown link — gaps beyond it are meaningless
            }

            cumulative += row.GapToCarInFrontMs;
            gaps[row.CarIndex] = cumulative;
        }

        return gaps;
    }
}