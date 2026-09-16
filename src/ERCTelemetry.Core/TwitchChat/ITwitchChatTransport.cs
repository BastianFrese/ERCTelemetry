namespace ERCTelemetry.Core.TwitchChat;

/// <summary>Transport boundary for the chat bot — the real <see cref="TwitchIrcClient"/>
/// talks IRC over WebSocket; tests use a fake. The service only depends on this.</summary>
public interface ITwitchChatTransport
{
    /// <summary>Raised for every chat message: (channel, user, text).</summary>
    event Action<string, string, string>? ChatMessage;

    /// <summary>Connects, authenticates and joins the channel. Throws on failure so the
    /// caller can surface a status; the caller decides whether to retry.</summary>
    Task ConnectAsync(string channel, string oauthToken, CancellationToken ct);

    /// <summary>Sends one chat message. Must not throw on a dead connection — the caller
    /// treats a failed send as "chat is down", not as a crash.</summary>
    void SendMessage(string channel, string text);

    /// <summary>Disconnects and releases the socket.</summary>
    Task DisconnectAsync();
}
