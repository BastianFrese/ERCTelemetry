using ERCTelemetry.Core.Clips;
using Xunit;

namespace ERCTelemetry.Core.Tests.Clips;

/// <summary>FfmpegCommand: the exact flags that make the MP4 browser-compatible.</summary>
public class FfmpegCommandTests
{
    [Fact]
    public void Build_contains_the_browser_compatibility_flags()
    {
        var args = FfmpegCommand.Build(fps: 10, outputPath: @"C:\clips\clip.mp4");

        Assert.Contains("-f", args);
        Assert.Contains("image2pipe", args);
        Assert.Contains("-framerate", args);
        Assert.Contains("10", args);
        Assert.Contains("mjpeg", args);
        Assert.Contains("pipe:0", args);
        Assert.Contains("libx264", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("+faststart", args);
        Assert.Contains(@"C:\clips\clip.mp4", args);
    }

    [Fact]
    public void Build_uses_the_configured_frame_rate()
    {
        var args = FfmpegCommand.Build(fps: 15, outputPath: "out.mp4");

        var framerateIndex = Array.IndexOf(args, "-framerate");
        Assert.True(framerateIndex >= 0);
        Assert.Equal("15", args[framerateIndex + 1]);
    }

    [Fact]
    public void BuildWithAudio_muxes_the_wav_and_encodes_aac()
    {
        var args = FfmpegCommand.BuildWithAudio(fps: 60, audioPath: @"C:\tmp\audio.wav", outputPath: "clip.mp4");

        // Video input: piped MJPEG frames at the given rate.
        Assert.Contains("image2pipe", args);
        Assert.Contains("pipe:0", args);
        var framerateIndex = Array.IndexOf(args, "-framerate");
        Assert.Equal("60", args[framerateIndex + 1]);

        // Audio input: the WAV file, encoded to AAC.
        Assert.Contains(@"C:\tmp\audio.wav", args);
        Assert.Contains("aac", args);
        Assert.Contains("128k", args);

        // Browser compatibility + no trailing gap.
        Assert.Contains("yuv420p", args);
        Assert.Contains("+faststart", args);
        Assert.Contains("-shortest", args);
        Assert.Contains("clip.mp4", args);
    }

    [Theory]
    [InlineData(300, 10.0, 60, 30)]    // dropped frames: real 30 fps, configured 60 → encode at 30
    [InlineData(600, 10.0, 60, 60)]    // healthy: real rate == configured rate → unchanged
    [InlineData(285, 10.0, 60, 29)]    // half-integer rate rounds away from zero (28.5 → 29)
    [InlineData(1200, 10.0, 60, 60)]   // impossible via throttle, but clamps above 60
    [InlineData(1, 10.0, 60, 60)]      // degenerate: single frame → configured fallback
    [InlineData(0, 10.0, 60, 60)]      // degenerate: no frames → configured fallback
    [InlineData(50, 0.0, 60, 60)]      // degenerate: zero span → configured fallback
    [InlineData(7, 0.0, 999, 60)]      // degenerate + out-of-range configured → clamped fallback
    public void EffectiveFps_derives_the_real_capture_rate(int frameCount, double spanSeconds, int configuredFps, int expected)
    {
        Assert.Equal(expected, FfmpegCommand.EffectiveFps(frameCount, spanSeconds, configuredFps));
    }
}
