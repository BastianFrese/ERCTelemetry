using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.Analysis;

/// <summary>What the pit-stop advisor recommends right now.</summary>
public enum PitStopRecommendation { KeepGoing, PitInLap, PitNow }

/// <summary>Pit-stop timing advice for the player: which lap to box, or whether to keep
/// going. <see cref="Reason"/> is the German voice line. Deterministic (Layer 1) — no LLM,
/// works offline.</summary>
public sealed record PitStopAdvice(
    PitStopRecommendation Recommendation,
    ushort SuggestedPitLap,   // 0 = none
    string Reason);           // German voice line

/// <summary>Recommends the pit lap from the tyre-degradation curve (wear % + age → projected
/// loss), the tank (remaining laps) and the track position (gap to the car behind — the
/// undercut window). Builds on <see cref="StrategyAdvisor"/> and <see cref="FuelCalculator"/>
/// (DRY). Stateful (single consumer): speaks on recommendation transitions, once per stint
/// (reset on compound change). Pure, headless-testable.</summary>
public sealed class PitStopAdvisor
{
    /// <summary>Gap (ms) to the car behind at which an undercut is worth considering.</summary>
    public const int UndercutGapMs = 1500;

    /// <summary>Laps of tyre life left at which the undercut window opens.</summary>
    public const int UndercutTyreLapsLeft = 3;

    /// <summary>Laps until the suggested pit lap at which the advisor says "pit now".</summary>
    public const int PitNowWindowLaps = 1;

    private ActualCompound _lastCompound;
    private PitStopRecommendation? _lastRecommendation;

    /// <summary>Returns the advice to speak now, or null to stay silent. Speaks on every
    /// transition (keep going → box in lap X → pit now) and once per stint — a compound
    /// change re-arms it.</summary>
    public PitStopAdvice? Evaluate(LiveAnalysisDigest digest, int gapToCarBehindMs)
    {
        if (digest.TyreCompound != _lastCompound)
        {
            _lastCompound = digest.TyreCompound;
            _lastRecommendation = null;
        }

        // Wear-per-lap from the stint average — the digest has no trend, only age + wear.
        var wearPerLap = digest.TyreAgeLaps > 0 ? digest.TyreWearPercent / digest.TyreAgeLaps : 0f;
        var advice = Compute(
            digest.Lap, digest.TotalLaps,
            digest.TyreWearPercent, wearPerLap,
            digest.FuelRemainingLaps, digest.FuelUsedLastLap,
            gapToCarBehindMs);

        if (advice.Recommendation == _lastRecommendation)
        {
            return null;
        }

        _lastRecommendation = advice.Recommendation;
        return advice.Recommendation == PitStopRecommendation.KeepGoing ? null : advice;
    }

    /// <summary>Pure recommendation from the current state. Fuel emergency overrides
    /// everything; otherwise the tyre curve drives the pit lap, bounded by the race length,
    /// with the undercut window (close car behind + tyres nearly done) pulling the stop
    /// forward.</summary>
    public static PitStopAdvice Compute(
        byte currentLap,
        byte totalLaps,
        float wearNow,
        float wearPerLap,
        float fuelLapsRemaining,
        float fuelPerLap,
        int gapToCarBehindMs)
    {
        var lapsToGo = Math.Max(0, totalLaps - currentLap);
        var shortfall = FuelCalculator.Shortfall(lapsToGo, fuelLapsRemaining);
        if (shortfall is > 0)
        {
            return new PitStopAdvice(PitStopRecommendation.PitNow, 0, "Tank-Notfall — du musst bald rein.");
        }

        var strategy = StrategyAdvisor.Compute(0, 0, fuelPerLap, wearNow, wearPerLap, currentLap);
        if (strategy.TyreLapsLeft < 0)
        {
            return new PitStopAdvice(PitStopRecommendation.KeepGoing, 0, "Weiter fahren.");
        }

        if (strategy.TyreLapsLeft <= 0)
        {
            return new PitStopAdvice(PitStopRecommendation.PitNow, 0, "Reifen sind durch — jetzt rein.");
        }

        if (strategy.SuggestedPitLap == 0)
        {
            return new PitStopAdvice(PitStopRecommendation.KeepGoing, 0, "Weiter fahren.");
        }

        if (strategy.SuggestedPitLap >= totalLaps)
        {
            return new PitStopAdvice(PitStopRecommendation.KeepGoing, 0, "Reifen halten bis zum Ende.");
        }

        if (gapToCarBehindMs is > 0 and < UndercutGapMs && strategy.TyreLapsLeft <= UndercutTyreLapsLeft)
        {
            return new PitStopAdvice(PitStopRecommendation.PitNow, 0, "Undercut-Fenster — Auto hinter dir ist nah.");
        }

        if (strategy.SuggestedPitLap - currentLap <= PitNowWindowLaps)
        {
            return new PitStopAdvice(PitStopRecommendation.PitNow, strategy.SuggestedPitLap, "Boxenfenster jetzt.");
        }

        return new PitStopAdvice(
            PitStopRecommendation.PitInLap, strategy.SuggestedPitLap, $"Box in Runde {strategy.SuggestedPitLap}.");
    }

    public void Reset()
    {
        _lastCompound = default;
        _lastRecommendation = null;
    }
}
