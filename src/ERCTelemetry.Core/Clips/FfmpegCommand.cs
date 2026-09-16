namespace ERCTelemetry.Core.Clips;

/// <summary>Builds the ffmpeg argument list for encoding a sequence of JPEG frames
/// (piped on stdin) into a browser-compatible H.264 MP4. Pure — no process logic, so
/// the exact flags are unit-testable. yuv420p + faststart keep the file playable in
/// browsers (the planned session-sharing feature) and in the default Windows player.</summary>
public static class FfmpegCommand
{
    /// <summary>The frame rate to hand to ffmpeg so the encoded clip plays at real time:
    /// the actual average capture rate of the window, derived from the first and last
    /// frame timestamps. Encode-at-configured-rate makes a clip play too fast whenever
    /// the real capture rate falls short of the configured FPS (frames dropped under
    /// load, slow encode) — ffmpeg then packs fewer than <c>fps</c> frames per real
    /// second, so the video is shorter than the (correctly timed) audio. Clamped to
    /// [1, 60]; falls back to the configured rate when the timing is degenerate
    /// (fewer than two frames or zero span).</summary>
    public static int EffectiveFps(int frameCount, double spanSeconds, int configuredFps)
    {
        var fallback = Math.Clamp(configuredFps, 1, 60);
        if (frameCount < 2 || spanSeconds <= 0)
        {
            return fallback;
        }

        var averageFps = frameCount / spanSeconds;
        // AwayFromZero: a measured rate of 28.5 fps should encode at 29 (banker's
        // rounding would silently pick 28 and play the clip ~1.8% slow).
        return (int)Math.Clamp(Math.Round(averageFps, MidpointRounding.AwayFromZero), 1, 60);
    }

    /// <summary>Arguments for: read MJPEG frames from stdin at the given frame rate,
    /// encode H.264 (veryfast preset, CRF 20), move the moov atom to the front.</summary>
    public static string[] Build(int fps, string outputPath) =>
    [
        "-y",
        "-f", "image2pipe",
        "-framerate", fps.ToString(),
        "-c:v", "mjpeg",
        "-i", "pipe:0",
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-crf", "20",
        "-pix_fmt", "yuv420p",
        "-movflags", "+faststart",
        outputPath,
    ];

    /// <summary>Arguments for: read MJPEG frames from stdin (video) and mux a WAV file
    /// (audio) into one browser-compatible H.264/AAC MP4. The audio file is a separate
    /// input because a single process has only one stdin — the frames go to pipe:0, the
    /// audio is read from disk. -shortest ends the file when either stream runs out, so
    /// a sub-sample length mismatch never leaves a silent tail or frozen last frame.</summary>
    public static string[] BuildWithAudio(int fps, string audioPath, string outputPath) =>
    [
        "-y",
        "-f", "image2pipe",
        "-framerate", fps.ToString(),
        "-c:v", "mjpeg",
        "-i", "pipe:0",
        "-i", audioPath,
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-crf", "20",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-b:a", "128k",
        "-shortest",
        "-movflags", "+faststart",
        outputPath,
    ];
}
