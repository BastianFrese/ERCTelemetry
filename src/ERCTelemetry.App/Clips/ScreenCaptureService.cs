using System.Diagnostics;
using System.IO;
using ERCTelemetry.Core.Clips;
using NAudio.Wave;

namespace ERCTelemetry.App.Clips;

/// <summary>Background screen-capture pipeline: HdrFrameSource (Windows.Graphics.Capture,
/// HDR-correct) feeds a rolling disk buffer of JPEG frames, AudioLoopbackSource feeds a
/// rolling PCM ring, and SaveClipAsync muxes the collision window through ffmpeg into a
/// browser-playable MP4 with audio. Captures only while a network session is active AND
/// clip recording is enabled — settings are read on a timer, so changes apply without a
/// restart. Video frames live on disk (segment files), only audio stays in RAM.</summary>
public sealed class ScreenCaptureService : IDisposable
{
    private readonly Func<ClipSettings> _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private readonly HdrFrameSource _frames = new();
    private readonly AudioLoopbackSource _audio;
    private RollingFrameStore? _store;

    /// <summary>Upper bound for one ffmpeg encode. A hung encoder (corrupt frame, D3D
    /// stall) must release the capture save gate instead of blocking every later clip.</summary>
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(5);
    private DateTimeOffset _lastFrameUtc;
    private int? _lastCaptureItemWidth;
    private volatile bool _sessionActive;
    private int _disposed;

    public ScreenCaptureService(Func<ClipSettings> settings)
    {
        _settings = settings;
        _audio = new AudioLoopbackSource(settings);
        _frames.FrameCaptured += OnFrameCaptured;
        _loop = Task.Run(() => ManageLoopAsync(_stop.Token));
    }

    /// <summary>Gates the capture pipeline: only capture while a network session is running.</summary>
    public void SetSessionActive(bool active) => _sessionActive = active;

    /// <summary>An encoded clip on disk.</summary>
    public sealed record ClipResult(string Path, double DurationSeconds);

    /// <summary>Waits until the post-roll window has elapsed, takes the frames
    /// [collision − preRoll, collision + postRoll] from the disk buffer and the matching
    /// audio from the PCM ring, then muxes both through ffmpeg. Null when the window had
    /// no frames, ffmpeg is missing or failed.</summary>
    public async Task<ClipResult?> SaveClipAsync(ClipMetadata metadata, CancellationToken ct)
    {
        var settings = _settings();

        var waitUntil = metadata.Utc + TimeSpan.FromSeconds(settings.PostRollSeconds);
        var delay = waitUntil - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        RollingFrameStore? store;
        AudioRingBuffer? ring;
        lock (_gate)
        {
            store = _store;
            ring = _audio.Ring;
        }

        var fromUtc = metadata.Utc - TimeSpan.FromSeconds(settings.PreRollSeconds);
        var toUtc = metadata.Utc + TimeSpan.FromSeconds(settings.PostRollSeconds);
        var frames = store?.TakeSince(fromUtc) ?? Array.Empty<(DateTimeOffset, byte[])>();
        // TakeSince returns everything from fromUtc onward, which can extend past toUtc
        // when the save runs late — trim to the window so the clip is exactly preRoll+postRoll.
        // Sort by capture time: a 2-buffer capture can store out of capture order, and
        // ffmpeg must get chronological JPEGs (the span below is then the true min/max).
        var windowed = frames.Where(f => f.Utc <= toUtc).OrderBy(f => f.Utc).ToList();
        if (windowed.Count == 0)
        {
            return null;
        }

        // Encode at the window's real capture rate, not the configured one: when frames
        // were dropped, the window holds fewer than fps-per-second frames, and encoding
        // them at the configured rate would play the video faster than real time while
        // the audio (sliced at its true sample rate) stays normal — a sped-up clip.
        var spanSeconds = (windowed[^1].Utc - windowed[0].Utc).TotalSeconds;
        var fps = FfmpegCommand.EffectiveFps(windowed.Count, spanSeconds, settings.Fps);

        var outputPath = ClipStorage.BuildPath(metadata);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var ffmpegPath = FfmpegPath();
        if (ffmpegPath is null)
        {
            App.Log("Clip recording disabled: ffmpeg.exe not found next to the app.");
            return null;
        }

        // Slice the audio window and write it to a temp WAV (video is piped on stdin).
        var audio = WriteAudioWindow(ring, fromUtc, toUtc);
        Process? process = null;
        var started = false;
        // Im Erfolgspfad wird die stderr-Task normal awaited; wirft es davor, muss sie im
        // catch beobachtet werden, sonst fault sie unobserved, wenn process.Dispose() die
        // umgeleiteten Streams schließt (LOW, 2026-09-16).
        Task<string>? stderrTask = null;
        try
        {
            var args = audio.FilePath is null
                ? FfmpegCommand.Build(fps, outputPath)
                : FfmpegCommand.BuildWithAudio(fps, audio.FilePath, outputPath);
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            // ArgumentList quotes every argument. The joined-string form breaks on paths
            // with spaces (clip + temp audio live in %LOCALAPPDATA%/%TEMP%), so an output
            // path like "Max Mustermann\AppData\..." reaches ffmpeg truncated — every
            // encode then fails for such users.
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            // A hung encoder must not hold the capture save gate forever — kill on
            // timeout exactly like on failure.
            using var encodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            encodeCts.CancelAfter(EncodeTimeout);
            var encodeCt = encodeCts.Token;

            process.Start();
            started = true;
            // Drain stderr concurrently so ffmpeg's progress lines never fill the pipe.
            stderrTask = process.StandardError.ReadToEndAsync();
            // Endet die Audio-Spur vor dem Video (Ring-Lücke beim Sessionstart,
            // Geräte-Neustart → WriteAudioWindow liefert weniger Samples), beendet
            // ffmpeg mit -shortest die Mux beim Audio-Ende und schließt stdin früh. Das
            // Schreiben der Rest-Frames trifft dann eine tote Pipe (IOException), und der
            // äußere catch löschte bisher den eigentlich gültigen Output und blockierte
            // bis zum 5-min-EncodeTimeout (HIGH, 2026-09-16). Ein solcher Early-Exit wird
            // nur anerkannt, wenn ffmpeg mit 0 endete und eine nicht-leere Datei hinterlässt
            // — jede andere IOException bleibt ein echter Encode-Fehler.
            foreach (var (_, jpeg) in windowed)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(jpeg, encodeCt);
                }
                catch (IOException)
                {
                    // Der Guard entscheidet, ob der Early-Exit regulär war (siehe oben).
                    if (await FfmpegCommand.IsRegularEarlyExitAsync(process, outputPath))
                    {
                        break;
                    }
                    throw;
                }
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Auch das Close kann regulär früh enden: bei einem Low-Motion-Clip passten
                // die letzten Frames noch in den Pipe-Puffer, bevor ffmpeg stdin schloss —
                // dann schlug bisher das unbewachte Close fehl und der äußere catch löschte
                // den gültigen Output. Denselben Guard anwenden (LOW, 2026-09-16).
                if (!await FfmpegCommand.IsRegularEarlyExitAsync(process, outputPath))
                {
                    throw;
                }
            }

            await process.WaitForExitAsync(encodeCt);
            var stderr = await stderrTask!;
            if (process.ExitCode != 0)
            {
                App.Log($"ffmpeg failed (exit {process.ExitCode}): {stderr}");
                TryDelete(outputPath);
                return null;
            }

            // -shortest ends the file at the shorter stream, so the real duration is the
            // min of the two when audio is present.
            var videoDuration = windowed.Count / (double)fps;
            var duration = audio.FilePath is null ? videoDuration : Math.Min(videoDuration, audio.DurationSeconds);
            return new ClipResult(outputPath, duration);
        }
        catch (Exception ex)
        {
            // On cancel/timeout ffmpeg is still running and holds the output file open —
            // TryDelete would fail and leave an orphan partial MP4. Kill the tree first,
            // so the file is actually deletable; the finally repeats the kill (no-op).
            KillProcessTreeAndWaitForExit(process, started);
            TryDelete(outputPath);
            // stderr-Task NACH dem Kill beobachten: hängt der Encoder, schließt erst der
            // Kill die stderr-Pipe, sodass ReadToEndAsync hier zu Ende kommt (statt ewig zu
            // blockieren) — und der ffmpeg-stderr landet für die Diagnose im Log statt
            // unobserved zu faulten, wenn process.Dispose() die Streams schließt
            // (LOW, 2026-09-16).
            var stderr = string.Empty;
            if (stderrTask is not null)
            {
                try
                {
                    stderr = await stderrTask;
                }
                catch (Exception readEx)
                {
                    App.Log($"Clip encode stderr read failed: {readEx.Message}");
                }
            }

            App.Log($"Clip encode failed: {ex.Message}{(stderr.Length == 0 ? string.Empty : $" — stderr: {stderr}")}");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                // ffmpeg must never survive a cancel/timeout: it would keep the output
                // file open (TryDelete fails) and leave a clip with no DB entry, and a
                // hung process would hold the capture save gate forever. Kill the tree.
                KillProcessTreeAndWaitForExit(process, started);
                process.Dispose();
            }

            if (audio.FilePath is not null)
            {
                TryDelete(audio.FilePath);
            }
        }
    }

    /// <summary>Terminates ffmpeg if it is still running and waits (bounded) for it to
    /// exit. Kill alone returns before the child has released its file handles, so a
    /// TryDelete right after would hit a sharing violation and leave an orphan partial
    /// MP4 behind (MEDIUM, 2026-09-16). Idempotent — called from both the catch (before
    /// the output file is deleted) and the finally.</summary>
    private static void KillProcessTreeAndWaitForExit(Process? process, bool started)
    {
        if (process is null || !started || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone — nothing left to kill.
        }

        try
        {
            // Bounded: ein weiterhin hängendes Kind darf die Save-Gate nicht länger als
            // 2 s blockieren. WaitForExit lässt den Kernel die Output-Handles schließen.
            process.WaitForExit(2000);
        }
        catch
        {
            // Kill-Exited-Race: WaitForExit auf einen soeben beendeten Prozess wirft.
        }
    }

    /// <summary>A sliced audio window written to a temp WAV (null FilePath = no audio).</summary>
    private sealed record AudioSlice(string? FilePath, double DurationSeconds);

    /// <summary>Slices [fromUtc, toUtc) out of the PCM ring and writes it to a temp WAV
    /// file. Returns an empty slice when there is no audio or the window is empty.</summary>
    private static AudioSlice WriteAudioWindow(AudioRingBuffer? ring, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (ring is null)
        {
            return new AudioSlice(null, 0);
        }

        var samples = ring.Take(fromUtc, toUtc);
        if (samples is null || samples.Length == 0)
        {
            return new AudioSlice(null, 0);
        }

        var path = Path.Combine(Path.GetTempPath(), $"erc-clip-{Guid.NewGuid():N}.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(ring.SampleRate, ring.Channels);
        try
        {
            using (var writer = new WaveFileWriter(path, format))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }
        }
        catch
        {
            // Schlägt der WAV-Write fehl (Disk voll / Temp-Pfad problematisch), bleibt die
            // teilweise geschriebene Datei sonst ewig im %TEMP% liegen (LOW, 2026-09-16).
            TryDelete(path);
            throw;
        }

        var duration = samples.Length / (double)(ring.SampleRate * ring.Channels);
        return new AudioSlice(path, duration);
    }

    private void OnFrameCaptured(DateTimeOffset utc, byte[] jpeg)
    {
        // The frame pool fires at the display rate — throttle to the configured FPS.
        var settings = _settings();
        var fps = Math.Clamp(settings.Fps, 1, 60);
        var minInterval = TimeSpan.FromSeconds(1.0 / fps);
        lock (_gate)
        {
            // With 2 pool buffers two encodes can finish out of capture order — never
            // store an older frame than the newest one already accepted, or the store's
            // timestamp order (and the segment pruning that relies on it) breaks.
            if (utc < _lastFrameUtc || utc - _lastFrameUtc < minInterval)
            {
                return;
            }

            _lastFrameUtc = utc;
            _store?.Add(utc, jpeg);
        }
    }

    private async Task ManageLoopAsync(CancellationToken ct)
    {
        // Startup cleanup: clips older than 30 days are deleted once per launch.
        try
        {
            ClipStorage.CleanupOldClips(TimeSpan.FromDays(30));
        }
        catch (Exception ex)
        {
            App.Log($"Clip cleanup failed: {ex.Message}");
        }

        while (!ct.IsCancellationRequested)
        {
            // Der komplette Tick-Body ist gegen jeden Fehler abgesichert: ein Wurf hier
            // (z. B. WinRT-COM-Read in _frames.ItemWidth während eines Device-Lost-Rennen,
            // oder ein transienter Disk-/ACL-Fehler im RollingFrameStore-Ctor) würde den
            // Task.Run-Loop unbeobachtet sterben lassen — die Aufnahme bliebe für den Rest
            // des Prozesslaufs stumm, ohne Log-Eintrag (MEDIUM, 2026-09-16). Loggen und im
            // nächsten Tick weiter versuchen.
            try
            {
                var settings = _settings();
                var shouldCapture = _sessionActive && settings.Enabled;
                var window = TimeSpan.FromSeconds(Math.Max(30, settings.PreRollSeconds + settings.PostRollSeconds));

                lock (_gate)
                {
                    if (shouldCapture)
                    {
                        if (_store is null)
                        {
                            _store = new RollingFrameStore(BufferDirectory(), window);
                        }
                        else if (_store.Window != window)
                        {
                            // Settings changed the window — recreate the buffer.
                            _store.Dispose();
                            _store = new RollingFrameStore(BufferDirectory(), window);
                        }

                        // Resolution changed — restart capture so the frame pool is recreated
                        // at the new size (the pool is sized from MaxWidth at Start).
                        var maxWidth = Math.Clamp(settings.MaxWidth, 640, 7680);
                        if (_frames.MaxWidth != maxWidth)
                        {
                            _frames.Stop();
                            _frames.MaxWidth = maxWidth;
                            // Frames before the change are at the old width — a clip window
                            // spanning the change would mix sizes and fail the ffmpeg encode.
                            _store.Clear();
                            // Ein bereits in-flight Frame aus dem alten Pool (Surface-Copy lief
                            // schon vor dem Stop) hat einen Timestamp VOR dem Clear und würde die
                            // Throttle passieren und den leeren Store mit der alten Breite füllen.
                            // _lastFrameUtc auf jetzt setzen verwirft ihn (utc < _lastFrameUtc).
                            _lastFrameUtc = DateTimeOffset.UtcNow;
                        }

                        // Primary display resolution changed mid-session (e.g. the game switches
                        // borderless → fullscreen): the frame pool is sized to the old width
                        // and would keep delivering scaled/padded frames. Stop so the next tick
                        // recreates the pool at the new size. GraphicsCaptureItem exposes no
                        // SizeChanged in the .NET projection, so poll the item width instead.
                        // The first observation (after Start) only records the width — an
                        // unconditional Stop there would restart capture once for no reason.
                        var itemWidth = _frames.ItemWidth;
                        if (itemWidth is not null && itemWidth != _lastCaptureItemWidth)
                        {
                            if (_lastCaptureItemWidth is not null)
                            {
                                App.Log($"Screen capture item resized to {itemWidth}px wide — restarting capture.");
                                _frames.Stop();
                                // Frames before the change are at the old width — a clip window
                                // spanning the change would mix sizes and fail the ffmpeg encode.
                                _store.Clear();
                                // In-flight Frames aus dem alten Pool verwerfen (siehe oben).
                                _lastFrameUtc = DateTimeOffset.UtcNow;
                            }

                            _lastCaptureItemWidth = itemWidth;
                        }

                        // Idempotent — also retries after a failed start (locked desktop etc.).
                        _frames.Start();
                        _audio.Start();

                        // Window outgrew the audio ring — restart capture with a bigger buffer.
                        var ring = _audio.Ring;
                        if (ring is not null && ring.CapacitySeconds < window.TotalSeconds)
                        {
                            _audio.Stop();
                            _audio.Start();
                        }
                    }
                    else if (_store is not null)
                    {
                        _store.Dispose();
                        _store = null;
                        _frames.Stop();
                        _audio.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"Clip manage loop tick failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string BufferDirectory() =>
        Path.Combine(ClipStorage.RootPath(), "_buffer");

    private static string? FfmpegPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _store?.Dispose();
            _store = null;
            _frames.Dispose();
            _audio.Dispose();
        }

        _stop.Dispose();
    }
}
