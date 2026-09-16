using F1Game.UDP.Data;
using F1Game.UDP.Enums;
using F1Game.UDP.Packets;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

/// <summary>SessionHistory ingestion: incremental emission, sector-minutes combination,
/// partial-lap skip, stint list semantics.</summary>
public class SessionHistoryTests
{
    private static PacketHeader Header(ulong uid) => new()
    {
        PacketFormat = 2026,
        GameYear = 26,
        PacketType = PacketType.SessionHistory,
        SessionUID = uid,
    };

    private static SessionHistoryDataPacket HistoryPacket(ulong uid, byte carIndex,
        params (uint LapMs, ushort S1Ms, byte S1Min, ushort S2Ms, byte S2Min, ushort S3Ms, byte S3Min, ushort Flags)[] laps)
    {
        var rows = new LapHistoryData[100];
        for (var i = 0; i < laps.Length; i++)
        {
            var (lapMs, s1ms, s1min, s2ms, s2min, s3ms, s3min, flags) = laps[i];
            rows[i] = new LapHistoryData
            {
                LapTimeInMS = lapMs,
                Sector1TimeInMS = s1ms,
                Sector1TimeMinutes = s1min,
                Sector2TimeInMS = s2ms,
                Sector2TimeMinutes = s2min,
                Sector3TimeInMS = s3ms,
                Sector3TimeMinutes = s3min,
                LapValidBitFlags = (LapValid)flags,
            };
        }

        return new SessionHistoryDataPacket
        {
            Header = Header(uid),
            CarIndex = carIndex,
            NumLaps = (byte)laps.Length,
            LapHistoryData = rows,
        };
    }

    private static SessionDataPacket SessionPacket(ulong uid) => new()
    {
        Header = new PacketHeader { PacketType = PacketType.Session, SessionUID = uid },
        SessionType = SessionType.Race,
        TotalLaps = 10,
        TrackLength = 5000,
    };

    [Fact]
    public void Laps_are_emitted_incrementally()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1));

        var first = store.Apply(HistoryPacket(1, carIndex: 3,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15),
            (91_000u, 30_200, 0, 30_400, 0, 30_400, 0, 15),
            (92_000u, 30_400, 0, 30_500, 0, 30_600, 0, 15)));
        var evt = Assert.Single(first.OfType<LapHistoryUpdated>());
        Assert.Equal(3, evt.NewLaps.Count);
        Assert.Equal(1, evt.NewLaps[0].LapNumber);
        Assert.Equal(3, evt.NewLaps[2].LapNumber);
        Assert.Equal(90_000u, evt.NewLaps[0].LapTimeMs);
        Assert.Equal(3, evt.TotalLaps);
        Assert.Equal((ushort)15, evt.NewLaps[0].ValidFlags);

        // Identical re-send: nothing new.
        Assert.Empty(store.Apply(HistoryPacket(1, 3,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15),
            (91_000u, 30_200, 0, 30_400, 0, 30_400, 0, 15),
            (92_000u, 30_400, 0, 30_500, 0, 30_600, 0, 15))));

        // One more completed lap: exactly the new row.
        var fourth = store.Apply(HistoryPacket(1, 3,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15),
            (91_000u, 30_200, 0, 30_400, 0, 30_400, 0, 15),
            (92_000u, 30_400, 0, 30_500, 0, 30_600, 0, 15),
            (93_000u, 30_500, 0, 30_500, 0, 30_500, 0, 15)));
        var evt4 = Assert.Single(fourth.OfType<LapHistoryUpdated>());
        Assert.Single(evt4.NewLaps);
        Assert.Equal(4, evt4.NewLaps[0].LapNumber);
    }

    [Fact]
    public void Sector_minutes_are_combined()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1));

        var events = store.Apply(HistoryPacket(1, 2, (92_345u, 0, 1, 23_456, 0, 23_456, 1, 15)));
        var row = Assert.Single(Assert.Single(events.OfType<LapHistoryUpdated>()).NewLaps);
        Assert.Equal(60_000u, row.Sector1Ms);
        Assert.Equal(23_456u, row.Sector2Ms);
        Assert.Equal(83_456u, row.Sector3Ms);
    }

    [Fact]
    public void Partial_lap_is_skipped_and_emitted_once_completed()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1));

        // Third row has LapTimeInMS == 0 (lap 3 in progress) → only laps 1..2 emitted.
        var first = store.Apply(HistoryPacket(1, 3,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15),
            (91_000u, 30_200, 0, 30_400, 0, 30_400, 0, 15),
            (0u, 0, 0, 0, 0, 0, 0, 0)));
        var evt = Assert.Single(first.OfType<LapHistoryUpdated>());
        Assert.Equal(2, evt.NewLaps.Count);
        Assert.Equal(2, evt.TotalLaps);

        // Identical packet again → nothing.
        Assert.Empty(store.Apply(HistoryPacket(1, 3,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15),
            (91_000u, 30_200, 0, 30_400, 0, 30_400, 0, 15),
            (0u, 0, 0, 0, 0, 0, 0, 0))));

        // Lap 3 completes → exactly the missing row.
        var third = store.Apply(HistoryPacket(1, 3,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15),
            (91_000u, 30_200, 0, 30_400, 0, 30_400, 0, 15),
            (92_000u, 30_400, 0, 30_500, 0, 30_600, 0, 15)));
        var evt3 = Assert.Single(third.OfType<LapHistoryUpdated>());
        Assert.Single(evt3.NewLaps);
        Assert.Equal(3, evt3.NewLaps[0].LapNumber);
        Assert.Equal(92_000u, evt3.NewLaps[0].LapTimeMs);
    }

    private static SessionHistoryDataPacket WithStints(SessionHistoryDataPacket packet, params byte[] endLaps) =>
        packet with
        {
            NumTyreStints = (byte)endLaps.Length,
            TyreStintsHistoryData = endLaps.Select(e => new TyreStintHistoryData
            {
                EndLap = e,
                TyreActualCompound = ActualCompound.F1C3,
                TyreVisualCompound = VisualCompound.F1Soft,
            }).ToArray(),
        };

    [Fact]
    public void Stint_list_is_resent_when_count_changes()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1));

        var first = store.Apply(WithStints(HistoryPacket(1, 4,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15)), 255));
        var evt = Assert.Single(first.OfType<LapHistoryUpdated>());
        var stint = Assert.Single(evt.Stints);
        Assert.Equal((byte)255, stint.EndLap); // ongoing stint = 255

        // Same laps, same stint count → nothing.
        Assert.Empty(store.Apply(WithStints(HistoryPacket(1, 4,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15)), 255)));

        // Stint count 1 → 2: stints re-sent complete.
        var after = store.Apply(WithStints(HistoryPacket(1, 4,
            (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15)), 255, 6));
        var evt2 = Assert.Single(after.OfType<LapHistoryUpdated>());
        Assert.Equal(2, evt2.Stints.Count);
        Assert.Equal((byte)255, evt2.Stints[0].EndLap);
        Assert.Equal((byte)6, evt2.Stints[1].EndLap);
    }

    [Fact]
    public void Out_of_range_car_index_is_ignored()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1));
        Assert.Empty(store.Apply(HistoryPacket(1, 30, (90_000u, 30_000, 0, 30_000, 0, 30_000, 0, 15))));
    }
}