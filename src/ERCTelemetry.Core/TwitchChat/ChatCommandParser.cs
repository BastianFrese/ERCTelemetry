namespace ERCTelemetry.Core.TwitchChat;

/// <summary>Turns a raw chat message into a <see cref="ParsedChatCommand"/>. Recognizes
/// <c>!gap</c>, <c>!pace</c> and <c>!reifen</c> (alias <c>!tyres</c>), case-insensitive.
/// Everything after the command word becomes the name tokens — the viewer does not need
/// to spell a driver name exactly (<c>!gap hauser erdi</c> works).</summary>
public static class ChatCommandParser
{
    /// <summary>Parses a chat message. Returns null when the message is not a recognized
    /// command (so the caller can ignore ordinary chatter).</summary>
    public static ParsedChatCommand? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '!')
        {
            return null;
        }

        var parts = trimmed[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var command = parts[0].ToLowerInvariant() switch
        {
            "gap" => ChatCommand.Gap,
            "pace" => ChatCommand.Pace,
            "reifen" or "tyres" => ChatCommand.Tyres,
            _ => (ChatCommand?)null,
        };

        if (command is null)
        {
            return null;
        }

        var tokens = parts.Skip(1).ToArray();
        return new ParsedChatCommand(command.Value, tokens);
    }
}
