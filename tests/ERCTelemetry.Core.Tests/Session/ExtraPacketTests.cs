using F1Game.UDP.Data;
using F1Game.UDP.Enums;
using F1Game.UDP.Packets;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

/// <summary>Newer packet types: CarSetups (player only), LapPositions, CarTelemetry2
/// flags, per-lap MotionEx summary.</summary>
public class ExtraPacketTests
{
    private static PacketHeader Head(ulong uid, PacketType type, byte player = 0) => new()
    {
        PacketFormat = 2026,
        GameYear = 26,
        PacketType = type,
        SessionUID = uid,
        PlayerCarIndex = player,
    };

    private static SessionDataPacket SessionPacket(ulong uid, byte player = 0) => new()
    {
        Header = Head(uid, PacketType.Session, player),
        SessionType = SessionType.Race,
        TotalLaps = 10,
    };

    private static LapDataPacket CrossingPacket(ulong uid, byte car, byte currentLap, uint lastLapMs) => new()
    {
        Header = Head(uid, PacketType.LapData, car),
        LapData = LapArray(car, currentLap, 1, lastLapMs),
    };

    private static LapData[] LapArray(byte car, byte lap, byte position, uint lastLapMs)
    {
        var laps = new LapData[22];
        laps[car] = new LapData
        {
            CarPosition = position,
            CurrentLapNum = lap,
            LastLapTimeInMS = lastLapMs,
        };
        return laps;
    }

    [Fact]
    public void Setup_is_captured_for_the_player_only_and_deduplicated()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1, player: 1));

        var packet = new CarSetupDataPacket
        {
            Header = Head(1, PacketType.CarSetups, player: 1),
            NextFrontWingValue = 3.5f,
            CarSetups = SetupSlots(new(), new() { FrontWing = 20, RearWing = 30 }),
        };

        var setup = Assert.Single(store.Apply(packet).OfType<SetupCaptured>());
        Assert.Equal((byte)1, setup.CarIndex);
        Assert.Equal((byte)20, setup.Setup.FrontWing);
        Assert.Equal((byte)4, setup.Setup.NextFrontWingValue); // Math.Round(3.5) = 4
        Assert.Equal(4, setup.Setup.TyresPressure.Length);
        Assert.Empty(store.Apply(packet)); // deduplicated

        var changed = packet with { CarSetups = SetupSlots(new(), new() { FrontWing = 21, RearWing = 30 }) };
        Assert.Single(store.Apply(changed).OfType<SetupCaptured>());
    }

    private static CarSetupData[] SetupSlots(params CarSetupData[] slots)
    {
        var setups = new CarSetupData[22];
        for (var i = 0; i < slots.Length; i++)
        {
            setups[i] = slots[i];
        }

        return setups;
    }

    [Fact]
    public void Lap_positions_are_emitted_once_per_window()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1));

        var packet = new LapPositionsDataPacket
        {
            Header = Head(1, PacketType.LapPositions),
            StartingLap = 10,
            NumberOfLaps = 2,
            PositionsPerLapForVehicle = PosRows(RowOf(3, 1), RowOf(1, 3)),
        };

        var chunk = Assert.Single(store.Apply(packet).OfType<LapPositionsChunk>());
        Assert.Equal((byte)10, chunk.StartingLap);
        Assert.Equal((byte)2, chunk.NumLaps);
        Assert.Equal((byte)1, chunk.PositionsPerLap[0][2]); // car #3 (index 2) leads
        Assert.Equal((byte)2, chunk.PositionsPerLap[0][0]); // car #1 (index 0) is P2
        Assert.Equal((byte)1, chunk.PositionsPerLap[1][0]);
        Assert.Equal((byte)2, chunk.PositionsPerLap[1][2]); // car #3 (index 2) is P2
        Assert.Empty(store.Apply(packet)); // same window

        var advanced = packet with { StartingLap = 20 };
        Assert.Single(store.Apply(advanced).OfType<LapPositionsChunk>());
    }

    private static byte[] RowOf(params byte[] positions)
    {
        var row = new byte[24];
        for (var i = 0; i < positions.Length; i++)
        {
            row[positions[i] - 1] = (byte)(i + 1); // row[car] = position
        }

        return row;
    }

    private static Array50<Array24<byte>> PosRows(params byte[][] rows)
    {
        var items = new Array24<byte>[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            items[i] = (Array24<byte>)rows[i];
        }

        return (Array50<Array24<byte>>)items;
    }

    [Fact]
    public void Telemetry2_flags_flow_into_the_next_lap_completed()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1, player: 2));

        // Warm-up crossing: the first crossing after a lap jump is never announced
        // (forward-jump guard), so seed the lap counter before setting the flags.
        store.Apply(CrossingPacket(1, car: 2, currentLap: 5, lastLapMs: 0));

        store.Apply(new CarTelemetry2DataPacket
        {
            Header = Head(1, PacketType.CarTelemetry2, player: 2),
            CarTelemetry2Data = Slots2(
                new CarTelemetry2Data { ActiveAeroAvailable = true, ActiveAeroMode = 3 },
                new(),
                new CarTelemetry2Data { ActiveAeroAvailable = true, ActiveAeroMode = 4, OvertakeActive = true, IsDrivingWrongWay = true }),
        });

        var events = store.Apply(CrossingPacket(1, car: 2, currentLap: 6, lastLapMs: 91_000u));
        var lap = Assert.Single(events.OfType<LapCompleted>());
        Assert.Equal((byte)2, lap.CarIndex);
        Assert.Equal((byte)4, lap.ActiveAeroMode);
        Assert.True(lap.OvertakeUsed);
        Assert.True(lap.WrongWay);

        // Flags reset on lap advance; aero mode persists.
        var events2 = store.Apply(CrossingPacket(1, car: 2, currentLap: 7, lastLapMs: 90_000u));
        var lap2 = Assert.Single(events2.OfType<LapCompleted>());
        Assert.False(lap2.OvertakeUsed);
        Assert.False(lap2.WrongWay);
        Assert.Equal((byte)4, lap2.ActiveAeroMode);
    }

    private static CarTelemetry2Data[] Slots2(params CarTelemetry2Data[] slots)
    {
        var data = new CarTelemetry2Data[22];
        for (var i = 0; i < slots.Length; i++)
        {
            data[i] = slots[i];
        }

        return data;
    }

    [Fact]
    public void Motion_summary_is_emitted_for_the_completed_lap()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(1, player: 0));

        store.Apply(new LapDataPacket
        {
            Header = Head(1, PacketType.LapData),
            LapData = LapArray(0, 3, 1, 0),
        });
        store.Apply(MotionExPacket(slipFl: 1.5f, slipFr: 0.4f, frontAero: 0.05f, rearAero: 0.08f));
        store.Apply(MotionExPacket(slipFl: 2.5f, slipFr: 0.2f, frontAero: 0.03f, rearAero: 0.06f));

        var events = store.Apply(CrossingPacket(1, car: 0, currentLap: 4, lastLapMs: 90_000u));
        Assert.Equal((byte)3, Assert.Single(events.OfType<LapCompleted>()).LapNumber);
        var summary = Assert.Single(events.OfType<LapMotionSummary>());
        Assert.Equal((byte)3, summary.LapNumber);
        Assert.Equal(2.5f, summary.Data.MaxWheelSlipRatio[0]);
        Assert.Equal(0.4f, summary.Data.MaxWheelSlipRatio[1]);
        Assert.Equal(0.03f, summary.Data.MinFrontAeroHeight, precision: 3);
        Assert.Equal(0.06f, summary.Data.MinRearAeroHeight, precision: 3);

        // Summary consumed: next crossing without samples emits none.
        var events2 = store.Apply(CrossingPacket(1, car: 0, currentLap: 5, lastLapMs: 90_000u));
        Assert.DoesNotContain(events2, e => e is LapMotionSummary);
    }

    private static MotionExDataPacket MotionExPacket(float slipFl, float slipFr, float frontAero, float rearAero) => new()
    {
        Header = Head(1, PacketType.MotionEx),
        WheelSlipRatio = new Tyres<float>
        {
            FrontLeft = slipFl,
            FrontRight = slipFr,
            RearLeft = slipFl * 0.5f,
            RearRight = slipFr * 0.5f,
        },
        FrontAeroHeight = frontAero,
        RearAeroHeight = rearAero,
    };
}