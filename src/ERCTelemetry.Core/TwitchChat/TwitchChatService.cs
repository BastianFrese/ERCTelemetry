using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.TwitchChat;

/// <summary>Orchestrates the chat bot: listens on the transport, parses commands, applies
/// cooldowns (per user + global, so a spammer cannot flood the chat), builds the answer
/// from the latest snapshot and sends it back. Thread-safe — the transport raises
/// <see cref="ITwitchChatTransport.ChatMessage"/> from its read loop.</summary>
public sealed class TwitchChatService
{
    private readonly ITwitchChatTransport _transport;
    private readonly Func<TelemetrySnapshot?> _latestSnapshot;
    private readonly TimeSpan _userCooldown;
    private readonly TimeSpan _globalCooldown;
    private readonly Dictionary<string, DateTimeOffset> _lastReplyByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private DateTimeOffset _lastReplyAt = DateTimeOffset.MinValue;

    public TwitchChatService(
        ITwitchChatTransport transport,
        Func<TelemetrySnapshot?> latestSnapshot,
        TimeSpan? userCooldown = null,
        TimeSpan? globalCooldown = null)
    {
        _transport = transport;
        _latestSnapshot = latestSnapshot;
        _userCooldown = userCooldown ?? TimeSpan.FromSeconds(10);
        _globalCooldown = globalCooldown ?? TimeSpan.FromSeconds(2);
        _transport.ChatMessage += OnChatMessage;
    }

    /// <summary>Handles one chat message. Non-commands are ignored; commands are answered
    /// unless a cooldown is active. Never throws — a broken transport send is swallowed
    /// (the bot stays alive; the next message retries).</summary>
    private void OnChatMessage(string channel, string user, string text)
    {
        var parsed = ChatCommandParser.Parse(text);
        if (parsed is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (now - _lastReplyAt < _globalCooldown)
            {
                return;
            }

            if (_lastReplyByUser.TryGetValue(user, out var last) && now - last < _userCooldown)
            {
                return;
            }

            _lastReplyAt = now;
            _lastReplyByUser[user] = now;
            if (_lastReplyByUser.Count > MaxTrackedUsers)
            {
                _lastReplyByUser.Clear(); // bounded memory — cooldowns reset, that is fine
            }
        }

        try
        {
            var answer = ChatAnswerBuilder.Build(parsed, _latestSnapshot());
            _transport.SendMessage(channel, answer);
        }
        catch
        {
            // Chat is down or the transport failed — never crash the read loop.
        }
    }

    /// <summary>Upper bound on remembered per-user cooldowns (bounded memory).</summary>
    private const int MaxTrackedUsers = 200;
}
