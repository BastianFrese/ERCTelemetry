using System.Globalization;

namespace ERCTelemetry.Core.TwitchChat;

/// <summary>One parsed IRC line. Only the message types the chat bot cares about are
/// modeled; everything else (names list, joins, parts) becomes <see cref="IrcUnknown"/>.</summary>
public abstract record IrcMessage;

/// <summary>Server keep-alive — the client must answer with <c>PONG :&lt;payload&gt;</c>.</summary>
public sealed record IrcPing(string Payload) : IrcMessage;

/// <summary>A chat message: <c>:user!user@user.tmi.twitch.tv PRIVMSG #channel :text</c>.</summary>
public sealed record IrcPrivMsg(string User, string Channel, string Text) : IrcMessage;

/// <summary>A 3-digit numeric reply (RFC 1459), e.g. Twitch's <c>001</c> (welcome) or the
/// login-refused codes <c>463</c>/<c>464</c>/<c>465</c>/<c>466</c>. <see cref="Code"/> is the
/// numeric command, <see cref="Message"/> the trailing text after the last ':'.</summary>
public sealed record IrcNumeric(int Code, string Message) : IrcMessage;

/// <summary>A NOTICE message: server or channel advisory text. <see cref="Target"/> is the
/// recipient — <c>*</c> for the login-form notice (<c>NOTICE * :…</c>), <c>#channel</c> for
/// channel notices. Twitch sends some login refusals as a NOTICE ("Login authentication
/// failed", "Incorrect Password") rather than a numeric, which is why the client inspects it.</summary>
public sealed record IrcNotice(string Target, string Text) : IrcMessage;

/// <summary>Anything the parser does not model (JOIN, PART, …).</summary>
public sealed record IrcUnknown(string Raw) : IrcMessage;

/// <summary>Parses raw IRC lines into <see cref="IrcMessage"/>s. Pure string logic so the
/// Twitch client's protocol handling is fully unit-testable without a socket.</summary>
public static class IrcMessageParser
{
    /// <summary>Parses one IRC line (without the trailing CRLF). Never throws — malformed
    /// lines become <see cref="IrcUnknown"/>.</summary>
    public static IrcMessage Parse(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return new IrcUnknown(string.Empty);
        }

        var trimmed = line.TrimEnd('\r', '\n');
        if (trimmed.StartsWith("PING ", StringComparison.Ordinal))
        {
            return new IrcPing(trimmed[5..].TrimStart(':'));
        }

        // <message> ::= [':' <prefix> <SP>] <command> <params>
        var source = string.Empty;
        var rest = trimmed;
        if (trimmed.StartsWith(':'))
        {
            var space = trimmed.IndexOf(' ');
            if (space <= 0)
            {
                return new IrcUnknown(trimmed);
            }

            source = trimmed[1..space];
            rest = trimmed[(space + 1)..];
        }

        var commandEnd = rest.IndexOf(' ');
        var command = commandEnd < 0 ? rest : rest[..commandEnd];
        var args = commandEnd < 0 ? "" : rest[(commandEnd + 1)..];

        if (command == "PRIVMSG" && source.Length > 0)
        {
            var (channel, text) = SplitChannelAndText(args);
            if (channel.Length > 0)
            {
                return new IrcPrivMsg(ExtractUser(source), channel, text);
            }
        }

        // Numerics: ":tmi.twitch.tv 463 erdi :Login authentication failed" (prefixed) or
        // the prefix-less "001 erdi :Welcome, GLHF!". The 3-digit code is what matters —
        // the client maps 463/464/465/466 (login refused) to an auth failure. ASCII-only
        // range check: char.IsDigit would accept Unicode digits (e.g. full-width ０) that
        // int.Parse rejects, breaking this method's never-throws contract.
        if (command.Length == 3 && command.All(c => c is >= '0' and <= '9'))
        {
            return new IrcNumeric(int.Parse(command, CultureInfo.InvariantCulture), ExtractNumericMessage(args));
        }

        // NOTICE: "NOTICE * :Login authentication failed" (login) or "NOTICE #chan :…".
        if (command == "NOTICE")
        {
            var (target, text) = SplitChannelAndText(args);
            return new IrcNotice(target, text);
        }

        return new IrcUnknown(trimmed);
    }

    /// <summary>"#channel :text" (or "* :text" for a login NOTICE) → ("#channel", "text").</summary>
    private static (string Channel, string Text) SplitChannelAndText(string args)
    {
        var colon = args.IndexOf(" :", StringComparison.Ordinal);
        if (colon < 0)
        {
            return (args.Trim(), string.Empty);
        }

        return (args[..colon].Trim(), args[(colon + 2)..]);
    }

    /// <summary>"user!user@user.tmi.twitch.tv" → "user".</summary>
    private static string ExtractUser(string prefix)
    {
        var bang = prefix.IndexOf('!');
        return bang > 0 ? prefix[..bang] : prefix;
    }

    /// <summary>"&lt;user&gt; :&lt;message&gt;" (or "&lt;user&gt; = #chan :&lt;message&gt;" for the
    /// names reply) → the text after the last " :"— the human-readable reason.</summary>
    private static string ExtractNumericMessage(string args)
    {
        var colon = args.IndexOf(" :", StringComparison.Ordinal);
        return colon >= 0 ? args[(colon + 2)..] : args.Trim();
    }
}
