using System.Globalization;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Layer-1 AI-Coach: turns the <see cref="DuelReport"/>'s trace tips and corner
/// losses into a structured German gap report with concrete advice. Deterministic and
/// offline — the "Tipp-Datenbank" (finding → Ratschlag) lives here as a pure mapping.
/// The player is always side A of the duel (the report is built for the player vs. a
/// rival); losses are therefore A's losses against B.</summary>
public static class CoachReportBuilder
{
    /// <summary>Builds the coach report from a duel. Returns null when there is no
    /// duel or no trace data to coach on (no tips and no corner losses).</summary>
    public static CoachReport? Build(DuelReport? duel)
    {
        if (duel is null)
        {
            return null;
        }

        var findings = new List<CoachFinding>();
        foreach (var loss in duel.CornerLosses)
        {
            findings.Add(new CoachFinding(
                loss.CornerNumber,
                loss.LossSeconds,
                loss.AtPercent,
                CornerAdvice(loss)));
        }

        foreach (var tip in duel.TraceTips)
        {
            findings.Add(new CoachFinding(
                0,
                (float)TipLossSeconds(tip),
                0,
                TipAdvice(tip)));
        }

        if (findings.Count == 0)
        {
            return null;
        }

        var total = duel.CornerLosses.Sum(l => (double)l.LossSeconds);
        return new CoachReport(duel.B.Name, total, findings);
    }

    /// <summary>Advice for one corner's time loss: the bigger the loss, the more
    /// pointed the tip. Invariant formatting so German systems never flip to a comma.</summary>
    private static string CornerAdvice(CornerLoss loss)
    {
        var lossText = loss.LossSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        return loss.LossSeconds switch
        {
            >= 0.5f => $"Kurve {loss.CornerNumber}: −{lossText}s — hier verlierst du am meisten. " +
                       "Bremse später und härter, nimm mehr Speed mit in die Kurve.",
            >= 0.2f => $"Kurve {loss.CornerNumber}: −{lossText}s — du bremst zu früh. " +
                       "Verschiebe den Bremspunkt nach hinten.",
            _ => $"Kurve {loss.CornerNumber}: −{lossText}s — kleine Unsicherheit. " +
                 "Fahre die Kurve weiter aus, das bringt Zeit am Kurvenausgang.",
        };
    }

    /// <summary>Advice for one trace tip, keyed by the tip kind. The tip text already
    /// names the laps; the advice adds the "what to do".</summary>
    private static string TipAdvice(TraceTip tip) => tip.Kind switch
    {
        "loss" => $"{tip.Text} — du verlierst Zeit auf der Geraden. " +
                  "Prüfe DRS-Fenster und Schaltpunkte.",
        "gain" => $"{tip.Text} — hier bist du schneller. Behalte diese Linie bei.",
        "brake" => $"{tip.Text} — verschiebe den Bremspunkt entsprechend.",
        "apex" => $"{tip.Text} — arbeite an der Kurvenausfahrt, mehr Speed am Apex.",
        _ => tip.Text,
    };

    /// <summary>Best-effort loss estimate for a trace tip: tips don't carry a numeric
    /// loss, so we report 0 and let the corner losses carry the numbers.</summary>
    private static double TipLossSeconds(TraceTip tip) => 0;
}
