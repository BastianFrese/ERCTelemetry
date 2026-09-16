using ERCTelemetry.Core.TwitchChat;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class ChatCommandParserTests
{
    [Theory]
    [InlineData("!gap", ChatCommand.Gap)]
    [InlineData("!GAP", ChatCommand.Gap)]
    [InlineData("!pace", ChatCommand.Pace)]
    [InlineData("!reifen", ChatCommand.Tyres)]
    [InlineData("!tyres", ChatCommand.Tyres)]
    public void Recognizes_commands_case_insensitive(string message, ChatCommand expected)
    {
        var parsed = ChatCommandParser.Parse(message);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Command);
        Assert.Empty(parsed.NameTokens);
    }

    [Fact]
    public void Captures_name_tokens_after_the_command()
    {
        var parsed = ChatCommandParser.Parse("!gap hauser erdi");

        Assert.NotNull(parsed);
        Assert.Equal(ChatCommand.Gap, parsed!.Command);
        Assert.Equal(["hauser", "erdi"], parsed.NameTokens);
    }

    [Fact]
    public void Ignores_extra_whitespace_between_tokens()
    {
        var parsed = ChatCommandParser.Parse("  !reifen   hauser   ");

        Assert.NotNull(parsed);
        Assert.Equal(ChatCommand.Tyres, parsed!.Command);
        Assert.Equal(["hauser"], parsed.NameTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hallo zusammen")]
    [InlineData("!unknown")]
    [InlineData("!")]
    public void Returns_null_for_non_commands(string? message)
    {
        Assert.Null(ChatCommandParser.Parse(message));
    }
}
