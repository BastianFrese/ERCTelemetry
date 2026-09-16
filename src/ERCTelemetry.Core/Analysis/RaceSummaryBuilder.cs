using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Layer-1 race summary: a deterministic German narrative built from the
/// <see cref="RaceReport"/> (start → events → finish), like Automated Insights/Wordsmith.
/// No LLM — pure templates over the report data, headless-testable. An LLM pass can
/// rewrite the paragraphs later (Layer 2).</summary>
public static class RaceSummaryBuilder
{
    /// <summary>Max timeline events woven into the player story (keeps the summary readable).</summary>
    private const int MaxPlayerEvents = 6;

    public static RaceSummary Build(RaceReport report)
    {
        if (report.Header is not { } h)
        {
            return new RaceSummary(string.Empty, []);
        }

        var paragraphs = new List<string>(5);
        paragraphs.Add(Intro(h));
        paragraphs.Add(Winner(report.Classification));
        if (report.Player is { } player)
        {
            paragraphs.Add(PlayerStory(player));
            paragraphs.Add(PlayerEvents(report.Timeline, player.CarIndex, player.Name));
        }

        if (report.Duel is { } duel)
        {
            paragraphs.Add(DuelStory(duel));
        }

        return new RaceSummary($"Rennzusammenfassung — {TrackLabel(h.Track)}", paragraphs);
    }

    private static string Intro(ReportHeader h)
    {
        var type = h.SessionType switch
        {
            "Race" => "Rennen",
            "TimeTrial" => "Zeitrennen",
            _ when h.SessionType.StartsWith("Qualifying", StringComparison.Ordinal) => "Qualifying",
            _ when h.SessionType.StartsWith("Practice", StringComparison.Ordinal) => "Training",
            _ => "Session",
        };
        return $"{type} auf {TrackLabel(h.Track)} über {h.TotalLaps} Runden, " +
               $"{h.NumDrivers} Fahrer am Start. Wetter: {h.Weather}, " +
               $"{h.TrackTemp:0}°C Strecke / {h.AirTemp:0}°C Luft.";
    }

    private static string Winner(IReadOnlyList<PerDriverSummary> classification)
    {
        var podium = classification
            .Where(s => s.Position is >= 1 and <= 3)
            .OrderBy(s => s.Position)
            .ToList();
        if (podium.Count == 0)
        {
            return "Keine Endwertung gespeichert.";
        }

        var winner = podium[0];
        var rest = podium.Skip(1).Select(s => s.Name).ToList();
        return rest.Count switch
        {
            0 => $"Sieg für {winner.Name}.",
            1 => $"Sieg für {winner.Name} vor {rest[0]}.",
            _ => $"Sieg für {winner.Name} vor {rest[0]} und {rest[1]}.",
        };
    }

    private static string PlayerStory(PerDriverSummary p)
    {
        var parts = new List<string>(4);
        if (p.GridPosition > 0 && p.Position > 0)
        {
            var gained = p.Position - p.GridPosition;
            parts.Add(gained switch
            {
                < 0 => $"Du startest von P{p.GridPosition} und beendest das Rennen auf P{p.Position} " +
                       $"({-gained} Positionen gewonnen).",
                > 0 => $"Du startest von P{p.GridPosition} und beendest das Rennen auf P{p.Position} " +
                       $"({gained} Positionen verloren).",
                _ => $"Du startest von P{p.GridPosition} und beendest das Rennen auf P{p.Position}.",
            });
        }
        else if (p.Position > 0)
        {
            parts.Add($"Du beendest das Rennen auf P{p.Position}.");
        }

        if (p.BestLapMs > 0)
        {
            parts.Add($"Deine schnellste Runde: {FormatMs(p.BestLapMs)}.");
        }

        if (p.NumPitStops > 0)
        {
            parts.Add($"Du legst {p.NumPitStops} Boxenstopps ein.");
        }

        if (p.LapsLed > 0)
        {
            parts.Add($"Du führst {p.LapsLed} Runden an.");
        }

        if (p.PenaltiesSeconds > 0)
        {
            parts.Add($"Strafen: {p.PenaltiesSeconds} s.");
        }

        return string.Join(" ", parts);
    }

    private static string PlayerEvents(
        IReadOnlyList<TimelineEntry> timeline, byte playerCarIndex, string playerName)
    {
        var sentences = new List<string>(MaxPlayerEvents);
        foreach (var t in timeline)
        {
            if (sentences.Count >= MaxPlayerEvents)
            {
                break;
            }

            var lap = t.LapNumber > 0 ? $" in Runde {t.LapNumber}" : string.Empty;
            switch (t.Type)
            {
                case "Overtake" when t.CarIndex == playerCarIndex:
                    sentences.Add($"Du überholst {OvertakenName(t.Text)}{lap}.");
                    break;
                case "Overtake" when t.SecondCarIndex == playerCarIndex:
                    sentences.Add($"Du wirst von {OvertakerName(t.Text)} überholt{lap}.");
                    break;
                case "FastestLap" when t.CarIndex == playerCarIndex:
                    sentences.Add($"Schnellste Runde{lap}.");
                    break;
                case "Penalty" when t.CarIndex == playerCarIndex:
                    sentences.Add($"Strafe{lap}.");
                    break;
                case "Warning" when t.CarIndex == playerCarIndex:
                    sentences.Add($"Verwarnung{lap}.");
                    break;
                case "Retirement" when t.CarIndex == playerCarIndex:
                    sentences.Add($"Du scheidest{lap} aus.");
                    break;
                case "SafetyCar":
                    sentences.Add($"Safety Car{lap}.");
                    break;
            }
        }

        return sentences.Count > 0
            ? string.Join(" ", sentences)
            : "Keine besonderen Ereignisse für dich gespeichert.";
    }

    private static string DuelStory(DuelReport duel)
    {
        var a = duel.A.Name;
        var b = duel.B.Name;
        var parts = new List<string>(3)
        {
            $"Im Duell gegen {b}: {duel.LapWinsA}:{duel.LapWinsB} Runden gewonnen.",
        };
        if (duel.BestDeltaMs != 0)
        {
            var faster = duel.BestDeltaMs < 0 ? a : b;
            parts.Add($"Beste Runde: {faster} um {Math.Abs(duel.BestDeltaMs) / 1000:0.000} s schneller.");
        }

        if (duel.CornerLosses.Count > 0)
        {
            var worst = duel.CornerLosses.OrderByDescending(c => c.LossSeconds).First();
            parts.Add($"Größter Verlust: Kurve {worst.CornerNumber} (−{worst.LossSeconds:0.000} s).");
        }

        return string.Join(" ", parts);
    }

    private static string TrackLabel(string track) =>
        track.StartsWith("F1_", StringComparison.Ordinal) ? track[3..] : track;

    private static string FormatMs(uint ms) =>
        ms <= 0 ? string.Empty : $"{ms / 60000}:{ms % 60000 / 1000:00}.{ms % 1000:000}";

    /// <summary>Name of the driver the player overtook. Both store texts reach the
    /// timeline: the German race-control event ("OVERHAUL! … du hast X überholt") from
    /// the events table, and the English "X passed Y" synthesized from the overtakes
    /// table. Falls back to the raw text so an unknown format still renders coherently.</summary>
    private static string OvertakenName(string text)
    {
        var ich = text.IndexOf(" du hast ", StringComparison.Ordinal);
        if (ich >= 0)
        {
            var from = ich + " du hast ".Length;
            var to = text.IndexOf(" überholt", from, StringComparison.Ordinal);
            if (to > from)
            {
                return text[from..to];
            }
        }

        var passed = text.IndexOf(" passed ", StringComparison.Ordinal);
        return passed >= 0 ? text[(passed + " passed ".Length)..] : text;
    }

    /// <summary>Name of the driver who overtook the player ("X passed Y" / "X hat dich
    /// überholt"). Never slices a negative index — an unrecognized format returns the raw
    /// text instead of throwing ArgumentOutOfRangeException.</summary>
    private static string OvertakerName(string text)
    {
        var passed = text.IndexOf(" passed ", StringComparison.Ordinal);
        if (passed >= 0)
        {
            return text[..passed];
        }

        var dich = text.IndexOf(" hat dich überholt", StringComparison.Ordinal);
        return dich >= 0 ? text[..dich] : text;
    }
}

/// <summary>Layer-1 race summary: a title plus narrative paragraphs (German).</summary>
public sealed record RaceSummary(string Title, IReadOnlyList<string> Paragraphs);
