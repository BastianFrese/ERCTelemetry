using System.IO;
using System.Speech.Synthesis;
using System.Threading.Channels;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Llm;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Tts;
using ERCTelemetry.Core.VoiceAlerts;
using NAudio.Wave;
using NLayer.NAudioSupport;

namespace ERCTelemetry.App.Composition;

/// <summary>Speaks German voice alerts (tyre wear, rain, race-control events for the
/// player) via Microsoft Neural voices (Edge-TTS). Consumes the dedicated
/// <see cref="AppServices.VoiceEvents"/> channel; the Core <see cref="VoiceAlertPlanner"/>
/// decides what to say and hands each alert to the Edge-TTS websocket client. The returned
/// MP3 plays through NAudio on a serialized playback task (no overlapping alerts); when the
/// synthesizer is unavailable (offline, service blocked) a single alert falls back to the
/// local Windows voice so it still gets spoken. Off by default — opt-in in
/// Settings → Verhalten. The settings are read per alert, so a toggle/voice change applies live.</summary>
public sealed class VoiceAlertService : IDisposable
{
    private readonly ChannelReader<StoreEvent> _events;
    private readonly ChannelReader<TelemetrySnapshot> _snapshots;
    private readonly ChannelReader<TelemetrySnapshot> _liveSnapshots;
    private readonly AppSettingsService _settings;
    private readonly VoiceAlertPlanner _planner = new();
    private readonly ProximitySpotter _spotter = new();
    private readonly LiveAnalysisDigestBuilder _digestBuilder = new();
    private readonly LiveAnalysisTrigger _trigger = new();
    private readonly LiveAnalysisTemplate _template = new();
    private readonly PitStopAdvisor _pitStopAdvisor = new();
    private readonly RivalDigestBuilder _rivalBuilder = new();
    private readonly RivalTemplate _rivalTemplate = new();
    private readonly TyreTempMonitor _tyreTempMonitor = new();
    private readonly LlmService? _llm;
    private readonly EdgeTtsClient _edgeTts = new();

    /// <summary>Local Windows voice — kept as the offline fallback (one alert at a time,
    /// so a failing network can never silence the pipeline).</summary>
    private readonly SpeechSynthesizer _synth = new();

    /// <summary>Synthesized MP3 waiting for the playback task. Bounded + DropOldest: on an
    /// alert flood the stale alerts drop and the newest ones still speak. Written by the
    /// speaking loop and the settings test button, so SingleWriter is off.</summary>
    private readonly Channel<byte[]> _audio =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Texts waiting to be synthesized. Bounded + DropOldest: when alerts pile up
    /// faster than they can be spoken, the stale ones drop instead of being said late.
    /// Written by the three analysis loops and the fire-and-forget LLM tasks, so
    /// SingleWriter is off.</summary>
    private readonly Channel<string> _pending =
        Channel.CreateBounded<string>(new BoundedChannelOptions(8)
        {
            SingleWriter = false,
            SingleReader = true, // SpeakLoopAsync is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Serializes the LLM calls (live analysis + rival) so a slow model can never
    /// stack concurrent requests; the deterministic alerts are not gated by this.</summary>
    private readonly SemaphoreSlim _llmGate = new(1, 1);

    private readonly CancellationTokenSource _stop = new();
    private Task? _task;
    private Task? _proximityTask;
    private Task? _liveAnalysisTask;
    private Task? _playbackTask;
    private Task? _speakTask;

    /// <summary>True once the final classification is in — the game keeps sending motion
    /// packets during the cool-down lap and results screen, so the proximity spotter is
    /// reset once on the transition and not fed again until a new session starts.</summary>
    private bool _raceOver;

    public VoiceAlertService(AppServices services, AppSettingsService settings)
    {
        _events = services.VoiceEvents.Reader;
        _snapshots = services.VoiceSnapshots.Reader;
        _liveSnapshots = services.LiveAnalysisSnapshots.Reader;
        _settings = settings;
        _llm = services.Llm;
        _task = Task.Run(() => RunAsync(_stop.Token));
        _proximityTask = Task.Run(() => RunProximityAsync(_stop.Token));
        _liveAnalysisTask = Task.Run(() => RunLiveAnalysisAsync(_stop.Token));
        _playbackTask = Task.Run(() => PlaybackAsync(_stop.Token));
        _speakTask = Task.Run(() => SpeakLoopAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _events.WaitToReadAsync(ct))
            {
                while (_events.TryRead(out var storeEvent))
                {
                    // The planner always tracks state (tyre-stint dedup) so re-enabling
                    // mid-session keeps working; only the speaking is gated. The overtake
                    // language is read per alert, so a change applies live.
                    _planner.Language = _settings.Current.VoiceLanguage;
                    foreach (var text in _planner.Plan(storeEvent))
                    {
                        if (_settings.Current.VoiceAlertsEnabled)
                        {
                            EnqueueSpeak(text);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Feeds the proximity spotter the freshest snapshot at 4 Hz (250 ms) and
    /// speaks its calls. Drains the channel to the latest snapshot so a burst of packets
    /// never queues stale positions — the spotter only ever sees the current field. The
    /// spotter tracks state (debounce, distance steps) so re-enabling mid-session keeps
    /// working; only the speaking is gated by the settings.</summary>
    private async Task RunProximityAsync(CancellationToken ct)
    {
        try
        {
            while (await _snapshots.WaitToReadAsync(ct))
            {
                TelemetrySnapshot? latest = null;
                while (_snapshots.TryRead(out var snapshot))
                {
                    latest = snapshot;
                }

                if (latest is not null)
                {
                    // The game keeps sending motion packets during the cool-down lap and
                    // results screen, so once the final classification is in, stop feeding
                    // the spotter — a finished race must not keep announcing stale zones.
                    if (latest.FinalResults.Count > 0)
                    {
                        if (!_raceOver)
                        {
                            _raceOver = true;
                            _spotter.Reset();
                        }
                    }
                    else
                    {
                        _raceOver = false;
                        _spotter.Language = _settings.Current.VoiceLanguage;
                        var alerts = _spotter.Update(
                            latest.Positions,
                            latest.Meta?.PlayerCarIndex ?? 255,
                            latest.Standings);
                        if (_settings.Current.VoiceAlertsEnabled && _settings.Current.ProximityAlertsEnabled)
                        {
                            foreach (var text in alerts)
                            {
                                EnqueueSpeak(text);
                            }
                        }
                    }
                }

                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Periodic live analysis (tyres / fuel / tempo trend / status update): the
    /// digest builder + trigger decide when something is worth saying, then the Layer-1
    /// template (free, no key) or the LLM (API key) formulates it. Drains to the latest
    /// snapshot so a burst never queues stale state; the builder/trigger track state so
    /// re-enabling mid-session keeps working. Only the speaking is gated by the settings.</summary>
    private async Task RunLiveAnalysisAsync(CancellationToken ct)
    {
        try
        {
            while (await _liveSnapshots.WaitToReadAsync(ct))
            {
                TelemetrySnapshot? latest = null;
                while (_liveSnapshots.TryRead(out var snapshot))
                {
                    latest = snapshot;
                }

                // No live analysis once the final classification is in — the cool-down lap
                // and results screen must not keep producing tyre/fuel/status commentary.
                if (latest is not null && latest.FinalResults.Count == 0)
                {
                    var digest = _digestBuilder.Build(latest);
                    if (digest is not null)
                    {
                        var speakEnabled = _settings.Current.VoiceAlertsEnabled &&
                                           _settings.Current.LiveAnalysisEnabled;

                        // Deterministic alerts first — enqueued immediately, never delayed
                        // by the LLM or TTS. Pit-stop timing (free, no LLM): speaks on
                        // recommendation transitions, once per stint. The gap to the car
                        // behind is the next standings row's gap to the car in front.
                        var playerRow = latest.Standings.FirstOrDefault(r => r.IsPlayer);
                        var carBehind = playerRow is null
                            ? null
                            : latest.Standings.FirstOrDefault(r => r.Position == playerRow.Position + 1);
                        var pitAdvice = _pitStopAdvisor.Evaluate(digest, carBehind?.GapToCarInFrontMs ?? 0);
                        if (pitAdvice is not null && speakEnabled)
                        {
                            EnqueueSpeak(pitAdvice.Reason);
                        }

                        // Tyre/brake temperature (deterministic, free — no LLM): warns on
                        // overheating with a 20 s cooldown so it cannot repeat every second.
                        var tyreTempAlert = _tyreTempMonitor.Evaluate(latest);
                        if (tyreTempAlert is not null && speakEnabled)
                        {
                            EnqueueSpeak(tyreTempAlert.Text);
                        }

                        // LLM/template analysis — fire-and-forget so a slow model never
                        // blocks the deterministic alerts above. The trigger is evaluated
                        // every cycle regardless of the enabled flag (state tracking).
                        var reason = _trigger.Evaluate(digest);
                        if (reason is not null && speakEnabled)
                        {
                            _ = AnalyzeAndSpeakAsync(digest, reason.Value, ct);
                        }

                        // Rival trends (Layer 1 free / Layer 2 with key): only notable
                        // actions (pit stop, tyre change, fast lap), never periodic.
                        var rivalDigest = _rivalBuilder.Update(latest);
                        if (rivalDigest is not null && speakEnabled)
                        {
                            _ = RivalAndSpeakAsync(rivalDigest, ct);
                        }
                    }
                }

                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Queues a text for synthesis + playback. Never blocks the caller — the
    /// speaking loop synthesizes and the playback task plays, both in the background.
    /// When the queue is full the oldest pending text drops (stale alerts are better
    /// skipped than said late). Gated on the master voice switch: the single point
    /// every producer goes through (event loop, proximity, live analysis AND the
    /// fire-and-forget LLM results) — with the whole feature off, nothing can be
    /// enqueued, not even a long-running LLM analysis that finished mid-toggle.</summary>
    private void EnqueueSpeak(string text)
    {
        if (!_settings.Current.VoiceAlertsEnabled)
        {
            return; // master switch off — nothing is spoken
        }

        if (!_pending.Writer.TryWrite(text))
        {
            App.Log("Sprach-Warteschlange voll — Ansage verworfen");
        }
    }

    /// <summary>Consumes the pending texts and hands each to <see cref="SpeakAsync"/>.
    /// Runs concurrently with playback, so the next alert's synthesis overlaps the
    /// current one's playback instead of waiting for it. If the master switch turns
    /// off, every queued alert is dropped (<c>continue</c>) — the loop keeps draining
    /// silently until the queue is empty, so no already-enqueued alert plays late.</summary>
    private async Task SpeakLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(ct))
            {
                while (_pending.Reader.TryRead(out var text))
                {
                    if (!_settings.Current.VoiceAlertsEnabled)
                    {
                        continue; // voice output turned off — drop pending alerts
                    }

                    await SpeakAsync(text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Layer-1 template or Layer-2 LLM live analysis, then speaks the result.
    /// Fire-and-forget from the analysis loop; the gate serializes the LLM calls so a
    /// slow model can never stack concurrent requests.</summary>
    private async Task AnalyzeAndSpeakAsync(LiveAnalysisDigest digest, LiveAnalysisTriggerReason reason, CancellationToken ct)
    {
        await _llmGate.WaitAsync(ct);
        try
        {
            var text = _llm is { IsConfigured: true }
                ? await _llm.LiveAnalyzeAsync(digest, reason, ct)
                : await _template.AnalyzeAsync(digest, reason, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                EnqueueSpeak(text);
            }
        }
        finally
        {
            _llmGate.Release();
        }
    }

    /// <summary>Layer-1 template or Layer-2 LLM rival analysis, then speaks the result.
    /// Fire-and-forget from the analysis loop; shares the LLM gate with the live analysis.</summary>
    private async Task RivalAndSpeakAsync(RivalDigest rivalDigest, CancellationToken ct)
    {
        await _llmGate.WaitAsync(ct);
        try
        {
            var text = _llm is { IsConfigured: true }
                ? await _llm.RivalAnalyzeAsync(rivalDigest, ct)
                : await _rivalTemplate.AnalyzeAsync(rivalDigest, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                EnqueueSpeak(text);
            }
        }
        finally
        {
            _llmGate.Release();
        }
    }

    /// <summary>Synthesizes the alert with the selected voice and queues it for playback;
    /// on ANY failure (offline, auth) the local Windows voice speaks it instead.</summary>
    private async Task SpeakAsync(string text)
    {
        var audio = await _edgeTts.SynthesizeAsync(text, _settings.Current.Voice ?? EdgeTtsVoices.DefaultVoice, _stop.Token);
        if (audio is { Length: > 0 })
        {
            if (!_audio.Writer.TryWrite(audio))
            {
                App.Log("TTS-Queue voll — Ansage verworfen");
            }

            return;
        }

        FallbackSpeak(text);
    }

    private void FallbackSpeak(string text)
    {
        // Two loops (event + proximity) can hit the fallback at the same time — the
        // Windows synthesizer is not thread-safe, so serialize the calls.
        lock (_synth)
        {
            try
            {
                _synth.SpeakAsync(text);
            }
            catch (Exception ex)
            {
                // A missing voice or a busy audio device must never take the pipeline down.
                App.Log($"TTS failed: {ex.Message}");
            }
        }
    }

    /// <summary>Plays the queued MP3s one at a time on a dedicated thread — real-time audio,
    /// so a long alert simply delays the next one instead of overlapping it. If the master
    /// switch turns off, queued clips are dropped instead of played, so the toggle stops
    /// the voice output as fast as the currently playing sentence can finish.</summary>
    private async Task PlaybackAsync(CancellationToken ct)
    {
        try
        {
            while (await _audio.Reader.WaitToReadAsync(ct))
            {
                while (_audio.Reader.TryRead(out var audio))
                {
                    if (!_settings.Current.VoiceAlertsEnabled)
                    {
                        continue; // voice output turned off — drop queued audio
                    }

                    try
                    {
                        PlayMp3(audio, ct);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"TTS-Wiedergabe fehlgeschlagen: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Decodes the MP3 (NLayer, pure managed — no external codecs) and plays it to
    /// the default device until it ends or the app shuts down.</summary>
    private static void PlayMp3(byte[] mp3, CancellationToken ct)
    {
        using var stream = new MemoryStream(mp3);
        var decompressor = new Mp3FileReaderBase.FrameDecompressorBuilder(wave => new Mp3FrameDecompressor(wave));
        using var reader = new Mp3FileReaderBase(stream, decompressor);
        using var output = new WaveOut();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            if (ct.IsCancellationRequested)
            {
                output.Stop();
                break;
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>Settings "Test" button — speaks a sample phrase so the user can verify the
    /// selected voice works. No-op while the feature is off.</summary>
    public void SpeakTest()
    {
        if (!_settings.Current.VoiceAlertsEnabled)
        {
            return;
        }

        var testPhrase = _settings.Current.VoiceLanguage == VoiceLanguage.English
            ? "Voice output works."
            : "Sprachausgabe funktioniert.";
        _ = SpeakAsync(testPhrase);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _audio.Writer.TryComplete();
        _pending.Writer.TryComplete();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown mid-speak.
        }

        try
        {
            _proximityTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown mid-speak.
        }

        try
        {
            _liveAnalysisTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown mid-analysis.
        }

        try
        {
            _playbackTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown mid-play.
        }

        try
        {
            _speakTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown mid-synthesis.
        }

        _stop.Dispose();
        // _llmGate is deliberately NOT disposed: a fire-and-forget LLM analysis
        // (AnalyzeAndSpeakAsync/RivalAndSpeakAsync) can still be running past the 1-s wait
        // above and then Release() in its finally — disposing the gate would throw
        // ObjectDisposedException in an unobserved task. App-lifetime gate; GC cleans up.
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }
}
