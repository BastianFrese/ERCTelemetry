using System.Text.Json;
using F1Game.UDP.Data;
using F1Game.UDP.Enums;
using F1Game.UDP.Packets;
using ERCTelemetry.Core.OverlayProtocol;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Telemetry;
using Xunit;

namespace ERCTelemetry.Core.Tests.Telemetry;

public class PacketReplayerTests
{
    private const ulong SessionUid = 1111;

    [Fact]
    public void Recorder_round_trip_reparses_recorded_datagrams()
    {
        var path = F1RecPath();
        try
        {
            using (var recorder = new PacketRecorder())
            {
                recorder.Start(path);
                recorder.Record(TestDatagrams.Motion());
                recorder.Record(TestDatagrams.MotionWith(frameId: 101));
                recorder.Record(new byte[] { 0, 0 }); // header format 0 → unsupported
                recorder.Stop();
            }

            var replay = PacketReplayer.Load(path);

            Assert.Equal(3, replay.Packets.Count + replay.CorruptFrames + replay.UnsupportedFormatFrames);
            Assert.Equal(2, replay.Packets.Count);
            Assert.Equal(PacketType.Motion, replay.Packets[0].Packet.PacketType);
            Assert.Equal((ushort)2026, replay.Packets[0].Packet.Header.PacketFormat);
            Assert.Equal(101u, replay.Packets[1].Packet.Header.OverallFrameIdentifier);
            Assert.Equal(1, replay.UnsupportedFormatFrames);
            Assert.Equal(0, replay.CorruptFrames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_payload_is_counted_and_skipped()
    {
        var path = F1RecPath();
        try
        {
            // Valid 2026 header, truncated body: the parser must reject it.
            using (var recorder = new PacketRecorder())
            {
                recorder.Start(path);
                recorder.Record(TestDatagrams.Motion()[..40]);
                recorder.Stop();
            }

            var replay = PacketReplayer.Load(path);

            Assert.Empty(replay.Packets);
            Assert.Equal(1, replay.CorruptFrames);
            Assert.Equal(0, replay.UnsupportedFormatFrames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_snapshot_flows_to_overlay_json()
    {
        var store = new SessionStateStore();
        store.Apply(SessionPacket(SessionUid));
        store.Apply(ParticipantsPacket(SessionUid,
            (0, "Stream Pilot", Team.Mercedes, 44),
            (1, "Rival Pilot", Team.Mercedes, 7)));
        store.Apply(LapPacket(SessionUid, (0, 1, 2, 84_500, 1_200)));

        var snapshot = store.BuildSnapshot();
        var json = OverlayMessageFactory.CreateState(snapshot);

        using var document = JsonDocument.Parse(json);
        var standings = document.RootElement.GetProperty("standings");

        Assert.Equal(1, standings.GetArrayLength());
        var leader = standings[0];
        Assert.Equal("Stream Pilot", leader.GetProperty("name").GetString());
        Assert.Equal(1, leader.GetProperty("position").GetInt32());
        Assert.Equal(84_500u, leader.GetProperty("lastLapTimeMs").GetUInt32());
        Assert.Equal(20, document.RootElement.GetProperty("session").GetProperty("totalLaps").GetInt32());
    }

    [Fact]
    public void Real_fixture_replays_when_present()
    {
        // A real .f1rec is optional: the user drops one into tests/Fixtures/ after
        // recording a session via the Debug tab. Absent fixture → nothing to verify.
        var fixturesDirectory = FindFixturesDirectory();
        if (fixturesDirectory is null)
        {
            return;
        }

        foreach (var fixture in Directory.EnumerateFiles(fixturesDirectory, "*.f1rec"))
        {
            var replay = PacketReplayer.Load(fixture);

            Assert.NotEmpty(replay.Packets);
            Assert.Equal(0, replay.CorruptFrames);
            Assert.Equal(0, replay.UnsupportedFormatFrames);
            Assert.All(replay.Packets,
                p => Assert.Equal((ushort)2026, p.Packet.Header.PacketFormat));
        }
    }

    /// <summary>Walks up from the test output directory looking for tests/Fixtures.</summary>
    private static string? FindFixturesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Fixtures");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.f1rec").Any())
            {
                return candidate;
            }

            directory = directory.Parent!;
        }

        return null;
    }

    private static PacketHeader Header(PacketType type, ulong uid, byte playerIndex = 0) => new()
    {
        PacketFormat = 2026,
        GameYear = 26,
        GameMajorVersion = 1,
        PacketVersion = 1,
        PacketType = type,
        SessionUID = uid,
        PlayerCarIndex = playerIndex,
    };

    private static SessionDataPacket SessionPacket(ulong uid) => new()
    {
        Header = Header(PacketType.Session, uid),
        SessionType = SessionType.Race,
        TotalLaps = 20,
        TrackLength = 5000,
        Weather = Weather.Clear,
    };

    private static ParticipantsDataPacket ParticipantsPacket(ulong uid,
        params (byte Index, string Name, Team Team, byte RaceNumber)[] drivers)
    {
        var participants = new ParticipantData[22];
        foreach (var (index, name, team, raceNumber) in drivers)
        {
            participants[index] = new ParticipantData
            {
                Name = name,
                Team = team,
                RaceNumber = raceNumber,
            };
        }

        return new ParticipantsDataPacket
        {
            Header = Header(PacketType.Participants, uid),
            NumActiveCars = (byte)drivers.Length,
            Participants = participants,
        };
    }

    private static LapDataPacket LapPacket(ulong uid,
        params (int Index, byte Position, byte Lap, uint LastLap, uint CurrentLap)[] cars)
    {
        var laps = new LapData[22];
        foreach (var (index, position, lap, lastLap, currentLap) in cars)
        {
            laps[index] = new LapData
            {
                CarPosition = position,
                CurrentLapNum = lap,
                LastLapTimeInMS = lastLap,
                CurrentLapTimeInMS = currentLap,
            };
        }

        return new LapDataPacket { Header = Header(PacketType.LapData, uid), LapData = laps };
    }

    private static string F1RecPath() =>
        Path.Combine(Path.GetTempPath(), $"erc-f1rec-{Guid.NewGuid():N}.f1rec");
}
