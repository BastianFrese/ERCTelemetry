using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using ERCTelemetry.Core.OverlayProtocol;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.App.OverlayServer;

/// <summary>Registry of connected overlay browser clients. Messages are serialized once and
/// fanned out into per-client bounded DropOldest channels; one writer task per socket sends
/// them. A slow or failing client only loses messages — it can never block the pipeline.
/// Throttle state is lock-synchronized and safe to call from any thread (connection and
/// pump threads included).</summary>
public sealed class OverlayWebSocketHub
{
    private const int OutboundCapacity = 64;

    /// <summary>Client frames are tiny ({@code select}/{@code ping}); anything bigger is a bug
    /// or abuse — the loopback binding lowers but does not remove the DoS angle.</summary>
    private const int MaxInboundMessageBytes = 8 * 1024;

    private sealed class Client
    {
        public required Channel<string> Outbound;
        public byte? SelectedCarIndex; // per-client select override (player-slot frame choice)
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly OverlayThrottle _throttle = new();

    /// <summary>Resolves the active color scheme wire value (null = classic) at send time.
    /// Wired by OverlayWebHost from the settings service so pages can switch schemes live.</summary>
    public Func<string?>? ColorSchemeProvider { get; set; }

    /// <summary>Resolves the block-visibility map for the <c>config</c> message (null =
    /// nothing to push). Wired by OverlayWebHost from the settings service; pushed on
    /// connect and on every settings save.</summary>
    public Func<IReadOnlyDictionary<string, bool>?>? ConfigProvider { get; set; }

    /// <summary>Resolves the fitted circuit layout for the <c>tracklayout</c> message
    /// (null = no layout locked / unknown track). Wired by the OverlayWebHost pump; sent
    /// on connect and pushed once when the fit locks mid-session.</summary>
    public Func<OverlayTrackLayout?>? LayoutProvider { get; set; }

    public int ClientCount => _clients.Count;

    /// <summary>Accepts a websocket on /ws and serves it until the client disconnects.
    /// Called from the ASP.NET pipeline; one call per connection.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var client = new Client
        {
            Outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboundCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // slow client ⇒ stale data, not memory
            }),
        };
        _clients.TryAdd(id, client);

        // New client: hello immediately, and a full state sync as soon as the pump ticks.
        client.Outbound.Writer.TryWrite(OverlayMessageFactory.CreateHello(ColorSchemeProvider?.Invoke()));
        // Block visibility rides its own per-connect write — ForceState is hub-global and
        // would push a full state to every client just to configure this one.
        if (BuildConfigPayload() is { } config)
        {
            client.Outbound.Writer.TryWrite(config);
        }
        // Same pattern for the fitted circuit outline: a browser source connected
        // mid-session gets the full track immediately instead of re-learning it.
        if (BuildLayoutPayload() is { } layout)
        {
            client.Outbound.Writer.TryWrite(layout);
        }

        _throttle.ForceState();

        var writer = WriteLoopAsync(socket, client.Outbound, context.RequestAborted);
        try
        {
            await ReadLoopAsync(socket, client, context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Abrupt disconnect or app shutdown — the finally below cleans up.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Outbound.Writer.TryComplete();
            await writer; // let the writer flush or abort
            await CloseQuietlyAsync(socket);
        }
    }

    /// <summary>Combined state message at most every 200 ms and only when its
    /// session/standings/points content changed (serialized once, written to every client).</summary>
    public void PublishState(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        if (_clients.IsEmpty || !_throttle.ShouldSendState(snapshot, now))
        {
            return;
        }

        var payload = OverlayMessageFactory.CreateState(snapshot, ColorSchemeProvider?.Invoke());
        foreach (var client in _clients.Values)
        {
            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>Player/rival drive frames at ~30 Hz. Clients that sent no select share one
    /// prebuilt payload; per-client overrides are serialized per distinct selected car.</summary>
    public void PublishPlayer(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        if (_clients.IsEmpty || !_throttle.ShouldSendPlayer(now))
        {
            return;
        }

        var defaultPayload = OverlayMessageFactory.CreatePlayer(snapshot);
        var overridePayloads = new Dictionary<byte, string>();

        foreach (var client in _clients.Values)
        {
            if (client.SelectedCarIndex is not { } selected)
            {
                client.Outbound.Writer.TryWrite(defaultPayload);
                continue;
            }

            if (!overridePayloads.TryGetValue(selected, out var payload))
            {
                payload = OverlayMessageFactory.CreatePlayer(snapshot, selected);
                overridePayloads.Add(selected, payload);
            }

            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>~10 Hz minimap positions (skip when Positions is null — nothing to send
    /// until the first Motion packet). Serialized once, fanned out to every client.</summary>
    public void PublishMap(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        if (_clients.IsEmpty || !_throttle.ShouldSendMap(now))
        {
            return;
        }

        // Serialized once even though page-side scaling differs; null = no Motion data yet.
        var payload = OverlayMessageFactory.CreateMap(snapshot);
        if (payload is null)
        {
            return;
        }

        foreach (var client in _clients.Values)
        {
            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>Race-control event → immediate event message to every client.</summary>
    public void PublishEvent(StoreEvent storeEvent)
    {
        var payload = OverlayMessageFactory.CreateEvent(storeEvent);
        foreach (var client in _clients.Values)
        {
            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>AI-commentator line → immediate commentary message to every client.</summary>
    public void PublishCommentary(string text)
    {
        var payload = OverlayMessageFactory.CreateCommentary(text);
        foreach (var client in _clients.Values)
        {
            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>Block-visibility config → immediate config message to every client
    /// (settings save). Serialized once; skipped when no provider is wired.</summary>
    public void PublishConfig()
    {
        if (_clients.IsEmpty || BuildConfigPayload() is not { } payload)
        {
            return;
        }

        foreach (var client in _clients.Values)
        {
            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>Fitted circuit layout → immediate tracklayout message to every client
    /// (the moment the fit locks). No-op when no layout is locked or no client is up.</summary>
    public void PublishLayout()
    {
        if (_clients.IsEmpty || BuildLayoutPayload() is not { } payload)
        {
            return;
        }

        foreach (var client in _clients.Values)
        {
            client.Outbound.Writer.TryWrite(payload);
        }
    }

    /// <summary>Makes the next state message go out immediately regardless of the 5 Hz
    /// timer and the deep-compare (settings save — a static session would otherwise keep
    /// the old scheme/config until real content changes). Safe from any thread.</summary>
    public void ForceState() => _throttle.ForceState();

    /// <summary>Resolves the provider once and serializes the config payload once;
    /// null when no provider is wired or it returned nothing to push.</summary>
    private string? BuildConfigPayload()
    {
        if (ConfigProvider?.Invoke() is not { } blocks)
        {
            return null;
        }

        return OverlayMessageFactory.CreateConfig(blocks);
    }

    /// <summary>Resolved fitted layout, serialized; null when nothing is wired or locked.</summary>
    private string? BuildLayoutPayload()
    {
        if (LayoutProvider?.Invoke() is not { } layout)
        {
            return null;
        }

        return OverlayMessageFactory.CreateTrackLayout(layout);
    }

    private async Task ReadLoopAsync(WebSocket socket, Client client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(socket, buffer, ct);
            if (message is null)
            {
                return; // closed
            }

            if (OverlayClientParser.Parse(message) is not { } parsed)
            {
                continue;
            }

            if (parsed.Type == OverlayProtocolConstants.SelectType)
            {
                client.SelectedCarIndex = parsed.CarIndex; // null clears the override
            }
            // "ping" and unknown types need no action
        }
    }

    private static async Task<string?> ReceiveFullMessageAsync(
        WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var temp = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.CloseStatus.HasValue)
            {
                return null;
            }

            if (temp.Length + result.Count > MaxInboundMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                    "overlay client frame too large", CancellationToken.None);
                return null;
            }

            temp.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(temp.ToArray());
    }

    private static async Task WriteLoopAsync(WebSocket socket, Channel<string> outbound, CancellationToken ct)
    {
        try
        {
            await foreach (var message in outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text,
                    endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
            or ChannelClosedException)
        {
            // Client vanished or app shutting down — dropping it is the contract.
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Already gone, or the close handshake hung — drop the socket either way.
        }

        socket.Dispose();
    }
}