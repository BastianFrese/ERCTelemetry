using System.Globalization;
using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;

namespace ERCTelemetry.Core.TwitchChat;

/// <summary>Builds the German chat reply for a parsed command from the live snapshot.
/// No name tokens → the player's own row; otherwise each token is fuzzy-matched against
/// the field. One line per resolved driver, joined with " · ".</summary>
public static class ChatAnswerBuilder
{
    /// <summary>Builds the reply text. Never throws — a missing snapshot, an unknown
    /// driver or a driver without timing data all produce a helpful German message.</summary>
    public static string Build(ParsedChatCommand command, TelemetrySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "Noch keine Telemetrie-Daten — starte eine Session mit aktiver Aufnahme.";
        }

        var rows = snapshot.Standings;
        if (rows is null || rows.Count == 0)
        {
            return "Noch keine Positionsdaten — warte auf das nächste Telemetrie-Paket.";
        }

        var targets = ResolveTargets(command.NameTokens, rows);
        if (targets.Count == 0)
        {
            return command.NameTokens.Count == 0
                ? "Kein Spieler-Fahrer in dieser Session gefunden."
                : $"Fahrer '{command.NameTokens[0]}' nicht gefunden.";
        }

        var lines = targets.Select(t => BuildLine(command.Command, t.Row, t.Label));
        return string.Join(" · ", lines);
    }

    private static IReadOnlyList<(StandingsRow Row, string Label)> ResolveTargets(
        IReadOnlyList<string> tokens, IReadOnlyList<StandingsRow> rows)
    {
        if (tokens.Count == 0)
        {
            var player = rows.FirstOrDefault(r => r.IsPlayer);
            return player is null ? [] : [(player, player.Name)];
        }

        var names = rows.Select(r => r.Name).ToList();
        var result = new List<(StandingsRow, string)>();
        foreach (var token in tokens)
        {
            var matches = DriverNameMatcher.Match(token, names);
            if (matches.Count == 0)
            {
                continue;
            }

            var row = rows.First(r => string.Equals(r.Name, matches[0], StringComparison.OrdinalIgnoreCase));
            result.Add((row, row.Name));
        }

        return result;
    }

    private static string BuildLine(ChatCommand command, StandingsRow row, string label)
    {
        var prefix = $"{label}: ";
        return command switch
        {
            ChatCommand.Gap => prefix + BuildGap(row),
            ChatCommand.Pace => prefix + BuildPace(row),
            ChatCommand.Tyres => prefix + BuildTyres(row),
            _ => prefix + "Unbekannter Befehl.",
        };
    }

    private static string BuildGap(StandingsRow row)
    {
        var position = row.Position > 0 ? $"P{row.Position}" : "P?";
        if (row.GapToLeaderMs <= 0)
        {
            return $"{position} · FÜHRT";
        }

        var toLeader = FormatGap(row.GapToLeaderMs);
        var toFront = row.GapToCarInFrontMs > 0 ? $" · {FormatGap(row.GapToCarInFrontMs)} auf P{row.Position - 1}" : "";
        return $"{position} · {toLeader} zum Leader{toFront}";
    }

    private static string BuildPace(StandingsRow row)
    {
        var last = row.LastLapTimeMs > 0 ? FormatLap(row.LastLapTimeMs) : "—";
        var best = row.BestLapTimeMs > 0 ? FormatLap(row.BestLapTimeMs) : "—";
        return $"Letzte Runde {last} · Beste {best}";
    }

    private static string BuildTyres(StandingsRow row)
    {
        var compound = CompoundLabel(row.TyreCompound);
        return row.TyreAgeLaps > 0
            ? $"{compound} · {row.TyreAgeLaps} Runden alt"
            : $"{compound}";
    }

    /// <summary>m:ss.fff for lap times (0 → "—").</summary>
    private static string FormatLap(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>+s.fff for gaps (0 → "—"). Invariant culture — chat replies must not
    /// flip to a comma decimal separator on a German system.</summary>
    private static string FormatGap(int ms)
    {
        return ms <= 0 ? "—" : $"+{(ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}s";
    }

    /// <summary>F1C5 → "C5", F1Inter → "INT", F1Wet → "WET" — same labels as the HUD.</summary>
    private static string CompoundLabel(ActualCompound compound) => compound switch
    {
        ActualCompound.F1Wet => "WET",
        ActualCompound.F1Inter => "INT",
        _ when compound.ToString().StartsWith("F1C", StringComparison.Ordinal)
            => compound.ToString()[3..],
        _ => compound.ToString(),
    };
}
