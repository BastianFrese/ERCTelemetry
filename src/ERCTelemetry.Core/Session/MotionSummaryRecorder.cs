namespace ERCTelemetry.Core.Session;

/// <summary>Accumulates the player's high-rate motion data into one per-lap aggregate.
/// <see cref="Sample"/> is called per MotionEx packet (~60 Hz, player only); the window
/// rolls automatically when the car's current lap number advances. The completed lap's
/// aggregate leaves via <see cref="TakeSummary"/> exactly once — the store calls it when
/// that lap's <see cref="LapCompleted"/> fires (before any MotionEx packet of the new lap
/// has been applied, so the window is still the lap that just finished).</summary>
public sealed class MotionSummaryRecorder
{
    private readonly float[] _maxSlipRatio = new float[4];
    private float _minFrontAeroHeight = float.MaxValue;
    private float _minRearAeroHeight = float.MaxValue;
    private float _maxLateralG;
    private float _maxLongitudinalG;
    private byte _lap;
    private bool _hasData;

    /// <summary>Folds one MotionEx sample into the current lap's window. Slip ratios are
    /// absolute (spin-ups and lock-ups both matter); aero heights are minima (bottoming
    /// out); g is |g| from the Motion packet's latest values (quantised /1000).</summary>
    public void Sample(float slipFrontLeft, float slipFrontRight, float slipRearLeft, float slipRearRight,
        float frontAeroHeight, float rearAeroHeight, float gLat, float gLong, byte currentLapNum)
    {
        if (currentLapNum != _lap)
        {
            ResetWindow();
            _lap = currentLapNum;
        }

        _hasData = true;
        _maxSlipRatio[0] = Math.Max(_maxSlipRatio[0], Math.Abs(slipFrontLeft));
        _maxSlipRatio[1] = Math.Max(_maxSlipRatio[1], Math.Abs(slipFrontRight));
        _maxSlipRatio[2] = Math.Max(_maxSlipRatio[2], Math.Abs(slipRearLeft));
        _maxSlipRatio[3] = Math.Max(_maxSlipRatio[3], Math.Abs(slipRearRight));
        _minFrontAeroHeight = Math.Min(_minFrontAeroHeight, frontAeroHeight);
        _minRearAeroHeight = Math.Min(_minRearAeroHeight, rearAeroHeight);
        _maxLateralG = Math.Max(_maxLateralG, Math.Abs(gLat));
        _maxLongitudinalG = Math.Max(_maxLongitudinalG, Math.Abs(gLong));
    }

    /// <summary>Returns and clears the aggregate for <paramref name="lapNumber"/>,
    /// or null when no samples were seen for that lap (e.g. laps before the first
    /// MotionEx packet).</summary>
    public LapMotionSummaryData? TakeSummary(byte lapNumber)
    {
        if (!_hasData || _lap != lapNumber)
        {
            return null;
        }

        var data = new LapMotionSummaryData(
            (float[])_maxSlipRatio.Clone(),
            _minFrontAeroHeight,
            _minRearAeroHeight,
            _maxLateralG,
            _maxLongitudinalG);
        ResetWindow();
        return data;
    }

    public void Reset()
    {
        ResetWindow();
        _lap = 0;
    }

    private void ResetWindow()
    {
        Array.Clear(_maxSlipRatio);
        _minFrontAeroHeight = float.MaxValue;
        _minRearAeroHeight = float.MaxValue;
        _maxLateralG = 0;
        _maxLongitudinalG = 0;
        _hasData = false;
    }
}