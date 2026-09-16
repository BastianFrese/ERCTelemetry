using System.Diagnostics;

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

    /// <summary>True when ffmpeg closed stdin before all frames were written because
    /// -shortest already ended the mux at the shorter stream (audio shorter than the
    /// video — Kollision kurz nach Sessionstart, Audio-Ring durch Neustart noch nicht
    /// gefüllt): the process exits 0 and leaves a complete, non-empty output. That is a
    /// REGULAR completion, not a failed encode — without this the caller would catch the
    /// broken-pipe IOException, delete the valid MP4 and treat the clip as failed.
    /// Returns false for any real failure, so the caller keeps the exception. The pipe
    /// usually closes the moment ffmpeg finishes reading its inputs while it is still
    /// writing the muxed file, so the process is given a short grace to reach its exit
    /// code before the decision.</summary>
    public static async Task<bool> IsRegularEarlyExitAsync(Process process, string outputPath)
    {
        // Erst auf den Exit warten, dann die Output-Datei prüfen: im Moment der stdin-IOException
        // kann die Datei noch kurzzeitig fehlen/leer sein (ffmpeg hat die Mux noch nicht in die
        // Datei gespült, +faststart-Moov-Rewrite), obwohl der Early-Exit regulär ist. Ein vorzeitiger
        // File-Check würde den Guard fälschlich abschlagen und einen gültigen Clip löschen (LOW,
        // 2026-09-16).
        using var grace = new CancellationTokenSource(EarlyExitGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            return false; // still running → not a regular -shortest end
        }

        return process.ExitCode == 0
            && new FileInfo(outputPath) is { Exists: true, Length: > 0 };
    }

    /// <summary>How long ffmpeg may still be writing its output after the stdin pipe
    /// closed before the early-exit is judged irregular. Encode of a short clip finishes
    /// in well under a second; 3 s generously separates "regular -shortest end" from
    /// "encoder died without finishing".</summary>
    private static readonly TimeSpan EarlyExitGrace = TimeSpan.FromSeconds(3);
}
