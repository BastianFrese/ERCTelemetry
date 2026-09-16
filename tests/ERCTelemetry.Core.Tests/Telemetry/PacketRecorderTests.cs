using ERCTelemetry.Core.Telemetry;
using Xunit;

namespace ERCTelemetry.Core.Tests.Telemetry;

public class PacketRecorderTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"f1rec-test-{Guid.NewGuid():N}.f1rec");

    [Fact]
    public void Recorded_frames_round_trip_with_original_bytes()
    {
        var frames = new[]
        {
            new byte[] { 1, 2, 3, 4 },
            new byte[] { 0xFF },
            new byte[] { 7 },
        };

        using (var recorder = new PacketRecorder())
        {
            recorder.Start(_path);
            foreach (var frame in frames)
            {
                recorder.Record(frame);
            }
        }

        var read = PacketRecorder.ReadFrames(_path);

        Assert.Equal(3, read.Count);
        for (var i = 0; i < frames.Length; i++)
        {
            Assert.Equal(frames[i], read[i].Data);
        }
        Assert.InRange(read[1].UtcTicks - read[0].UtcTicks, 0, TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Record_without_start_drops_silently()
    {
        using var recorder = new PacketRecorder();
        recorder.Record(new byte[] { 1 }); // must not throw
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void ReadFrames_rejects_non_recording_files()
    {
        File.WriteAllText(_path, "not a recording");
        Assert.Throws<InvalidDataException>(() => PacketRecorder.ReadFrames(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}