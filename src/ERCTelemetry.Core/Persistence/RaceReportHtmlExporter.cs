using System.Globalization;
using System.Net;
using System.Text;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Persistence;

/// <summary>Renders a RaceReport as one self-contained HTML page (inline CSS + SVG, no
/// external assets). Section order mirrors the in-app report tab. All numbers are formatted
/// with <see cref="CultureInfo.InvariantCulture"/>; every stored name/text is HTML-encoded
/// before it reaches the page.</summary>
public static class RaceReportHtmlExporter
{
    private static readonly CultureInfo I = CultureInfo.InvariantCulture;

    public static void Write(string path, RaceReport report)
    {
        File.WriteAllText(path, Render(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Render(RaceReport report)
    {
        var sb = new StringBuilder();
        sb.Append(ReportHtmlTemplates.PageHead);
        sb.AppendLine($"<title>{Esc("Race report — " + (report.Header?.Track ?? "session"))}</title>");
        sb.AppendLine("</head><body><div class=\"wrap\">");
        sb.AppendLine("<div class=\"brand\" style=\"color:var(--accent);font-size:12px\">" +
            "ERCTELEMETRY · RACE REPORT</div>");
        if (report.Header is not { } header)
        {
            sb.AppendLine("<p class=\"muted\">No session data.</p>");
        }
        else
        {
            RenderOverview(sb, header, report);
            RenderClassification(sb, report);
            RenderPace(sb, report);
            RenderSectors(sb, report);
            RenderDuel(sb, report);
            RenderField(sb, report);
            RenderStints(sb, report);
            RenderDamage(sb, report);
            RenderTimeline(sb, report);
            RenderTraces(sb, report);
        }

        sb.AppendLine("</div>");
        sb.Append(ReportHtmlTemplates.PageTail);
        return sb.ToString();
    }

    private static void RenderOverview(StringBuilder sb, ReportHeader h, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">01</span>Überblick</h2>");
        sb.AppendLine("<p>");
        sb.Append(Badge(h.SessionType)).Append(Badge(h.Track));
        sb.Append(Badge(h.Weather)).Append(
            $"{h.TrackTemp}°C track · {h.AirTemp}°C air");
        sb.Append(Badge(h.Formula)).Append(Badge(h.RuleSet));
        sb.Append(Badge($"pit {h.PitSpeedLimit} km/h")).Append(
            $"{h.NumDrsZones} DRS zones");
        sb.Append(Badge($"{h.NumDrivers} drivers")).Append(
            $"{h.NumLapsStored} laps stored");
        sb.Append(Badge(h.GameMode)).Append(
            h.IsNetworkGame ? "online" : "offline");
        sb.AppendLine("</p>");
        if (report.Player is { } p)
        {
            sb.Append(Kpi("Position", p.Position > 0 ? $"P{p.Position}" : "—"));
            sb.Append(Kpi("Punkte", p.Points.ToString("0", I)));
            sb.Append(Kpi("Grid", p.GridPosition > 0 ? $"P{p.GridPosition}" : "—"));
            sb.Append(Kpi("Gewinn", p.PositionsGained.ToString("+0;-0;0", I)));
            sb.Append(Kpi("Beste Runde", FormatMs(p.BestLapMs)));
            sb.Append(Kpi("Ø Runde", FormatMs(p.AverageLapMs)));
            sb.Append(Kpi("σ", FormatMs(p.ConsistencySigmaMs)));
            sb.Append(Kpi("Stops", p.NumPitStops.ToString(I)));
            sb.Append(Kpi("Runden geführt", p.LapsLed.ToString(I)));
            sb.Append(Kpi("Fuel", p.FuelUsedLitres.ToString("0.0", I) + " L"));
            sb.Append(Kpi("ERS", p.ErsUsedMegajoules.ToString("0.0", I) + " MJ"));
            sb.AppendLine("</p>");
        }
        sb.AppendLine("</section>");
    }

    private static void RenderClassification(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">02</span>Ergebnis &amp; Feld</h2>");
        sb.AppendLine("<table><tr><th>P</th><th>Fahrer</th><th>Team</th><th>#</th>" +
            "<th>Rd</th><th>Grid</th><th>Pts</th><th>Best</th><th>Gap</th><th>Ø</th>" +
            "<th>σ</th><th>Valid</th><th>Stops</th><th>+/-</th><th>Lead</th><th>Pen</th>" +
            "<th>Status</th></tr>");
        var leaderBest = report.Classification
            .Select(x => x.BestLapMs)
            .Where(x => x > 0)
            .DefaultIfEmpty(0u)
            .Min();
        foreach (var d in report.Classification)
        {
            sb.AppendLine("<tr class=\"" + (d.Position == 1 ? "p1" : "") + "\">");
            sb.Append(CellPos(d.Position));
            sb.Append($"<td>{Esc(d.Name)}</td>");
            sb.Append($"<td>{Esc(d.Team.Display())}</td>");
            sb.Append(NumCell(d.RaceNumber.ToString(I)));
            sb.Append(NumCell(d.RacingLaps.ToString(I)));
            sb.Append(NumCell(d.GridPosition > 0 ? $"P{d.GridPosition}" : "—"));
            sb.Append(NumCell(d.Points.ToString("0", I)));
            sb.Append(NumCell(FormatMs(d.BestLapMs)));
            sb.Append(NumCell(FormatGap(d.BestLapMs, leaderBest)));
            sb.Append(NumCell(FormatMs(d.AverageLapMs)));
            sb.Append(NumCell(FormatMs(d.ConsistencySigmaMs)));
            sb.Append(NumCell(d.LapsValid.ToString(I)));
            sb.Append(NumCell(d.NumPitStops.ToString(I)));
            sb.Append(NumCell(GainCell(d.PositionsGained)));
            sb.Append(NumCell(d.LapsLed.ToString(I)));
            sb.Append(NumCell(d.PenaltiesSeconds > 0
                ? d.PenaltiesSeconds.ToString("0 \"s\"", I) : "—"));
            sb.AppendLine($"<td>{Esc(d.ResultStatus)}{DetailSuffix(d.ResultReason)}</td></tr>");
        }

        sb.AppendLine("</table></section>");
    }

    private static void RenderPace(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">03</span>Pace &amp; Konsistenz</h2>");
        sb.AppendLine("<table><tr><th>Fahrer</th><th>Laps</th><th>Best</th><th>Ø</th>" +
            "<th>σ</th><th>Score</th></tr>");
        foreach (var c in report.Consistency)
        {
            sb.AppendLine($"<tr><td>{Esc(c.DriverName)}</td>");
            sb.Append(NumCell(c.RacingLaps.ToString(I)));
            sb.Append(NumCell(FormatMs(c.BestLapMs)));
            sb.Append(NumCell(FormatMs(c.AverageLapMs)));
            sb.Append(NumCell(FormatMs(c.ConsistencySigmaMs)));
            sb.AppendLine(NumCell(c.Score.ToString("0.0", I)) + "</tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine("<p class=\"dim\">Rundenzeiten aller Fahrer (ms über Runde)</p>");
        sb.AppendLine(LapTimesSvg(report.LapSeries));
        sb.AppendLine("</section>");
    }

    private static void RenderSectors(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">04</span>Sektor-Deep-Dive</h2>");
        sb.AppendLine("<table><tr><th>Fahrer</th><th>S1 best</th><th>S1 Ø</th>" +
            "<th>S2 best</th><th>S2 Ø</th><th>S3 best</th><th>S3 Ø</th>" +
            "<th>Schwächster Sektor</th></tr>");
        foreach (var d in report.Classification)
        {
            var weakest = WeakestSector(d);
            sb.AppendLine($"<tr><td>{Esc(d.Name)}</td>");
            sb.Append(NumCell(FormatMs(d.S1.BestMs) + " / " + FormatMs(d.S1.AverageMs)));
            sb.Append(NumCell(FormatMs(d.S2.BestMs) + " / " + FormatMs(d.S2.AverageMs)));
            sb.Append(NumCell(FormatMs(d.S3.BestMs) + " / " + FormatMs(d.S3.AverageMs)));
            sb.AppendLine($"<td class=\"fast\">{weakest}</td></tr>");
        }

        sb.AppendLine("</table></section>");
    }

    private static string WeakestSector(PerDriverSummary d)
    {
        var candidates = new (string Name, double Loss)[]
        {
            ("S1", d.S1.AverageMs - d.S1.BestMs),
            ("S2", d.S2.AverageMs - d.S2.BestMs),
            ("S3", d.S3.AverageMs - d.S3.BestMs),
        };
        var seen = candidates.Where(c => c.Loss > 0).ToList();
        return seen.Count == 0
            ? "—"
            : $"{seen.OrderByDescending(c => c.Loss).First().Name} " +
              $"(+{seen.OrderByDescending(c => c.Loss).First().Loss.ToString("0.000 s", I)})";
    }

    private static void RenderDuel(StringBuilder sb, RaceReport report)
    {
        if (report.Duel is not { } duel)
        {
            sb.AppendLine("<section><h2><span class=\"bar\">05</span>Fahrer-Vergleich</h2>" +
                "<p class=\"dim\">Kein Duell gewählt (im Export wird A/B standardmäßig nicht gesetzt).</p></section>");
            return;
        }

        sb.AppendLine("<section><h2><span class=\"bar\">05</span>Fahrer-Vergleich</h2>");
        sb.AppendLine($"<p><span class=\"pos\">{Esc(duel.A.Name)}</span> vs. " +
            $"<span class=\"pos\">{Esc(duel.B.Name)}</span></p>");
        sb.Append(Kpi("Rundensiege A", duel.LapWinsA.ToString(I)));
        sb.Append(Kpi("Rundensiege B", duel.LapWinsB.ToString(I)));
        sb.Append(Kpi("Δ Best", FormatDeltaMs(duel.BestDeltaMs)));
        sb.Append(Kpi("Δ Ø", FormatDeltaMs(duel.AverageDeltaMs)));
        sb.Append(Kpi("Δ σ", FormatDeltaMs(duel.SigmaDeltaMs)));
        sb.Append(Kpi("Sektorsiege S1", $"{duel.SectorWins.A1} : {duel.SectorWins.B1}"));
        sb.Append(Kpi("Sektorsiege S2", $"{duel.SectorWins.A2} : {duel.SectorWins.B2}"));
        sb.Append(Kpi("Sektorsiege S3", $"{duel.SectorWins.A3} : {duel.SectorWins.B3}"));
        sb.AppendLine("</p>");
        sb.AppendLine("<table><tr><th>Rd</th><th>" + Esc(duel.A.Name) + "</th><th>" +
            Esc(duel.B.Name) + "</th><th>Δ</th></tr>");
        foreach (var pair in duel.LapPairs)
        {
            var delta = (double)pair.LapTimeA - pair.LapTimeB;
            sb.AppendLine($"<tr><td>{pair.LapNumber.ToString(I)}</td>");
            sb.Append(NumCell(FormatMs(pair.LapTimeA)));
            sb.Append(NumCell(FormatMs(pair.LapTimeB)));
            sb.AppendLine(NumCell(FormatDeltaMs(delta)) + "</tr>");
        }

        sb.AppendLine("</table>");
        foreach (var tip in duel.TraceTips)
        {
            sb.AppendLine($"<div class=\"tip\">{Esc(tip.Text)}</div>");
        }

        sb.AppendLine("</section>");
    }

    private static void RenderField(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">06</span>Feld-Vergleich</h2>");
        var chunks = report.FieldPositions;
        if (chunks.Count == 0)
        {
            sb.AppendLine("<p class=\"dim\">Keine Positionsdaten gespeichert.</p></section>");
            return;
        }

        sb.AppendLine("<p class=\"dim\">Positions-Matrix (grün = Führung, je Runde ein Streifen)</p>");
        sb.AppendLine(PositionMatrixSvg(report));
        sb.AppendLine("</section>");
    }

    private static void RenderStints(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">07</span>Stints &amp; Strategie</h2>");
        if (report.Player is not { } player || player.Stints.Count == 0)
        {
            sb.AppendLine("<p class=\"dim\">Keine Stint-Daten.</p></section>");
            return;
        }

        sb.AppendLine("<table><tr><th>Stint</th><th>Reifen</th><th>Alter</th>" +
            "<th>Runden</th><th>Best</th><th>Ø</th><th>Degr./Rd</th><th>σ</th></tr>");
        foreach (var s in player.Stints)
        {
            sb.AppendLine($"<tr><td>{s.StintNumber.ToString(I)}</td>");
            sb.Append($"<td class=\"pos\">{Esc(s.Tyre)}</td>");
            sb.Append(NumCell(s.AgeAtStart.ToString(I)));
            sb.Append(NumCell(s.Laps.ToString(I)));
            sb.Append(NumCell(FormatMs(s.BestLapMs)));
            sb.Append(NumCell(FormatMs(s.AverageLapMs)));
            sb.Append(NumCell(FormatMs(s.DegradationMsPerLap)));
            sb.AppendLine(NumCell(FormatMs(s.ConsistencySigmaMs)) + "</tr>");
        }

        sb.AppendLine("</table></section>");
    }

    private static void RenderDamage(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">08</span>Schaden &amp; Zuverlässigkeit</h2>");
        if (report.DamageLog.Count == 0)
        {
            sb.AppendLine("<p class=\"dim\">Keine Schaden-Änderungen geloggt.</p></section>");
            return;
        }

        sb.AppendLine("<table><tr><th>UTC</th><th>Rd</th><th>Car</th><th>FLW</th>" +
            "<th>FRW</th><th>RW</th><th>Floor</th><th>Sidepod</th><th>Engine</th>" +
            "<th>Getriebe</th><th>Fehler</th></tr>");
        foreach (var row in report.DamageLog)
        {
            var d = row.Damage;
            sb.AppendLine($"<tr><td>{row.Utc.ToLocalTime().ToString("HH:mm:ss", I)}</td>");
            sb.Append(NumCell(row.LapNumber.ToString(I)));
            sb.Append(NumCell((row.CarIndex + 1).ToString(I)));
            sb.Append(NumCell(d.FrontLeftWing.ToString(I)));
            sb.Append(NumCell(d.FrontRightWing.ToString(I)));
            sb.Append(NumCell(d.RearWing.ToString(I)));
            sb.Append(NumCell(d.Floor.ToString(I)));
            sb.Append(NumCell(d.Sidepod.ToString(I)));
            sb.Append(NumCell(d.EngineDamage.ToString(I)));
            sb.Append(NumCell(d.GearBoxDamage.ToString(I)));
            var faults = new List<string>();
            if (d.DrsFault) { faults.Add("DRS"); }
            if (d.GearBoxFault) { faults.Add("gearbox"); }
            if (d.EngineBlown) { faults.Add("engine blown"); }
            if (d.EngineSeized) { faults.Add("engine seized"); }
            sb.AppendLine($"<td>{Esc(string.Join(", ", faults))}</td></tr>");
        }

        sb.AppendLine("</table></section>");
    }

    private static void RenderTimeline(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">09</span>Ereignisse</h2>");
        if (report.Timeline.Count == 0)
        {
            sb.AppendLine("<p class=\"dim\">Keine Ereignisse.</p></section>");
            return;
        }

        foreach (var t in report.Timeline)
        {
            var lap = t.LapNumber > 0 ? $" · R{t.LapNumber}" : string.Empty;
            sb.AppendLine($"<div class=\"tip\"><span class=\"dim\">" +
                $"{t.Utc.ToLocalTime().ToString("HH:mm:ss", I)}{lap}</span> " +
                $"{Esc(t.Type)} — {Esc(t.Text)}</div>");
        }

        sb.AppendLine("</section>");
    }

    private static void RenderTraces(StringBuilder sb, RaceReport report)
    {
        sb.AppendLine("<section><h2><span class=\"bar\">10</span>Traces</h2>");
        if (report.Traces.Count == 0)
        {
            sb.AppendLine("<p class=\"dim\">Keine Speed-Traces gespeichert.</p></section>");
            return;
        }

        foreach (var t in report.Traces)
        {
            sb.AppendLine($"<p class=\"muted\">Best-Lap-Trace — {Esc(t.Name)} " +
                $"(Runde {t.Trace.LapNumber.ToString(I)}, {FormatMs(t.Trace.LapTimeMs)})</p>");
            sb.AppendLine(TraceSvg(t.Trace));
        }

        sb.AppendLine("</section>");
    }

    private static string[] ChartColors { get; } =
        ["#6EA8FE", "#35D07F", "#F3D02F", "#FF5A4E", "#A26BF0", "#E10600"];

    private static string LapTimesSvg(IReadOnlyList<LapPointSeries> series)
    {
        var points = series.Where(s => s.Points.Count > 0).ToList();
        if (points.Count == 0)
        {
            return "<p class=\"dim\">Keine Rundenzeiten.</p>";
        }

        const double w = 1200, h = 60 + 30;
        var maxLap = points.Max(s => s.Points.Max(p => p.LapNumber));
        var minMs = points.Min(s => s.Points.Where(p => p.LapTimeMs > 0).Min(p => (double)p.LapTimeMs));
        var maxMs = points.Max(s => s.Points.Where(p => p.LapTimeMs > 0).Max(p => (double)p.LapTimeMs));
        double X(int lap) => 50 + (lap - 1) * (w - 70) / Math.Max(maxLap - 1, 1);
        double Y(double ms) => 30 + (1 - (ms - minMs) / Math.Max(maxMs - minMs, 1)) * h;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg viewBox=\"0 0 {w.ToString("0", I)} {(h + 60).ToString("0", I)}\" " +
            "role=\"img\">");
        sb.AppendLine($"<line x1=\"50\" y1=\"{Y(minMs).ToString("0.0", I)}\" x2=\"{w.ToString("0", I)}\" " +
            $"y2=\"{Y(minMs).ToString("0.0", I)}\" stroke=\"#232833\"/>");
        sb.AppendLine($"<text x=\"50\" y=\"{(Y(minMs) - 4).ToString("0.0", I)}\" fill=\"#6B7480\" " +
            $"font-size=\"11\">schnellste {FormatMs(minMs)}</text>");
        var i = 0;
        foreach (var s in points)
        {
            var color = ChartColors[i % ChartColors.Length];
            var pts = string.Join(" ", s.Points
                .Where(p => p.LapTimeMs > 0)
                .Select(p => $"{X(p.LapNumber).ToString("0.0", I)},{Y(p.LapTimeMs).ToString("0.0", I)}"));
            sb.AppendLine($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" " +
                $"points=\"{pts}\"/>");
            var tx = (w - 60).ToString("0", I);
            var ty = (20 + i * 14).ToString("0", I);
            sb.AppendLine($"<text x=\"{tx}\" y=\"{ty}\" fill=\"{color}\" " +
                $"font-size=\"11\">{Esc(s.Name)}</text>");
            i++;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string TraceSvg(LapTrace trace)
    {
        if (trace.Samples.Count == 0)
        {
            return "<p class=\"dim\">Leerer Trace.</p>";
        }

        const double w = 1200, h = 220;
        var maxSpeed = trace.Samples.Max(s => (double)s.Speed);
        var maxDistance = (double)trace.Samples[^1].LapDistance;
        if (maxSpeed <= 0 || maxDistance <= 0)
        {
            return "<p class=\"dim\">Leerer Trace.</p>";
        }

        var pts = string.Join(" ", trace.Samples.Select(s =>
            $"{(10 + s.LapDistance / maxDistance * (w - 20)).ToString("0.0", I)}," +
            $"{(10 + (1 - s.Speed / maxSpeed) * (h - 20)).ToString("0.0", I)}"));
        return $"<svg viewBox=\"0 0 {w.ToString("0", I)} {(h + 10).ToString("0", I)}\" " +
            $"role=\"img\"><polyline fill=\"none\" stroke=\"#6EA8FE\" stroke-width=\"1.5\" " +
            $"points=\"{pts}\"/></svg>";
    }

    private static string PositionMatrixSvg(RaceReport report)
    {
        var sb = new StringBuilder();
        foreach (var chunk in report.FieldPositions)
        {
            var laps = Math.Min(chunk.NumLaps, 60);
            var carSlots = chunk.Rows.Count > 0 ? chunk.Rows[0].Count : 0;
            if (laps == 0 || carSlots == 0)
            {
                continue;
            }

            var cellW = 10;
            var cellH = 8;
            var w = laps * cellW;
            var h = carSlots * cellH;
            sb.AppendLine($"<p class=\"muted\">Runden {chunk.StartingLap + 1}–" +
                $"{chunk.StartingLap + chunk.NumLaps}</p>");
            sb.AppendLine($"<svg viewBox=\"0 0 {w.ToString("0", I)} {h.ToString("0", I)}\" " +
                "role=\"img\" style=\"width:100%\">");
            for (var lap = 0; lap < laps; lap++)
            {
                if (lap >= chunk.Rows.Count)
                {
                    break;
                }

                for (var car = 0; car < carSlots; car++)
                {
                    var pos = chunk.Rows[lap][car];
                    if (pos == 0)
                    {
                        continue;
                    }

                    var color = pos == 1
                        ? "#35D07F"
                        : pos <= 6
                            ? "#6EA8FE"
                            : pos <= 12 ? "#6B7480" : "#232833";
                    sb.AppendLine($"<rect x=\"{((lap * cellW).ToString("0", I))}\" " +
                        $"y=\"{((car * cellH).ToString("0", I))}\" width=\"{cellW - 1}\" " +
                        $"height=\"{cellH - 1}\" fill=\"{color}\"><title>{pos}</title></rect>");
                }
            }

            sb.AppendLine("</svg>");
        }

        return sb.Length == 0 ? "<p class=\"dim\">Keine Positionsdaten.</p>" : sb.ToString();
    }

    private static string Badge(string text) =>
        $"<span class=\"badge\">{Esc(text)}</span>";

    private static string Kpi(string label, string value) =>
        $"<span class=\"kpi\"><b>{Esc(value)}</b><span>{Esc(label)}</span></span>";

    private static string CellPos(byte position) =>
        position > 0 ? $"<td class=\"pos\">{position.ToString(I)}</td>" : "<td>—</td>";

    private static string NumCell(string content) =>
        $"<td class=\"num\">{content}</td>";

    private static string GainCell(int gained) => gained == 0
        ? "—"
        : gained > 0
            ? $"<span class=\"gain\">+{gained.ToString(I)}</span>"
            : $"<span class=\"loss\">{gained.ToString(I)}</span>";

    private static string DetailSuffix(string reason) => string.IsNullOrEmpty(reason)
        ? string.Empty
        : $" <span class=\"dim\">{Esc(reason)}</span>";

    private static string FormatMs(uint ms) => ms <= 0
        ? "—"
        : $"{(ms / 60000).ToString(I)}:{((ms % 60000) / 1000).ToString("00", I)}." +
            $"{(ms % 1000).ToString("000", I)}";

    private static string FormatMs(double ms) => ms <= 0
        ? "—"
        : $"{((long)ms / 60000).ToString(I)}:{(((long)ms % 60000) / 1000).ToString("00", I)}." +
            $"{((long)ms % 1000).ToString("000", I)}";

    private static string FormatMs(double ms, string fallback) => ms <= 0 ? fallback : FormatMs(ms);

    /// <summary>Gap to the leader as "+s.mmm"; empty for the leader itself.</summary>
    private static string FormatGap(uint bestMs, uint leaderBestMs)
    {
        if (bestMs <= 0 || leaderBestMs <= 0 || bestMs <= leaderBestMs)
        {
            return bestMs <= 0 ? "—" : "—";
        }

        var gap = (double)bestMs - leaderBestMs;
        return gap < 60_000
            ? $"+{(gap / 1000).ToString("0.000", I)}"
            : $"+{((long)gap / 60000).ToString(I)}:{(((long)gap % 60000) / 1000).ToString("00", I)}.{(((long)gap % 60000) % 1000).ToString("000", I)}";
    }

    /// <summary>Signed delta in seconds, "+0.123" (B faster) / "−0.123" (A faster).</summary>
    private static string FormatDeltaMs(double deltaMs)
    {
        if (Math.Abs(deltaMs) < 0.0005)
        {
            return "0.000";
        }

        return (deltaMs > 0 ? "+" : "−") + Math.Abs(deltaMs / 1000).ToString("0.000", I);
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
