namespace ERCTelemetry.Core.Llm;

/// <summary>Layer-2 live commentator backed by an <see cref="OllamaClient"/>: bundles the
/// recent Layer-1 lines + session context into a German prompt and returns one varied
/// commentary line. Null on failure — the overlay keeps the deterministic Layer-1 lines.</summary>
public sealed class LlmRaceCommentator : IRaceCommentator
{
    private readonly OllamaClient _client;

    public LlmRaceCommentator(OllamaClient client) => _client = client;

    public async Task<string?> CommentateAsync(CommentaryContext context, CancellationToken ct)
    {
        var system =
            "Du bist ein deutscher F1-Rennkommentator für einen Stream. Formuliere EINEN " +
            "kurzen, abwechslungsreichen Kommentar (max. 2 Sätze) auf Deutsch. Keine " +
            "Wiederholungen der vorgegebenen Ereignisse, kein Smalltalk, keine " +
            "Anführungszeichen, keine Emojis.";
        var user =
            $"Session: {context.SessionType} auf {context.Track}, Runde {context.Lap}/" +
            $"{context.TotalLaps}, Wetter: {context.Weather}. Spieler auf P{context.PlayerPosition}. " +
            $"Letzte Ereignisse: {string.Join(" | ", context.RecentLines)}";
        return await _client.CompleteAsync(system, user, ct).ConfigureAwait(false);
    }
}
