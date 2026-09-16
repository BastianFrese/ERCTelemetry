namespace ERCTelemetry.Core.TwitchChat;

/// <summary>Fuzzy driver-name matching for chat commands. Viewers rarely spell a name
/// exactly ("Manuel Hauser2", "Erdi10") — a token like "hauser" or "erdi" must still
/// resolve. Matching is case-insensitive and prefers exact, then prefix, then substring;
/// a token can match several drivers (all are returned, best first).</summary>
public static class DriverNameMatcher
{
    /// <summary>Matches one viewer-typed token against the driver names. Returns the
    /// matched names ordered by match quality (exact → prefix → substring), then
    /// alphabetically. Empty when nothing matches.</summary>
    public static IReadOnlyList<string> Match(string token, IReadOnlyList<string> names)
    {
        if (string.IsNullOrWhiteSpace(token) || names is null || names.Count == 0)
        {
            return [];
        }

        var needle = token.Trim().ToLowerInvariant();
        if (needle.Length == 0)
        {
            return [];
        }

        var hits = new List<(int Score, string Name)>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var haystack = name.Trim().ToLowerInvariant();
            var score = Score(needle, haystack);
            if (score > 0)
            {
                hits.Add((score, name.Trim()));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Select(h => h.Name)
            .ToList();
    }

    /// <summary>3 = exact, 2 = prefix, 1 = substring, 0 = no match.</summary>
    private static int Score(string needle, string haystack)
    {
        if (haystack == needle)
        {
            return 3;
        }

        if (haystack.StartsWith(needle, StringComparison.Ordinal))
        {
            return 2;
        }

        return haystack.Contains(needle, StringComparison.Ordinal) ? 1 : 0;
    }
}
