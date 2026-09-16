using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.TwitchChat;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class ChatAnswerBuilderTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static TelemetrySnapshot Snapshot() => new(
        Meta: null,
        Drivers: Array.Empty<DriverEntry>(),
        Standings: new[]
        {
            new StandingsRow(1, 3, "Erdi", Team.RedBullRacing, 1, 12, 91089, 90123, 0, 0,
                PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 12, true),
            new StandingsRow(2, 9, "Manuel Hauser2", Team.Mercedes, 44, 12, 93450, 89876, 2361, 2361,
                PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1Inter, 4, false),
        },
        FinalResults: Array.Empty<FinalResultRow>(),
        RecentEvents: Array.Empty<RaceEventEntry>(),
        Player: null,
        Rival: null,
        FrameVersion: 0,
        BuiltAtUtc: FixedUtc);

    [Fact]
    public void Gap_without_name_uses_the_player_row()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Gap, []), Snapshot());

        Assert.Equal("Erdi: P1 · FÜHRT", answer);
    }

    [Fact]
    public void Gap_with_partial_name_matches_fuzzy()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Gap, ["hauser"]), Snapshot());

        Assert.Equal("Manuel Hauser2: P2 · +2.361s zum Leader · +2.361s auf P1", answer);
    }

    [Fact]
    public void Pace_formats_last_and_best_lap()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Pace, ["erdi"]), Snapshot());

        Assert.Equal("Erdi: Letzte Runde 1:31.089 · Beste 1:30.123", answer);
    }

    [Fact]
    public void Tyres_show_compound_and_age()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Tyres, ["hauser"]), Snapshot());

        Assert.Equal("Manuel Hauser2: INT · 4 Runden alt", answer);
    }

    [Fact]
    public void Multiple_tokens_answer_each_driver()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Gap, ["hauser", "erdi"]), Snapshot());

        Assert.Equal(
            "Manuel Hauser2: P2 · +2.361s zum Leader · +2.361s auf P1 · Erdi: P1 · FÜHRT",
            answer);
    }

    [Fact]
    public void Unknown_driver_returns_helpful_message()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Gap, ["leclerc"]), Snapshot());

        Assert.Equal("Fahrer 'leclerc' nicht gefunden.", answer);
    }

    [Fact]
    public void Null_snapshot_returns_helpful_message()
    {
        var answer = ChatAnswerBuilder.Build(new ParsedChatCommand(ChatCommand.Gap, []), null);

        Assert.Contains("Noch keine Telemetrie-Daten", answer);
    }
}
