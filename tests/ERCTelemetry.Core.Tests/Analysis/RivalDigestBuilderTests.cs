using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>RivalDigestBuilder: tracks the rival the player is racing against (car ahead,
/// else car behind) and reports only notable actions — pit stop, tyre change, new best
/// lap — with a cooldown so fast laps cannot spam. Stateful (single consumer), so tests
/// feed it a sequence of snapshots like the App loop does.</summary>
public sealed class RivalDigestBuilderTests
{
    [Fact]
    public void Pit_stop_fires_when_pit_stops_increase()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalPitStops: 0));

        var digest = builder.Update(Snapshot(rivalPitStops: 1));

        Assert.Equal(RivalEvent.PitStop, digest!.Event);
        Assert.Equal("Bob", digest.RivalName);
        Assert.Equal(1, digest.Lap);
    }

    [Fact]
    public void Tyre_change_fires_when_compound_changes()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalCompound: ActualCompound.F1C3));

        var digest = builder.Update(Snapshot(rivalCompound: ActualCompound.F1C2));

        Assert.Equal(RivalEvent.TyreChange, digest!.Event);
        Assert.Equal(ActualCompound.F1C2, digest.Compound);
    }

    [Fact]
    public void Fast_lap_fires_when_best_lap_improves()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalBestLapMs: 90_000));

        var digest = builder.Update(Snapshot(rivalBestLapMs: 89_000));

        Assert.Equal(RivalEvent.FastLap, digest!.Event);
        Assert.Equal(89_000u, digest.BestLapTimeMs);
    }

    [Fact]
    public void Fast_lap_has_cooldown()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalLap: 1, rivalBestLapMs: 90_000));
        var first = builder.Update(Snapshot(rivalLap: 1, rivalBestLapMs: 89_000)); // fires, cooldown 3

        var lap2 = builder.Update(Snapshot(rivalLap: 2, rivalBestLapMs: 88_000)); // cooldown 2
        var lap3 = builder.Update(Snapshot(rivalLap: 3, rivalBestLapMs: 87_000)); // cooldown 1
        var lap4 = builder.Update(Snapshot(rivalLap: 4, rivalBestLapMs: 86_000)); // cooldown 0 → fires

        Assert.Equal(RivalEvent.FastLap, first!.Event);
        Assert.Null(lap2);
        Assert.Null(lap3);
        Assert.Equal(RivalEvent.FastLap, lap4!.Event);
    }

    [Fact]
    public void Pit_stop_fires_during_cooldown()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalLap: 1, rivalBestLapMs: 90_000));
        builder.Update(Snapshot(rivalLap: 1, rivalBestLapMs: 89_000)); // fast lap, cooldown 3

        var digest = builder.Update(Snapshot(rivalLap: 2, rivalBestLapMs: 88_000, rivalPitStops: 1));

        Assert.Equal(RivalEvent.PitStop, digest!.Event);
    }

    [Fact]
    public void Rival_is_car_ahead_preferred()
    {
        var builder = new RivalDigestBuilder();
        // Player P2; car ahead (P1) pits, car behind (P3) does not.
        builder.Update(Snapshot(playerPosition: 2, rivalPosition: 1, rivalName: "Ahead", rivalPitStops: 0));

        var digest = builder.Update(Snapshot(playerPosition: 2, rivalPosition: 1, rivalName: "Ahead", rivalPitStops: 1));

        Assert.Equal(RivalEvent.PitStop, digest!.Event);
        Assert.Equal("Ahead", digest.RivalName);
    }

    [Fact]
    public void Gap_to_player_from_car_ahead()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(playerPosition: 2, rivalPosition: 1, playerGapToCarInFront: 5_000));
        builder.Update(Snapshot(playerPosition: 2, rivalPosition: 1, playerGapToCarInFront: 5_000, rivalBestLapMs: 90_000));

        var digest = builder.Update(Snapshot(playerPosition: 2, rivalPosition: 1, playerGapToCarInFront: 5_000, rivalBestLapMs: 89_000));

        Assert.Equal(5_000, digest!.GapToPlayerMs);
    }

    [Fact]
    public void Gap_to_player_from_car_behind()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(playerPosition: 1, rivalPosition: 2, rivalGapToCarInFront: 3_000));
        builder.Update(Snapshot(playerPosition: 1, rivalPosition: 2, rivalGapToCarInFront: 3_000, rivalBestLapMs: 90_000));

        var digest = builder.Update(Snapshot(playerPosition: 1, rivalPosition: 2, rivalGapToCarInFront: 3_000, rivalBestLapMs: 89_000));

        Assert.Equal(3_000, digest!.GapToPlayerMs);
    }

    [Fact]
    public void Trend_delta_from_rolling_window()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalLap: 1, rivalLastLapMs: 90_000));
        builder.Update(Snapshot(rivalLap: 2, rivalLastLapMs: 91_000));
        builder.Update(Snapshot(rivalLap: 3, rivalLastLapMs: 92_000));

        var digest = builder.Update(Snapshot(rivalLap: 4, rivalLastLapMs: 93_000, rivalPitStops: 1));

        Assert.Equal(1_000f, digest!.TrendDeltaMsPerLap);
    }

    [Fact]
    public void Session_change_resets_state()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(sessionUid: 1, rivalBestLapMs: 90_000));
        builder.Update(Snapshot(sessionUid: 1, rivalBestLapMs: 89_000)); // fast lap in session 1

        var session2Baseline = builder.Update(Snapshot(sessionUid: 2, rivalBestLapMs: 90_000));
        var session2Fast = builder.Update(Snapshot(sessionUid: 2, rivalBestLapMs: 89_000));

        Assert.Null(session2Baseline);
        Assert.Equal(RivalEvent.FastLap, session2Fast!.Event);
    }

    [Fact]
    public void Rival_car_change_reseeds_baselines()
    {
        var builder = new RivalDigestBuilder();
        builder.Update(Snapshot(rivalCarIndex: 1, rivalPitStops: 0));

        // The P2 slot now holds a different car (player passed/passed). Its 2 pit stops
        // and F1C2 compound are that car's baseline — carrying the old car's 0 / F1C3
        // over would fire a false PitStop/TyreChange alert the moment the new rival
        // appears.
        var digest = builder.Update(Snapshot(rivalCarIndex: 2, rivalPitStops: 2, rivalCompound: ActualCompound.F1C2));

        Assert.Null(digest);

        // The new rival's own improvement still fires normally afterwards.
        builder.Update(Snapshot(rivalCarIndex: 2, rivalPitStops: 2, rivalCompound: ActualCompound.F1C2, rivalBestLapMs: 90_000));
        var fastLap = builder.Update(Snapshot(rivalCarIndex: 2, rivalPitStops: 2, rivalCompound: ActualCompound.F1C2, rivalBestLapMs: 89_000));

        Assert.Equal(RivalEvent.FastLap, fastLap!.Event);
    }

    [Fact]
    public void No_rival_returns_null()
    {
        var builder = new RivalDigestBuilder();
        var player = new StandingsRow(1, 0, "Alice", Team.McLaren, 1, 1, 0, 0, 0, 0,
            PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 1, true);
        var snapshot = new TelemetrySnapshot(Meta(), [], [player], [], [], null, null, 1, DateTimeOffset.UtcNow);

        var digest = builder.Update(snapshot);

        Assert.Null(digest);
    }

    [Fact]
    public void No_player_returns_null()
    {
        var builder = new RivalDigestBuilder();
        var rival = new StandingsRow(1, 1, "Bob", Team.Ferrari, 2, 1, 0, 0, 0, 0,
            PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 1, false);
        var snapshot = new TelemetrySnapshot(Meta(), [], [rival], [], [], null, null, 1, DateTimeOffset.UtcNow);

        var digest = builder.Update(snapshot);

        Assert.Null(digest);
    }

    private static SessionMeta Meta(ulong sessionUid = 1) =>
        new(sessionUid, SessionType.Race, Track.Bahrain, 30, 5412, true, 0,
            GameMode.OnlineCustom, Weather.Clear, 30, 24, 0);

    private static TelemetrySnapshot Snapshot(
        ulong sessionUid = 1,
        byte playerPosition = 1,
        byte rivalPosition = 2,
        string rivalName = "Bob",
        byte rivalCarIndex = 1,
        byte rivalLap = 1,
        uint rivalLastLapMs = 0,
        uint rivalBestLapMs = 0,
        byte rivalPitStops = 0,
        ActualCompound rivalCompound = ActualCompound.F1C3,
        byte rivalTyreAge = 1,
        int playerGapToCarInFront = 0,
        int rivalGapToCarInFront = 0)
    {
        var player = new StandingsRow(playerPosition, 0, "Alice", Team.McLaren, 1, rivalLap, 0, 0,
            0, playerGapToCarInFront, PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 1, true);
        var rival = new StandingsRow(rivalPosition, rivalCarIndex, rivalName, Team.Ferrari, 2, rivalLap, rivalLastLapMs,
            rivalBestLapMs, 0, rivalGapToCarInFront, PitStatus.None, 0, ResultStatus.Active,
            rivalCompound, rivalTyreAge, false, NumPitStops: rivalPitStops);
        return new TelemetrySnapshot(Meta(sessionUid), [], [player, rival], [], [], null, null,
            1, DateTimeOffset.UtcNow);
    }
}
