using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ERCTelemetry.Core.Clips;
using Xunit;

namespace ERCTelemetry.Core.Tests.Clips;

/// <summary>End-to-end: the clip pipeline (the exact steps of
/// ScreenCaptureService.SaveClipAsync) against the real bundled ffmpeg.exe. Proves a
/// collision clip encodes to real-time duration with H.264 video + AAC audio, and that
/// an audio track shorter than the video (ring gap / device restart) completes through
/// the -shortest early-exit guard instead of failing the clip. Skipped when ffmpeg.exe
/// is not available (never run CI without it — it lives in the repo under
/// src/ERCTelemetry.App/ffmpeg/).</summary>
public sealed class EncodePipelineTests
{
    private const int LoopbackSampleRate = 48000; // WASAPI loopback default
    private const int LoopbackChannels = 2;

    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    // Real, decoder-able 32x32 JPEGs (black/white, generated with System.Drawing) — the
    // Store tests' 0xFF 0xD8 … bytes are not JPEGs and ffmpeg rejects the whole stream.
    private static readonly byte[] JpegBlack = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAgACADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD8qqKKKACiiigAooooAKKKKAP/2Q==");

    private static readonly byte[] JpegWhite = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAgACADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD9U6KKKACiiigAooooAKKKKAP/2Q==");

    [Fact]
    public async Task Healthy_clip_encodes_to_real_time_duration_with_h264_and_aac()
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            return; // Kein ffmpeg im Repo-Checkout — Encode-Test überspringt still.
        }
        var workDir = NewTempDir();
        try
        {
            // 20-s window: 12 s pre-roll + 8 s post-roll around the collision.
            var configuredFps = 60;
            var preRoll = TimeSpan.FromSeconds(12);
            var postRoll = TimeSpan.FromSeconds(8);
            var collisionUtc = BaseUtc + TimeSpan.FromSeconds(12);
            var fromUtc = collisionUtc - preRoll;
            var toUtc = collisionUtc + postRoll;

            // Frames captured at a REAL average rate below the configured one (every
            // other 60-fps slot is a simulated drop → ~30 fps). Encoding at the
            // configured 60 would make the clip play at 2x speed — the EffectiveFps fix
            // must encode at ~30 so 600 frames cover the full 20-s window.
            using var store = new RollingFrameStore(Path.Combine(workDir, "buffer"), window: TimeSpan.FromSeconds(30));
            foreach (var (utc, jpeg) in CaptureFrames(fromUtc, toUtc, configuredFps))
            {
                store.Add(utc, jpeg);
            }

            // Audio covers the whole window — a healthy, gap-free session.
            var ring = new AudioRingBuffer(LoopbackSampleRate, LoopbackChannels, capacitySeconds: 30);
            FillRing(ring, fromUtc, toUtc);

            var output = Path.Combine(workDir, "clip-healthy.mp4");
            var result = await EncodeAsync(ffmpeg, store, ring, fromUtc, toUtc, configuredFps, output);
            var probe = Probe(ffmpeg, output);

            Assert.True(result.Success, $"ffmpeg exit {result.ExitCode}: {result.Stderr}");
            var file = new FileInfo(output);
            Assert.True(file.Exists && file.Length > 0, "A non-empty MP4 must exist");

            // Real-time duration: the encoded clip must be as long as the video the
            // audio were live, NOT half of it (that is the 2x-speed regression).
            Assert.InRange(probe.Seconds, 18.0, 21.5);
            Assert.True(Math.Abs(probe.Seconds - result.ExpectedDuration) < 1.0,
                $"Probe {probe.Seconds:F2}s differs from real-time {result.ExpectedDuration:F2}s");

            Assert.True(probe.HasH264, "Video stream must be H.264");
            Assert.True(probe.HasAac, "Audio stream must be AAC");

            // audio ≈ video here, so all frames were written and the guard never fires.
            Assert.False(result.GuardTripped);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Audio_shorter_than_video_completes_via_the_regular_early_exit_guard()
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            return; // Kein ffmpeg im Repo-Checkout — Encode-Test überspringt still.
        }
        var workDir = NewTempDir();
        try
        {
            var configuredFps = 60;
            var preRoll = TimeSpan.FromSeconds(12);
            var postRoll = TimeSpan.FromSeconds(8);
            var collisionUtc = BaseUtc + TimeSpan.FromSeconds(12);
            var fromUtc = collisionUtc - preRoll;
            var toUtc = collisionUtc + postRoll;

            using var store = new RollingFrameStore(Path.Combine(workDir, "buffer"), window: TimeSpan.FromSeconds(30));
            foreach (var (utc, jpeg) in CaptureFrames(fromUtc, toUtc, configuredFps))
            {
                store.Add(utc, jpeg);
            }

            // The ring only covers the first 2 s of the window (Kollision kurz nach
            // Sessionstart, Ring durch Geräte-Neustart noch nicht gefüllt). ffmpeg ends
            // the mux with -shortest at the 2-s audio track and closes stdin early; the
            // leftover video frames then hit a broken pipe in the write loop.
            var ring = new AudioRingBuffer(LoopbackSampleRate, LoopbackChannels, capacitySeconds: 30);
            FillRing(ring, fromUtc, fromUtc + TimeSpan.FromSeconds(2));

            var output = Path.Combine(workDir, "clip-short-audio.mp4");
            var result = await EncodeAsync(ffmpeg, store, ring, fromUtc, toUtc, configuredFps, output);
            var probe = Probe(ffmpeg, output);

            Assert.True(result.Success, $"ffmpeg exit {result.ExitCode}: {result.Stderr}");
            // The broken-pipe IOException was recognized as the regular -shortest end and
            // the actually-valid output was KEPT (the old behaviour deleted it). This is
            // the fix's end-to-end proof against a real ffmpeg.
            Assert.True(result.GuardTripped, "The -shortest early-exit guard must have fired");
            var file = new FileInfo(output);
            Assert.True(file.Exists && file.Length > 0, "A non-empty MP4 must be kept");

            // The clip is as long as the audio (≈2 s), not the full video window.
            Assert.InRange(probe.Seconds, 1.5, 3.0);
            Assert.True(Math.Abs(probe.Seconds - result.ExpectedDuration) < 1.0,
                $"Probe {probe.Seconds:F2}s differs from expected {result.ExpectedDuration:F2}s");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ pipeline

    private sealed record EncodeResult(
        bool Success, int ExitCode, string Stderr, string OutputPath,
        double ExpectedDuration, bool GuardTripped);

    /// <summary>The encode path of ScreenCaptureService.SaveClipAsync, reproduced with the
    /// Core building blocks: TakeSince → trim to window → chronological sort →
    /// EffectiveFps → ffmpeg with the sliced WAV, video piped on stdin. Returns whether
    /// ffmpeg succeeded and whether the -shortest early-exit guard tripped.</summary>
    private static async Task<EncodeResult> EncodeAsync(
        string ffmpeg, RollingFrameStore store, AudioRingBuffer ring,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int configuredFps, string outputPath)
    {
        var windowed = store.TakeSince(fromUtc)
            .Where(f => f.Utc <= toUtc)
            .OrderBy(f => f.Utc)
            .ToList();
        if (windowed.Count == 0)
        {
            throw new InvalidOperationException("Test setup produced no frames for the window.");
        }

        var spanSeconds = (windowed[^1].Utc - windowed[0].Utc).TotalSeconds;
        var fps = FfmpegCommand.EffectiveFps(windowed.Count, spanSeconds, configuredFps);
        var videoDuration = windowed.Count / (double)fps;

        var audio = ring.Take(fromUtc, toUtc);
        var wavPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "audio.wav");
        double audioDuration = 0;
        if (audio is { Length: > 0 })
        {
            WritePcmFloatWav(wavPath, audio, LoopbackSampleRate, LoopbackChannels);
            audioDuration = audio.Length / (double)(LoopbackSampleRate * LoopbackChannels);
        }

        var args = audio is { Length: > 0 }
            ? FfmpegCommand.BuildWithAudio(fps, wavPath, outputPath)
            : FfmpegCommand.Build(fps, outputPath);
        var expectedDuration = audio is { Length: > 0 } ? Math.Min(videoDuration, audioDuration) : videoDuration;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var token = timeout.Token;
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var guardTripped = false;
        try
        {
            foreach (var (_, jpeg) in windowed)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(jpeg, token);
                }
                catch (IOException)
                {
                    // Broken pipe because -shortest already ended the mux — the guard
                    // decides whether that was a regular completion.
                    if (await FfmpegCommand.IsRegularEarlyExitAsync(process, outputPath))
                    {
                        guardTripped = true;
                        break;
                    }
                    throw;
                }
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException) when (guardTripped)
            {
                // ffmpeg already closed the pipe — nothing left to close.
            }

            await process.WaitForExitAsync(token);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
            }
        }

        var stderr = await stderrTask;
        return new EncodeResult(
            process.ExitCode == 0, process.ExitCode, stderr.Trim(), outputPath, expectedDuration, guardTripped);
    }

    /// <summary>Reads duration + codecs back out of the MP4 (ffprobe-style, via ffmpeg -i).</summary>
    private static (double Seconds, bool HasH264, bool HasAac) Probe(string ffmpeg, string outputPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return ((double)(ParseDuration(stderr) ?? 0), stderr.Contains("Video: h264"), stderr.Contains("Audio: aac"));
    }

    private static double? ParseDuration(string ffmpegOutput)
    {
        var m = Regex.Match(ffmpegOutput, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success)
        {
            return null;
        }

        var hours = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return hours * 3600.0 + minutes * 60.0 + seconds;
    }

    // ------------------------------------------------------------------ fixtures

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erc-encode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Resolves ffmpeg.exe: ERCTELEMETRY_FFMPEG env-var first, then the repo
    /// default under src/ERCTelemetry.App/ffmpeg/ (climbing from the build output to the
    /// repo root). Null when neither exists.</summary>
    private static string? ResolveFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ERCTELEMETRY_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        // Repo default: climb from the build output to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ERCTelemetry.App", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Frames every 1/fps seconds with every other slot dropped (a real capture
    /// at half the configured rate) and black/white alternating content.</summary>
    private static List<(DateTimeOffset Utc, byte[] Jpeg)> CaptureFrames(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int configuredFps)
    {
        var fps = Math.Clamp(configuredFps, 1, 60);
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var frames = new List<(DateTimeOffset, byte[])>();
        var index = 0;
        for (var utc = fromUtc; utc < toUtc; utc += interval, index++)
        {
            if (index % 2 == 0)
            {
                continue; // simulated drop
            }
            frames.Add((utc, index % 4 == 1 ? JpegBlack : JpegWhite));
        }
        return frames;
    }

    /// <summary>Fills the ring with a quiet 440-Hz tone from fromUtc to toUtc, so the AAC
    /// encoder has real audio to mux.</summary>
    private static void FillRing(AudioRingBuffer ring, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        const double toneFrequency = 440.0;
        var utc = fromUtc;
        double globalSample = 0;
        while (utc < toUtc)
        {
            var count = (int)((toUtc - utc).TotalSeconds * LoopbackSampleRate * LoopbackChannels);
            if (count <= 0)
            {
                break;
            }

            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * toneFrequency * globalSample / LoopbackSampleRate));
                globalSample++;
            }

            ring.Add(utc, samples);
            utc += TimeSpan.FromSeconds(count / (double)(LoopbackSampleRate * LoopbackChannels));
        }
    }

    /// <summary>Writes a float32-PCM WAV from interleaved samples (the same format
    /// WriteAudioWindow produces with NAudio).</summary>
    private static void WritePcmFloatWav(string path, float[] samples, int sampleRate, int channels)
    {
        var dataSize = samples.Length * sizeof(float);
        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // fmt chunk size
        writer.Write((short)3); // IEEE float
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(float)); // byte rate
        writer.Write((short)(channels * sizeof(float))); // block align
        writer.Write((short)32); // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }
}
