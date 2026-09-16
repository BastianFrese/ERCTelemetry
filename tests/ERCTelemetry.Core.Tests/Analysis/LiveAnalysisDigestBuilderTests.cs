using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>LiveAnalysisDigestBuilder: rolling lap-time window, trend detection, tyre wear
/// and fuel projection from periodic snapshots. The builder is stateful (single consumer),
/// so tests feed it a sequence of snapshots like the App loop does.</summary>
public sealed class LiveAnalysisDigestBuilderTests
{
    [Fact]
    public void Build_returns_null_without_meta()
    {
        var builder = new LiveAnalysisDigestBuilder();
        var snapshot = new TelemetrySnapshot(null, [], [], [], [], null, null, 1, DateTimeOffset.UtcNow);

        var digest = builder.Build(snapshot);

        Assert.Null(digest);
    }

    [Fact]
    public void Build_returns_null_without_player_row()
    {
        var builder = new LiveAnalysisDigestBuilder();
        var row = new StandingsRow(1, 1, "Bob", Team.Ferrari, 2, 1, 0, 0, 0, 0,
            PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 1, false);
        var snapshot = new TelemetrySnapshot(Meta(), [], [row], [], [], null, null, 1, DateTimeOffset.UtcNow);

        var digest = builder.Build(snapshot);

        Assert.Null(digest);
    }

    [Fact]
    public void Rolling_window_appends_only_new_lap_completions()
    {
        var builder = new LiveAnalysisDigestBuilder();

        builder.Build(Snapshot(lap: 1, lastLapMs: 90_000));
        builder.Build(Snapshot(lap: 1, lastLapMs: 90_000)); // same lap — no append
        var digest = builder.Build(Snapshot(lap: 2, lastLapMs: 91_000));

        Assert.Equal([90_000u, 91_000u], digest!.LastLapTimesMs);
    }

    [Fact]
    public void Rolling_window_keeps_last_five_laps()
    {
        var builder = new LiveAnalysisDigestBuilder();
        for (byte lap = 1; lap <= 6; lap++)
        {
            builder.Build(Snapshot(lap: lap, lastLapMs: (uint)(90_000 + lap * 1_000)));
        }

        var digest = builder.Build(Snapshot(lap: 7, lastLapMs: 97_000));

        Assert.Equal([93_000u, 94_000u, 95_000u, 96_000u, 97_000u], digest!.LastLapTimesMs);
    }

    [Fact]
    public void Trend_is_slower_when_laps_get_slower()
    {
        var builder = new LiveAnalysisDigestBuilder();
        for (byte lap = 1; lap <= 5; lap++)
        {
            builder.Build(Snapshot(lap: lap, lastLapMs: (uint)(90_000 + lap * 1_000)));
        }

        var digest = builder.Build(Snapshot(lap: 6, lastLapMs: 96_000));

        Assert.Equal(LapTrend.Slower, digest!.Trend);
        Assert.Equal(1_000f, digest.TrendDeltaMsPerLap);
    }

    [Fact]
    public void Trend_is_faster_when_laps_get_faster()
    {
        var builder = new LiveAnalysisDigestBuilder();
        for (byte lap = 1; lap <= 5; lap++)
        {
            builder.Build(Snapshot(lap: lap, lastLapMs: (uint)(94_000 - lap * 1_000)));
        }

        var digest = builder.Build(Snapshot(lap: 6, lastLapMs: 88_000));

        Assert.Equal(LapTrend.Faster, digest!.Trend);
        Assert.Equal(-1_000f, digest.TrendDeltaMsPerLap);
    }

    [Fact]
    public void Trend_is_stable_below_threshold()
    {
        var builder = new LiveAnalysisDigestBuilder();
        builder.Build(Snapshot(lap: 1, lastLapMs: 90_000));
        builder.Build(Snapshot(lap: 2, lastLapMs: 90_100));
        var digest = builder.Build(Snapshot(lap: 3, lastLapMs: 90_200));

        Assert.Equal(LapTrend.Stable, digest!.Trend);
        Assert.Equal(100f, digest.TrendDeltaMsPerLap);
    }

    [Fact]
    public void Trend_is_stable_with_fewer_than_three_laps()
    {
        var builder = new LiveAnalysisDigestBuilder();
        builder.Build(Snapshot(lap: 1, lastLapMs: 90_000));
        var digest = builder.Build(Snapshot(lap: 2, lastLapMs: 95_000));

        Assert.Equal(LapTrend.Stable, digest!.Trend);
        Assert.Equal(0f, digest.TrendDeltaMsPerLap);
    }

    [Fact]
    public void Tyre_wear_comes_from_tyre_status()
    {
        var builder = new LiveAnalysisDigestBuilder();

        var digest = builder.Build(Snapshot(lap: 5, lastLapMs: 90_000, tyreWear: 70f));

        Assert.Equal(70f, digest!.TyreWearPercent);
    }

    [Fact]
    public void Tyre_wear_is_zero_without_tyre_status()
    {
        var builder = new LiveAnalysisDigestBuilder();
        var snapshot = Snapshot(lap: 5, lastLapMs: 90_000) with { Tyres = null };

        var digest = builder.Build(snapshot);

        Assert.Equal(0f, digest!.TyreWearPercent);
    }

    [Fact]
    public void Fuel_projection_is_shortfall_when_tank_insufficient()
    {
        var builder = new LiveAnalysisDigestBuilder();

        // Lap 20 of 30 → 10 laps to go; 10 l − 2 l/lap × 10 = −10 l.
        var digest = builder.Build(Snapshot(lap: 20, lastLapMs: 90_000, fuelInTank: 10f, fuelUsedLastLap: 2f));

        Assert.Equal(-10f, digest!.ProjectedFuelAtEnd);
    }

    [Fact]
    public void Fuel_projection_is_positive_when_tank_sufficient()
    {
        var builder = new LiveAnalysisDigestBuilder();

        // Lap 20 of 30 → 10 laps to go; 50 l − 2 l/lap × 10 = +30 l.
        var digest = builder.Build(Snapshot(lap: 20, lastLapMs: 90_000, fuelInTank: 50f, fuelUsedLastLap: 2f));

        Assert.Equal(30f, digest!.ProjectedFuelAtEnd);
    }

    [Fact]
    public void Session_change_resets_the_window()
    {
        var builder = new LiveAnalysisDigestBuilder();
        builder.Build(Snapshot(sessionUid: 1, lap: 1, lastLapMs: 90_000));
        builder.Build(Snapshot(sessionUid: 1, lap: 2, lastLapMs: 91_000));

        var digest = builder.Build(Snapshot(sessionUid: 2, lap: 1, lastLapMs: 95_000));

        Assert.Equal([95_000u], digest!.LastLapTimesMs);
    }

    private static SessionMeta Meta(ulong sessionUid = 1, byte totalLaps = 30) =>
        new(sessionUid, SessionType.Race, Track.Bahrain, totalLaps, 5412, true, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);

    private static TelemetrySnapshot Snapshot(
        ulong sessionUid = 1,
        byte lap = 1,
        uint lastLapMs = 0,
        byte totalLaps = 30,
        float fuelInTank = 50f,
        float fuelRemainingLaps = 20f,
        float fuelUsedLastLap = 2f,
        float tyreWear = 0f,
        ActualCompound compound = ActualCompound.F1C3,
        byte tyreAge = 1,
        byte position = 1,
        int gapToLeader = 0)
    {
        var row = new StandingsRow(position, 0, "Alice", Team.McLaren, 1, lap, lastLapMs, 0,
            gapToLeader, 0, PitStatus.None, 0, ResultStatus.Active, compound, tyreAge, true,
            FuelUsedLastLap: fuelUsedLastLap);
        var player = new PlayerFrame(0, "Alice", 300, 8, 0.5f, 0f, 12_000, 3_000_000,
            ErsDeployMode.Medium, fuelInTank, fuelRemainingLaps, compound, tyreAge, true, false);
        var tyres = new List<TyreStatus> { new(0, tyreWear, 0f) };
        return new TelemetrySnapshot(Meta(sessionUid, totalLaps), [], [row], [], [], player, null,
            1, DateTimeOffset.UtcNow, Tyres: tyres);
    }
}
