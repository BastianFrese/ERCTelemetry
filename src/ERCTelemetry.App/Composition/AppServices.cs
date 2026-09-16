using System.Net.Sockets;
using System.Threading.Channels;
using F1Game.UDP.Packets;
using ERCTelemetry.App.Clips;
using ERCTelemetry.App.Share;
using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Share;
using ERCTelemetry.Core.Telemetry;

namespace ERCTelemetry.App.Composition;

/// <summary>Owns the telemetry pipeline: UDP listener → packet channel → aggregator
/// (single writer of the session store) → snapshot + event channels. Note: each channel
/// is a competing-consumer queue — it delivers every item to exactly ONE reader. Separate
/// consumers get their own channel pairs fed by the aggregator (see OverlaySnapshots/
/// OverlayEvents; persistence adds its own in Phase 5).</summary>
public sealed class AppServices : IDisposable
{
    private readonly PacketRecorder _recorder = new();
    private readonly CancellationTokenSource _stopAggregator = new();
    private readonly Task _aggregatorTask;
    private readonly TelemetryDb _db;
    private readonly PersistencePump _persistence;
    private readonly ScreenCaptureService _screenCapture;
    private readonly ClipRecorderService _clipRecorder;
    private readonly UdpForwarder _forwarder;
    private UdpListener? _listener;
    private int _disposed;

    /// <summary>No packet for this long → the game closed or the user left the session
    /// without a FinalClassification. The open session is finalized so it shows in
    /// history instead of staying "open" forever.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    public AppServices()
        : this(null)
    {
    }

    public AppServices(TelemetryDb? db, AppSettingsService? settings = null)
    {
        SessionStore = new SessionStateStore();
        _db = db ?? new TelemetryDb();
        _persistence = new PersistencePump(this, _db);
        Settings = settings;
        // Clip settings are read every tick, so changes apply without a restart.
        _screenCapture = new ScreenCaptureService(() => settings?.Current.Clips ?? new ClipSettings());
        _clipRecorder = new ClipRecorderService(this, _screenCapture);
        // Share settings are read per upload, so a token change applies without a restart.
        // The resolver auto-fetches the current server token via /api/config when no
        // explicit token is configured — Enduser share without any setup (see
        // ShareTokenResolver), while a configured token still wins (operator rotation).
        var shareTokenResolver = new ShareTokenResolver(
            () => settings?.Current.ShareBaseUrl, () => settings?.Current.ShareToken);
        Share = new ShareService(() => settings?.Current.ShareBaseUrl, shareTokenResolver);
        // ERC send settings are read per call, so a key/URL save applies without a restart.
        ErcRace = new ErcRaceSender(() => settings?.Current.ErcApiUrl, () => settings?.Current.ErcApiKey);
        // LLM layer reads settings live (rebuilt on key/model change); null without a
        // settings service (tests) — every consumer treats it as "Layer 1 only".
        Llm = settings is null ? null : new LlmService(settings);
        // Forwarding targets are read per datagram, so a settings save applies live.
        _forwarder = new UdpForwarder(() => settings?.Current ?? new AppSettings());
        _aggregatorTask = Task.Run(() => RunAggregatorAsync(_stopAggregator.Token));
    }

    public PacketStats Stats { get; } = new();

    public SessionStateStore SessionStore { get; }

    /// <summary>Effective app settings (null when constructed without one — tests). The
    /// clip services read <see cref="AppSettingsService.Current"/> live.</summary>
    public AppSettingsService? Settings { get; }

    public Channel<UnionPacket> Packets { get; } =
        Channel.CreateBounded<UnionPacket>(new BoundedChannelOptions(1024)
        {
            SingleReader = true, // aggregator is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public Channel<TelemetrySnapshot> Snapshots { get; } =
        Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(64)
        {
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public Channel<StoreEvent> Events { get; } =
        Channel.CreateBounded<StoreEvent>(new BoundedChannelOptions(1024)
        {
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Collision-clip feed — the ClipRecorderService's own channel (per the
    /// competing-consumer rule: channels deliver to exactly one reader).</summary>
    public Channel<StoreEvent> ClipEvents { get; } =
        Channel.CreateBounded<StoreEvent>(new BoundedChannelOptions(128)
        {
            SingleReader = true, // ClipRecorderService is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Overlay-only feeds — the overlay server's single reader works these instead
    /// of <see cref="Snapshots"/>/<see cref="Events"/>, which belong to the WPF dashboard
    /// (persistence gets its own pair in Phase 5).</summary>
    public Channel<TelemetrySnapshot> OverlaySnapshots { get; } =
        Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(16)
        {
            SingleReader = true, // OverlayServer pump is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public Channel<StoreEvent> OverlayEvents { get; } =
        Channel.CreateBounded<StoreEvent>(new BoundedChannelOptions(128)
        {
            SingleReader = true, // OverlayServer pump is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Feed of the in-game WPF overlay window (Phase 4) — a third consumer with
    /// its own channel, per the competing-consumer rule.</summary>
    public Channel<TelemetrySnapshot> OverlayWindowSnapshots { get; } =
        Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(8)
        {
            SingleReader = true, // the overlay window's 30 fps DispatcherTimer is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Race-control events for the in-game overlay window's feed widget — its own
    /// channel so the HUD never competes with the overlay server's <see cref="OverlayEvents"/>.</summary>
    public Channel<StoreEvent> OverlayWindowEvents { get; } =
        Channel.CreateBounded<StoreEvent>(new BoundedChannelOptions(64)
        {
            SingleReader = true, // the overlay window's consume loop is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Voice-alert feed — the VoiceAlertService's own channel (per the
    /// competing-consumer rule: channels deliver to exactly one reader).</summary>
    public Channel<StoreEvent> VoiceEvents { get; } =
        Channel.CreateBounded<StoreEvent>(new BoundedChannelOptions(64)
        {
            SingleReader = true, // VoiceAlertService is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Finished-league-race feed — the ErcRacePromptService's own channel (per
    /// the competing-consumer rule: channels deliver to exactly one reader). The persistence
    /// pump writes one ErcRaceEndedEvent per FinalClassification; prompts are rare, so a
    /// bounded queue that drops never hurts (a racing backlog is user-closed immediately).</summary>
    public Channel<ErcRaceEndedEvent> ErcRaceEnded { get; } =
        Channel.CreateBounded<ErcRaceEndedEvent>(new BoundedChannelOptions(8)
        {
            SingleReader = true, // ErcRacePromptService is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Proximity-spotter feed — the VoiceAlertService's own snapshot channel
    /// (per the competing-consumer rule: channels deliver to exactly one reader). The
    /// spotter needs positions, which the event feed does not carry.</summary>
    public Channel<TelemetrySnapshot> VoiceSnapshots { get; } =
        Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(8)
        {
            SingleReader = true, // VoiceAlertService is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Live-analysis feed — the VoiceAlertService's own snapshot channel (per the
    /// competing-consumer rule: channels deliver to exactly one reader). The periodic
    /// digest/trigger loop needs the full snapshot (lap times, tyres, fuel), which the
    /// event feed does not carry.</summary>
    public Channel<TelemetrySnapshot> LiveAnalysisSnapshots { get; } =
        Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(8)
        {
            SingleReader = true, // VoiceAlertService is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Twitch chat-bot feed — the TwitchChatHost's own channel (per the
    /// competing-consumer rule: channels deliver to exactly one reader).</summary>
    public Channel<TelemetrySnapshot> TwitchSnapshots { get; } =
        Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(8)
        {
            SingleReader = true, // TwitchChatHost is the only reader
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public bool IsListening => _listener is not null;

    /// <summary>Replay pump writing recorded packets into the packet channel, null when
    /// idle. Live listener and replay are mutually exclusive (StartReplay stops the
    /// listener) so a replayed session cannot interleave with live packet streams.</summary>
    public ReplayPlayer? Replay { get; private set; }

    public bool IsReplaying => Replay is { IsPlaying: true };

    /// <summary>UDP port the listener is actually bound to, null when stopped. Lets the
    /// Settings tab detect a port change and restart the listener.</summary>
    public int? ListeningPort => _listener?.LocalPort;

    public TelemetryDb Database => _db;

    /// <summary>Session sharing (manifest + clip upload to the share server). Settings
    /// are read per call, so a token change applies without a restart.</summary>
    public ShareService Share { get; }

    /// <summary>ERC admin-review upload (league list + finished-race send to erdi-erc.de).
    /// Settings are read per call, so a key/URL save applies without a restart.</summary>
    public ErcRaceSender ErcRace { get; }

    /// <summary>Optional LLM layer (Layer 2): commentary/coach/summary politur via Ollama.
    /// Null when constructed without a settings service (tests) — consumers fall back to
    /// Layer 1. Settings are read live, so a key/model save applies without a restart.</summary>
    public LlmService? Llm { get; }

    public FormatState FormatState => _listener?.CurrentFormatState ?? FormatState.Unknown;

    public bool IsRecording => _recorder.IsRecording;

    /// <summary>Single consumer: drains packets, applies them to the store and publishes
    /// one immutable snapshot per drain cycle plus any store events. A single bad packet
    /// (e.g. mid-rejoin frames with half-initialised participant slots) must never kill
    /// the aggregator — that froze ALL telemetry feeds until the app was restarted.
    /// When no packet arrives for <see cref="IdleTimeout"/> the open session is finalized
    /// (game closed / user left the lobby without a FinalClassification).</summary>
    private async Task RunAggregatorAsync(CancellationToken ct)
    {
        var pendingEvents = new List<StoreEvent>(4);
        while (true)
        {
            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(IdleTimeout);
                var hasPacket = await Packets.Reader.WaitToReadAsync(idleCts.Token);
                if (!hasPacket)
                {
                    break; // channel completed — shutdown
                }

                while (Packets.Reader.TryRead(out var packet))
                {
                    try
                    {
                        pendingEvents.AddRange(SessionStore.Apply(packet));
                    }
                    catch (Exception ex)
                    {
                        // Log + skip the packet; the pipeline (and every live view) stays alive.
                        App.Log($"Aggregator: applying {packet.PacketType} failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // No packets for IdleTimeout — the game closed or the user left the session
                // without a FinalClassification. Finalize the open session so it shows in
                // history instead of staying "open" forever. A packet may have arrived just
                // as the timer fired (the timer's cancellation can win the scheduling race
                // over the channel signal) — only finalize when the channel is truly empty.
                if (Packets.Reader.Count == 0)
                {
                    var idleEnded = SessionStore.CheckIdle(DateTimeOffset.UtcNow, IdleTimeout);
                    if (idleEnded is not null)
                    {
                        pendingEvents.Add(idleEnded);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"Aggregator cycle failed: {ex.Message}");
            }

            try
            {
                foreach (var storeEvent in pendingEvents)
                {
                    await Events.Writer.WriteAsync(storeEvent, ct);
                    OverlayEvents.Writer.TryWrite(storeEvent); // overlay drop is fine (bounded)
                    OverlayWindowEvents.Writer.TryWrite(storeEvent); // HUD feed drop is fine (bounded)
                    ClipEvents.Writer.TryWrite(storeEvent); // clip recorder drop is fine (bounded)
                    VoiceEvents.Writer.TryWrite(storeEvent); // voice-alert drop is fine (bounded)
                }

                pendingEvents.Clear();
                var snapshot = SessionStore.BuildSnapshot();
                Snapshots.Writer.TryWrite(snapshot);
                OverlaySnapshots.Writer.TryWrite(snapshot);
                OverlayWindowSnapshots.Writer.TryWrite(snapshot);
                TwitchSnapshots.Writer.TryWrite(snapshot); // chat-bot drop is fine (bounded)
                VoiceSnapshots.Writer.TryWrite(snapshot); // proximity-spotter drop is fine (bounded)
                LiveAnalysisSnapshots.Writer.TryWrite(snapshot); // live-analysis drop is fine (bounded)
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Snapshot build / event write failed without touching a packet — keep the
                // loop alive too (the next WaitToReadAsync gates the retry).
                App.Log($"Aggregator cycle failed: {ex.Message}");
            }
        }
    }

    /// <summary>Starts listening on the given UDP port. Throws <see cref="InvalidOperationException"/>
    /// when the port is unavailable (in use or blocked).</summary>
    public void StartListening(int port)
    {
        if (_listener is not null)
        {
            return;
        }

        try
        {
            _listener = new UdpListener(port, Packets.Writer, Stats, RawTap);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Cannot bind UDP port {port}. Another app may be using it " +
                $"(or Windows Firewall is blocking it — allow ERCTelemetry on private networks).", ex);
        }

        _listener.StartAsync();
    }

    public void StopListening()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>Starts replaying a .f1rec recording into the pipeline. Stops the live
    /// listener first — replay and live packets must not interleave in the store.</summary>
    public void StartReplay(string path, double speed = 1.0)
    {
        StopReplay();
        StopListening();
        var player = new ReplayPlayer(Packets.Writer, Stats);
        player.Failed += ex => App.Log($"Replay failed: {ex.Message}");
        Replay = player;
        player.Play(path, speed);
    }

    public void StopReplay()
    {
        Replay?.Stop();
        Replay = null;
    }

    public void StartRecording(string path)
    {
        _recorder.Start(path);
    }

    public void StopRecording()
    {
        _recorder.Stop();
    }

    private void RawTap(ReadOnlyMemory<byte> data)
    {
        // Forward the raw datagram before any format validation — the receiving app
        // parses it itself, so even packets our parser does not know must pass through.
        _forwarder.Forward(data);

        if (_recorder.IsRecording)
        {
            _recorder.Record(data.Span);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopRecording();
        _forwarder.Dispose();
        StopListening();
        StopReplay();

        // Let the aggregator finish first: it is the single writer of the snapshot/event
        // channels, so completing them (or tearing down persistence) before it drains the
        // queued packets could drop the session's last laps/results.
        Packets.Writer.TryComplete();
        try
        {
            _aggregatorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
            e => e is OperationCanceledException or ChannelClosedException))
        {
            // Ordered teardown: cancelled or channel-completed mid-wait.
        }
        catch (AggregateException ex)
        {
            App.Log($"Aggregator stopped with unexpected exceptions: {ex}");
        }

        _stopAggregator.Cancel();

        // Stop the clip pipeline before the event feed completes: the recorder writes
        // ClipSaved into Events, so it must be done before the pump drains the last items.
        _clipRecorder.Dispose();
        _screenCapture.Dispose();

        // Complete the event feed so the persistence pump drains everything still queued
        // into the DB before the database itself closes.
        Events.Writer.TryComplete();
        _persistence.Dispose();
        _db.Dispose();

        Snapshots.Writer.TryComplete();
        OverlaySnapshots.Writer.TryComplete();
        OverlayEvents.Writer.TryComplete();
        OverlayWindowSnapshots.Writer.TryComplete();
        OverlayWindowEvents.Writer.TryComplete();
        VoiceEvents.Writer.TryComplete();
        VoiceSnapshots.Writer.TryComplete();
        LiveAnalysisSnapshots.Writer.TryComplete();
        TwitchSnapshots.Writer.TryComplete();
        ErcRaceEnded.Writer.TryComplete(); // prompt service drains any final race-end
        _stopAggregator.Dispose();
    }
}