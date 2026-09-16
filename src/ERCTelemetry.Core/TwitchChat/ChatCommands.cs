namespace ERCTelemetry.Core.TwitchChat;

/// <summary>The chat commands viewers can type. <see cref="Gap"/> = Abstand zum
/// Führenden/vorausfahrenden, <see cref="Pace"/> = letzte/beste Runde,
/// <see cref="Tyres"/> = Reifenmischung + Alter.</summary>
public enum ChatCommand
{
    Gap = 0,
    Pace = 1,
    Tyres = 2,
}

/// <summary>A parsed chat command: the command plus the raw name tokens the viewer
/// typed after it (e.g. <c>!gap hauser erdi</c> → Gap + ["hauser", "erdi"]).</summary>
public sealed record ParsedChatCommand(
    ChatCommand Command,
    IReadOnlyList<string> NameTokens);
