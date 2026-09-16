using System.Net;
using ERCTelemetry.Core;
using ERCTelemetry.App.Composition;
using ERCTelemetry.Core.Commentary;
using ERCTelemetry.Core.Llm;
using ERCTelemetry.Core.OverlayProtocol;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Tracks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ERCTelemetry.App.OverlayServer;

/// <summary>Kestrel-based local web server for OBS browser-source overlays. Binds to
/// 127.0.0.1 only (OBS runs on this machine — no firewall prompt), serves the static
/// overlay pages from wwwroot and the /ws WebSocket hub. Owns its dedicated snapshot and
/// event channels so it never competes with the WPF UI consumer.</summary>
public sealed class OverlayWebHost : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayWebSocketHub _hub = new();
    private readonly CommentaryPlanner _commentary = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly LlmService? _llm;
    private readonly SemaphoreSlim _llmGate = new(1, 1);
    private readonly object _recentLinesLock = new();
    private readonly List<string> _recentLines = new(20);
    private TelemetrySnapshot? _lastSnapshot;
    // Track-layout fitting for the OBS minimap: session-scoped fitter fed from the same
    // snapshots the map stream uses — never shared with the HUD window's own instance.
    private TrackFitter? _mapFitter;
    private TrackLayout? _mapLayout;
    private ulong _mapFitterSessionUid;
    private bool _mapStartFinishSent;
    private WebApplication? _app;
    private Task? _pumpTask;
    private Task? _llmTask;
    private CancellationTokenSource? _pumpCts;
    private int _port;

    public OverlayWebHost(AppServices services)
    {
        _services = services;
        _llm = services.Llm;
        _hub.LayoutProvider = BuildLayout; // the pump owns the fitter, so it owns the answer
    }

    /// <summary>The port the server actually bound, 0 before <see cref="StartAsync"/>.</summary>
    public int Port => _port;

    public bool IsRunning => _app is not null;

    public int ClientCount => _hub.ClientCount;

    /// <summary>Resolves the active overlay color scheme (null = classic) at send time;
    /// wired from the settings service so OBS pages switch schemes live on the next
    /// hello/state message.</summary>
    public Func<string?>? ColorSchemeProvider
    {
        get => _hub.ColorSchemeProvider;
        set => _hub.ColorSchemeProvider = value;
    }

    /// <summary>Resolves the block-visibility map for the <c>config</c> message at send
    /// time; wired from the settings service so pages pick up visibility toggles.</summary>
    public Func<IReadOnlyDictionary<string, bool>?>? ConfigProvider
    {
        get => _hub.ConfigProvider;
        set => _hub.ConfigProvider = value;
    }

    /// <summary>Resolves the fitted circuit layout for the <c>tracklayout</c> message at
    /// send time; fed by the pump's own <see cref="ERCTelemetry.Core.Tracks.TrackFitter"/>
    /// so a mid-session browser source gets the finished track immediately. Null before a
    /// layout exists.</summary>
    public Func<OverlayTrackLayout?>? LayoutProvider
    {
        get => _hub.LayoutProvider;
        set => _hub.LayoutProvider = value;
    }

    /// <summary>Starts the web host and the snapshot pump. When a host is already running
    /// it is torn down first, so the call always lands the server on the requested port —
    /// a concurrent restart can never leave Kestrel serving the old port while the caller
    /// reports the new one. Throws when the port cannot be bound, leaving no half-started
    /// state behind (IsRunning stays honest, Port is reset, no pump); after dispose it
    /// returns silently.</summary>
    public async Task StartAsync(int port)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            if (_app is not null)
            {
                // A (re)start must land the server on the requested port — tear the
                // existing app/pump down first instead of silently keeping it.
                await StopClientsLockedAsync().ConfigureAwait(false);
            }

            _app = BuildApp(port);
            CancellationTokenSource pumpCts;
            try
            {
                // A concurrent Dispose() can dispose _stop between the gate-entry check
                // and here — treat the ObjectDisposedException as "dispose won the race"
                // and abandon the start instead of surfacing it as a spurious failure.
                pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            }
            catch (ObjectDisposedException)
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
                return;
            }

            _pumpCts = pumpCts;
            try
            {
                await _app.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Bind failure: leave no half-initialized state behind — IsRunning must stay
                // honest and the documented "restart with the old port" recovery must work.
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
                _pumpCts.Dispose();
                _pumpCts = null;
                throw;
            }

            _port = ReadBoundPort(port);
            _pumpTask = Task.Run(() => PumpAsync(pumpCts.Token));
            _llmTask = Task.Run(() => LlmCommentaryLoopAsync(pumpCts.Token));

            if (_stop.IsCancellationRequested)
            {
                // Dispose() ran while this start was in flight — tear the freshly started
                // app back down so no ownerless Kestrel survives it.
                await StopClientsLockedAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Restarts the server on a new port (Settings tab). Only the web app and pump
    /// cycle — the overlay pages' JS auto-reconnects their sockets within a few seconds.
    /// Throws if the new port cannot be bound; in that case the server is DOWN (restart
    /// with the old port to recover).</summary>
    public async Task RestartAsync(int port)
    {
        if (_stop.IsCancellationRequested)
        {
            throw new InvalidOperationException("Overlay host was disposed.");
        }

        await StopClientsAsync().ConfigureAwait(false);
        await StartAsync(port).ConfigureAwait(false);
    }

    /// <summary>Pushes the current block-visibility config to every connected overlay
    /// client (settings save). No-op when the host is not running or no provider is set.</summary>
    public void PushOverlayConfig()
    {
        if (_app is not null)
        {
            _hub.PublishConfig();
        }
    }

    /// <summary>Forces the next state message out immediately regardless of the 5 Hz timer
    /// and change detection (settings save — the color scheme rides on state). No-op when
    /// the host is not running.</summary>
    public void ForceState()
    {
        if (_app is not null)
        {
            _hub.ForceState();
        }
    }

    /// <summary>Stops serving clients and the pump, keeping <see cref="_stop"/> (and thus
    /// the ability to restart) intact. Holds <see cref="_startGate"/> across the whole
    /// teardown — cancelling the pump, waiting for it and disposing the app must be one
    /// critical section, otherwise a concurrent StartAsync could interleave and leave
    /// two live pumps on the SingleReader overlay channels.</summary>
    private async Task StopClientsAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopClientsLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Teardown proper — the caller must hold <see cref="_startGate"/>. Cancels
    /// the pump-scoped token so the pump task really exits instead of lingering as a
    /// competing reader on the SingleReader channels, waits for it briefly, then stops
    /// and disposes the web app.</summary>
    private async Task StopClientsLockedAsync()
    {
        var pumpTask = _pumpTask;
        var llmTask = _llmTask;
        var pumpCts = _pumpCts;
        _pumpTask = null;
        _llmTask = null;
        _pumpCts = null;

        pumpCts?.Cancel();

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AggregateException or OperationCanceledException or TimeoutException)
            {
                // Pump was mid-write when cancelled; clients are shutting down anyway.
            }
        }

        if (llmTask is not null)
        {
            try
            {
                await llmTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AggregateException or OperationCanceledException or TimeoutException)
            {
                // LLM loop was mid-call when cancelled; the gate is dropped with the host.
            }
        }

        // Dispose the CTS only when the wait observed completion: if the pump hangs past
        // the timeout, leaking it is safer than disposing a token PumpAsync's
        // Task.Delay(200, ct) may still hold (that would throw ODE as an unobserved task
        // exception).
        if (pumpCts is not null && (pumpTask is null || pumpTask.IsCompleted))
        {
            pumpCts.Dispose();
        }

        if (_app is not null)
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        _port = 0; // Port must not outlive IsRunning — a stale port would copy a dead URL.
    }

    /// <summary>Reads the port Kestrel really bound from the server addresses feature so the
    /// Debug tab reports reality (e.g. when 8090 was taken and Kestrel picked another).</summary>
    private int ReadBoundPort(int requestedPort)
    {
        var server = _app!.Services.GetService<IServer>();
        var addresses = server?.Features.Get<IServerAddressesFeature>();
        foreach (var address in addresses?.Addresses ?? Enumerable.Empty<string>())
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                return uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            }
        }

        return requestedPort;
    }

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory, // wwwroot is copied next to the exe
        });
        builder.Logging.ClearProviders(); // WPF app: no console to spam
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port); // loopback only — OBS is on this machine
        });

        var app = builder.Build();

        // Loopback-only server: reject requests whose Host header is not this machine —
        // closes the DNS-rebinding hole where a web page resolves a foreign hostname to
        // 127.0.0.1 and reads the local telemetry pages/WebSocket.
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        app.Map("/ws", (HttpContext context) => _hub.HandleAsync(context));

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                // OBS browser sources cache aggressively; never let them.
                context.Context.Response.Headers[HeaderNames.CacheControl] = "no-store";
            },
        });

        return app;
    }

    /// <summary>Pull loop for the overlay's own channels: forwards every store event
    /// immediately, coalesces snapshots to the latest and feeds state + player streams.
    /// A failing cycle is skipped (and retried after a short delay) instead of killing the
    /// pump — the overlays would silently freeze otherwise.</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _services.OverlaySnapshots.Reader.WaitToReadAsync(ct))
            {
                try
                {
                    while (_services.OverlayEvents.Reader.TryRead(out var storeEvent))
                    {
                        _hub.PublishEvent(storeEvent);
                        MarkStartFinishCrossing(storeEvent);
                        foreach (var line in _commentary.PlanEvent(storeEvent))
                        {
                            _hub.PublishCommentary(line);
                            AddRecentLine(line);
                        }
                    }

                    TelemetrySnapshot? latest = null;
                    while (_services.OverlaySnapshots.Reader.TryRead(out var snapshot))
                    {
                        latest = snapshot; // only the freshest matters for state/player
                    }

                    if (latest is not null)
                    {
                        _lastSnapshot = latest; // the LLM loop comments on the freshest state
                        var now = DateTimeOffset.UtcNow;
                        _hub.PublishState(latest, now);
                        _hub.PublishPlayer(latest, now);
                        _hub.PublishMap(latest, now);
                        UpdateMapFit(latest);
                        // No Layer-1 commentary once the final classification is in — the
                        // cool-down lap and results screen must not keep the commentator
                        // talking about a race that is already over.
                        if (latest.FinalResults.Count == 0)
                        {
                            foreach (var line in _commentary.Plan(latest))
                            {
                                _hub.PublishCommentary(line);
                                AddRecentLine(line);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Publish can only fail on unexpected bugs; keep the overlay alive and
                    // observably wrong (stale) rather than dead. Next tick retries.
                    await Task.Delay(200, ct);
                }

                if (_services.OverlaySnapshots.Reader.Completion.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Feeds the player's world position into the session-scoped map fitter and
    /// publishes the tracklayout message the moment the fit locks. Unknown tracks leave
    /// the fitter unset — the pages keep their self-learning trail. The fitter is frozen
    /// after lock, so this degrades to a cheap IsLocked check for the rest of the session.</summary>
    private void UpdateMapFit(TelemetrySnapshot latest)
    {
        var meta = latest.Meta;
        var positions = latest.Positions;
        if (meta is null || positions is null)
        {
            return;
        }

        if (meta.SessionUid != _mapFitterSessionUid)
        {
            _mapFitterSessionUid = meta.SessionUid;
            _mapStartFinishSent = false;
            _mapLayout = TrackLayoutCatalog.TryGet(meta.Track.ToString(), out var layout) ? layout : null;
            _mapFitter = _mapLayout is null ? null : new TrackFitter(_mapLayout);
        }

        if (_mapFitter is null)
        {
            return;
        }

        var car = meta.PlayerCarIndex;
        if (car >= Math.Min((int)positions.Count, TelemetryConstants.MaxCars))
        {
            return;
        }

        var (x, z) = (positions.X[car], positions.Z[car]);
        if (x == 0 && z == 0)
        {
            return; // unreported slot — as everywhere else in the pipeline
        }

        var wasLocked = _mapFitter.IsLocked;
        _mapFitter.Feed(x, z);
        if ((!wasLocked && _mapFitter.IsLocked) ||
            (!_mapStartFinishSent && _mapFitter.StartFinishWorld is not null))
        {
            _mapStartFinishSent = _mapFitter.StartFinishWorld is not null;
            _hub.PublishLayout();
        }
    }

    /// <summary>Player lap completions are start/finish crossings — three of them pin the
    /// S/F marker position on the layout. Non-player laps and unmapped tracks are ignored.</summary>
    private void MarkStartFinishCrossing(StoreEvent storeEvent)
    {
        if (storeEvent is not LapCompleted { CarIndex: var car } ||
            _mapFitter is null ||
            _lastSnapshot?.Meta is not { } meta ||
            meta.PlayerCarIndex != car ||
            _lastSnapshot.Positions is not { } positions ||
            car >= Math.Min((int)positions.Count, TelemetryConstants.MaxCars))
        {
            return;
        }

        var (x, z) = (positions.X[car], positions.Z[car]);
        if (x != 0 || z != 0)
        {
            _mapFitter.MarkStartFinishCrossing(x, z);
        }
    }

    /// <summary>Resolves the current fitted layout for the hub's LayoutProvider — used for
    /// the connect-time push and the lock-time fan-out. Null while nothing is locked.</summary>
    private OverlayTrackLayout? BuildLayout()
    {
        if (_mapFitter?.Fit is not { } fit || _mapLayout is null || _lastSnapshot?.Meta is not { } meta)
        {
            return null;
        }

        return OverlayMessageFactory.BuildTrackLayout(
            meta.Track.ToString(), meta.SessionUid, _mapLayout, fit, _mapFitter.StartFinishWorld);
    }

    /// <summary>Throttled Layer-2 commentary: every 45 s the LLM gets the freshest snapshot
    /// plus the recent Layer-1 lines and publishes its answer as a commentary line. A
    /// missing key/model, a failed call or a busy gate all skip the tick silently — Layer 1
    /// keeps the overlays talking either way.</summary>
    private async Task LlmCommentaryLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
                if (_llm is null || !_llm.IsConfigured || _lastSnapshot is not { } snapshot ||
                    snapshot.FinalResults.Count > 0) // no commentary after the race is over
                {
                    continue;
                }

                // One LLM call at a time — a slow response must not stack up.
                if (!await _llmGate.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    var line = await _llm.CommentateAsync(
                        BuildCommentaryContext(snapshot), ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _hub.PublishCommentary(line);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown — let the outer catch end the loop.
                    throw;
                }
                catch (Exception ex)
                {
                    // LLM timeout surfaces as TaskCanceledException with the token NOT
                    // cancelled — that must skip the tick, not kill the 45-s loop. The
                    // provider's null-on-failure contract makes this mostly unreachable,
                    // but a surprise must not take Layer-1 commentary down with it.
                    App.Log($"LLM commentary failed: {ex.Message}");
                }
                finally
                {
                    _llmGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Builds the LLM commentary context from a snapshot: session facts plus the
    /// recent Layer-1 lines (capped ring buffer) so the model knows what was just said.</summary>
    private CommentaryContext BuildCommentaryContext(TelemetrySnapshot snapshot)
    {
        var meta = snapshot.Meta;
        var player = snapshot.Standings.FirstOrDefault(s => s.IsPlayer);
        IReadOnlyList<string> recentLines;
        lock (_recentLinesLock)
        {
            recentLines = _recentLines.ToArray();
        }

        return new CommentaryContext(
            Track: meta?.Track.ToString() ?? "Unbekannt",
            SessionType: meta?.SessionType.ToString() ?? "Session",
            Lap: player?.CurrentLapNum ?? 0,
            TotalLaps: meta?.TotalLaps ?? 0,
            Weather: meta?.Weather.ToString() ?? "Unbekannt",
            PlayerPosition: player?.Position ?? 0,
            RecentLines: recentLines);
    }

    /// <summary>Appends a Layer-1 commentary line to the LLM context ring buffer (capped
    /// at 20 — the model only needs the recent conversation, not the whole race).</summary>
    private void AddRecentLine(string line)
    {
        lock (_recentLinesLock)
        {
            _recentLines.Add(line);
            if (_recentLines.Count > 20)
            {
                _recentLines.RemoveAt(0);
            }
        }
    }

    /// <summary>Sync, re-entrant shutdown. Cancels first, then runs the async teardown on
    /// the thread pool so no UI SynchronizationContext is captured (a direct
    /// GetAwaiter().GetResult() over awaited calls would deadlock the WPF dispatcher).</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return; // already disposed
        }

        _stop.Cancel();
        try
        {
            Task.Run(() => StopClientsAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Dispose must not throw; log and continue.
            App.Log($"Overlay host shutdown error: {ex.Message}");
        }

        _stop.Dispose();
    }
}