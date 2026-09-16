using ERCTelemetry.Core.TwitchChat;
using Xunit;

namespace ERCTelemetry.Core.Tests.TwitchChat;

public sealed class IrcMessageParserTests
{
    [Fact]
    public void Parses_ping_with_payload()
    {
        var msg = IrcMessageParser.Parse("PING :tmi.twitch.tv");

        var ping = Assert.IsType<IrcPing>(msg);
        Assert.Equal("tmi.twitch.tv", ping.Payload);
    }

    [Fact]
    public void Parses_privmsg_with_user_channel_and_text()
    {
        var msg = IrcMessageParser.Parse(
            ":viewer1!viewer1@viewer1.tmi.twitch.tv PRIVMSG #erdi :!gap hauser");

        var priv = Assert.IsType<IrcPrivMsg>(msg);
        Assert.Equal("viewer1", priv.User);
        Assert.Equal("#erdi", priv.Channel);
        Assert.Equal("!gap hauser", priv.Text);
    }

    [Fact]
    public void Parses_privmsg_without_leading_colon_in_text()
    {
        var msg = IrcMessageParser.Parse(":user!user@user.tmi.twitch.tv PRIVMSG #c :hi");

        var priv = Assert.IsType<IrcPrivMsg>(msg);
        Assert.Equal("hi", priv.Text);
    }

    [Fact]
    public void Welcome_and_names_lines_are_numerics_not_unknown()
    {
        var welcome = Assert.IsType<IrcNumeric>(
            IrcMessageParser.Parse(":tmi.twitch.tv 001 erdi :Welcome, GLHF!"));
        Assert.Equal(1, welcome.Code);
        Assert.Equal("Welcome, GLHF!", welcome.Message);

        // The names reply has a "=" target before the colon; only the text after the
        // last " :" is captured — the user list is what the numeric message reports.
        var names = Assert.IsType<IrcNumeric>(
            IrcMessageParser.Parse(":tmi.twitch.tv 353 erdi = #erdi :user1 user2"));
        Assert.Equal(353, names.Code);
        Assert.Equal("user1 user2", names.Message);

        // JOIN is still not modeled.
        Assert.IsType<IrcUnknown>(IrcMessageParser.Parse(":erdi!erdi@erdi.tmi.twitch.tv JOIN #erdi"));
    }

    [Theory]
    // Twitch's login-refused numerics — both prefixed and prefix-less forms must parse.
    [InlineData(":tmi.twitch.tv 463 erdi :Login authentication failed", 463, "Login authentication failed")]
    [InlineData(":tmi.twitch.tv 464 erdi :Login authentication failed", 464, "Login authentication failed")]
    [InlineData(":tmi.twitch.tv 465 erdi :Login authentication failed", 465, "Login authentication failed")]
    [InlineData(":tmi.twitch.tv 466 erdi :Login authentication failed", 466, "Login authentication failed")]
    [InlineData("001 erdi :Welcome, GLHF!", 1, "Welcome, GLHF!")]
    public void Parses_login_refusal_numerics_with_code_and_message(string line, int code, string message)
    {
        var numeric = Assert.IsType<IrcNumeric>(IrcMessageParser.Parse(line));

        Assert.Equal(code, numeric.Code);
        Assert.Equal(message, numeric.Message);
    }

    [Fact]
    public void Parses_notice_with_login_target_and_channel_target()
    {
        // Some refusals arrive as a NOTICE to "*" instead of a numeric.
        var login = Assert.IsType<IrcNotice>(
            IrcMessageParser.Parse(":tmi.twitch.tv NOTICE * :Login authentication failed"));
        Assert.Equal("*", login.Target);
        Assert.Equal("Login authentication failed", login.Text);

        // Ordinary channel notices parse the same way.
        var channel = Assert.IsType<IrcNotice>(
            IrcMessageParser.Parse(":tmi.twitch.tv NOTICE #erdi :This room is now in slow mode"));
        Assert.Equal("#erdi", channel.Target);
        Assert.Equal("This room is now in slow mode", channel.Text);
    }

    [Fact]
    public void Malformed_lines_are_unknown_not_throws()
    {
        Assert.IsType<IrcUnknown>(IrcMessageParser.Parse(""));
        Assert.IsType<IrcUnknown>(IrcMessageParser.Parse(null));
        Assert.IsType<IrcUnknown>(IrcMessageParser.Parse(":no space here"));
        Assert.IsType<IrcUnknown>(IrcMessageParser.Parse("PRIVMSG without prefix"));
    }

    [Fact]
    public void Non_ascii_digit_numeric_command_is_unknown_not_throws()
    {
        // Full-width digits (Unicode Nd) pass char.IsDigit but int.Parse rejects them.
        // The parser must keep its never-throws contract, not kill the read loop.
        Assert.IsType<IrcUnknown>(IrcMessageParser.Parse(":tmi.twitch.tv ４６４ erdi :Login authentication failed"));
    }

    [Fact]
    public void Strips_trailing_crlf()
    {
        var msg = IrcMessageParser.Parse("PING :tmi.twitch.tv\r\n");

        var ping = Assert.IsType<IrcPing>(msg);
        Assert.Equal("tmi.twitch.tv", ping.Payload);
    }
}
