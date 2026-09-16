using System.Net.WebSockets;
using System.Text;

namespace ERCTelemetry.Core.TwitchChat;

/// <summary>Real chat transport: Twitch IRC over WebSocket. Connects with an OAuth token
/// (PASS oauth:…), joins the channel, answers PING keep-alives and raises
/// <see cref="ChatMessage"/> for every PRIVMSG. Sends are serialized through one lock —
/// ClientWebSocket.SendAsync is not safe for concurrent callers (PONG + replies).</summary>
public sealed class TwitchIrcClient : ITwitchChatTransport, IDisposable
{
    private const string IrcEndpoint = "wss://irc-ws.chat.twitch.tv:443";

    private readonly string _username;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _disposed;

    public TwitchIrcClient(string username)
    {
        _username = username;
    }

    public event Action<string, string, string>? ChatMessage;

    /// <summary>Raised when the server refuses the login (IRC numerics 463/464/465/466 —
    /// "Login authentication failed", "Incorrect Password", or a login rate-limit). The
    /// token is stale; the host surfaces a fresh-login prompt and reconnects.</summary>
    public event Action? AuthenticationFailed;

    /// <summary>Connects, authenticates and joins the channel. Throws on failure (bad
    /// token, no network) so the caller can surface a status and decide on a retry.</summary>
    public async Task ConnectAsync(string channel, string oauthToken, CancellationToken ct)
    {
        var target = channel.StartsWith('#') ? channel : "#" + channel;
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(IrcEndpoint), ct).ConfigureAwait(false);

        _socket = socket;
        await SendRawAsync($"PASS oauth:{oauthToken}", ct).ConfigureAwait(false);
        await SendRawAsync($"NICK {_username}", ct).ConfigureAwait(false);
        await SendRawAsync($"JOIN {target}", ct).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Sends one chat message. Fire-and-forget with errors swallowed — a dead
    /// connection must not crash the caller; the next message simply does nothing.</summary>
    public void SendMessage(string channel, string text)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendRawAsync($"PRIVMSG {channel} :{text}", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Chat is down — nothing to do; the bot stays alive.
            }
        });
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_readTask is not null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch
            {
                // Read loop was mid-receive when cancelled.
            }
        }

        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Socket already gone.
            }

            socket.Dispose();
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Dispose must not throw.
        }

        _sendLock.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var lineBuffer = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var socket = _socket;
            if (socket is null)
            {
                break;
            }

            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                break; // connection dropped — the caller reconnects
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            lineBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            while (true)
            {
                var nl = lineBuffer.ToString().IndexOf('\n');
                if (nl < 0)
                {
                    break;
                }

                var line = lineBuffer.ToString(0, nl);
                lineBuffer.Remove(0, nl + 1);
                HandleLine(line);
            }
        }
    }

    private void HandleLine(string line)
    {
        switch (IrcMessageParser.Parse(line))
        {
            case IrcPing ping:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendRawAsync($"PONG :{ping.Payload}", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Keep-alive failed — the server will drop us; reconnect handles it.
                    }
                });
                break;

            case IrcPrivMsg priv:
                ChatMessage?.Invoke(priv.Channel, priv.User, priv.Text);
                break;

            case IrcNumeric numeric when numeric.Code is 463 or 464 or 465 or 466:
                AuthenticationFailed?.Invoke();
                break;

            // Some refusals arrive as a NOTICE to "*" instead of a numeric — the "Incorrect
            // Password" phrasing in particular. Treat those like the numerics above, so a
            // stale token always surfaces the fresh-login prompt. Only the login-form
            // target counts: a "#channel" notice with the same words would be chat text.
            case IrcNotice { Target: "*" or "" } notice
                when notice.Text.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase)
                  || notice.Text.Contains("Incorrect Password", StringComparison.OrdinalIgnoreCase):
                AuthenticationFailed?.Invoke();
                break;
        }
    }

    private async Task SendRawAsync(string line, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
