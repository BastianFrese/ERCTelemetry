using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.TwitchChat;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class TwitchChatServiceTests
{
    private sealed class FakeTransport : ITwitchChatTransport
    {
        public event Action<string, string, string>? ChatMessage;
        public List<string> Sent { get; } = [];
        public int ConnectCalls { get; private set; }

        public void Raise(string channel, string user, string text) => ChatMessage?.Invoke(channel, user, text);

        public Task ConnectAsync(string channel, string oauthToken, CancellationToken ct)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public void SendMessage(string channel, string text) => Sent.Add(text);

        public Task DisconnectAsync() => Task.CompletedTask;
    }

    private static TelemetrySnapshot Snapshot() => new(
        Meta: null,
        Drivers: Array.Empty<DriverEntry>(),
        Standings: new[]
        {
            new StandingsRow(1, 3, "Erdi", Team.RedBullRacing, 1, 12, 91089, 90123, 0, 0,
                PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 12, true),
        },
        FinalResults: Array.Empty<FinalResultRow>(),
        RecentEvents: Array.Empty<RaceEventEntry>(),
        Player: null,
        Rival: null,
        FrameVersion: 0,
        BuiltAtUtc: new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Answers_a_command_from_the_latest_snapshot()
    {
        var transport = new FakeTransport();
        var service = new TwitchChatService(transport, Snapshot, TimeSpan.Zero, TimeSpan.Zero);

        transport.Raise("#erdi", "viewer1", "!gap");

        var sent = Assert.Single(transport.Sent);
        Assert.Equal("Erdi: P1 · FÜHRT", sent);
    }

    [Fact]
    public void Ignores_non_commands()
    {
        var transport = new FakeTransport();
        var service = new TwitchChatService(transport, Snapshot, TimeSpan.Zero, TimeSpan.Zero);

        transport.Raise("#erdi", "viewer1", "hallo zusammen");

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Global_cooldown_suppresses_rapid_replies()
    {
        var transport = new FakeTransport();
        var service = new TwitchChatService(transport, Snapshot, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        transport.Raise("#erdi", "viewer1", "!gap");
        transport.Raise("#erdi", "viewer2", "!pace");

        Assert.Single(transport.Sent);
    }

    [Fact]
    public void Per_user_cooldown_suppresses_spam_from_one_user()
    {
        var transport = new FakeTransport();
        var service = new TwitchChatService(transport, Snapshot, TimeSpan.FromSeconds(30), TimeSpan.Zero);

        transport.Raise("#erdi", "spammer", "!gap");
        transport.Raise("#erdi", "spammer", "!pace");

        Assert.Single(transport.Sent);
    }

    [Fact]
    public void Different_users_bypass_the_per_user_cooldown()
    {
        var transport = new FakeTransport();
        var service = new TwitchChatService(transport, Snapshot, TimeSpan.FromSeconds(30), TimeSpan.Zero);

        transport.Raise("#erdi", "viewer1", "!gap");
        transport.Raise("#erdi", "viewer2", "!pace");

        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public void Broken_transport_send_does_not_crash_the_service()
    {
        var transport = new FakeTransport();

        // A throwing snapshot provider simulates a broken pipeline — the service must
        // swallow it and stay alive for the next message.
        var throwing = new TwitchChatService(transport, () => throw new InvalidOperationException("boom"),
            TimeSpan.Zero, TimeSpan.Zero);
        transport.Raise("#erdi", "viewer1", "!gap");

        Assert.Empty(transport.Sent);
        transport.Raise("#erdi", "viewer2", "!gap");
        Assert.Empty(transport.Sent);
    }
}
