using System.Globalization;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Persistence;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>Phase 5 gate: the export page names its sections, escapes stored names,
/// references no external assets, keeps numbers culture-invariant and survives an
/// empty report.</summary>
public class RaceReportHtmlExporterTests
{
    private static PerDriverSummary Driver(byte car, string name) => new(
        car, name, default, 7, (byte)(car + 1), 25f, "Active", "Finished",
        5, 1, 3, 89_500u, 90_000, 120,
        new SectorStats(30_000u, 30_500, 40), new SectorStats(29_000u, 29_400, 30),
        new SectorStats(30_500u, 31_000, 50),
        2, 1, 2, 0, 100.5, 12.3, 181.5, [], null);

    private static RaceReport MakeReport()
    {
        var header = new ReportHeader(1, "Race", "F1_Bahrain", 3, 5412, "OnlineCustom",
            "Clear", true, 30, 24, 0, "F1Modern", 80, 1, "Race", 5412,
            "2026-09-03T18:00:00Z", "checkered", 2, 3);
        var player = Driver(0, "A<b>&\"C");
        var rival = Driver(1, "Rival");
        return new RaceReport(
            header,
            [player, rival],
            player,
            [new ConsistencyRow(0, player.Name, 3, 89_500u, 90_000, 120, 0.9)],
            [new LapPointSeries(0, player.Name,
                [new LapPoint(1, 90_000u, 30_000u, 30_000u, 30_000u, 1, "F1C3", 1)])],
            [new PositionChunk(0, 1, [new byte[] { 1, 2 }])],
            new DuelReport(player, rival, 2, 0,
                [new DuelLapPair(1, 89_500u, 91_000u)],
                new SectorWinTally(1, 0, 1, 0, 0, 1),
                -1_500, -500, -30, [], [], []),
            [new TelemetryDb.DamageLogRow(0, DateTimeOffset.UtcNow, 1,
                new ERCTelemetry.Core.Session.CarDamageStatus(0, 10, 10, 10, 5, 5, false, 10, 0, 0, 0, 0), 20, 40)],
            [new TimelineEntry(DateTimeOffset.UtcNow, "Overtake", 0, 1, "passed", 1, 0)],
            null,
            [new TracedLap(0, player.Name,
                new LapTrace(1, 89_500u, 5412,
                    [new LapTraceSample(0, 120), new LapTraceSample(5412, 300)]))]);
    }

    [Fact]
    public void Render_contains_all_section_headers()
    {
        var html = RaceReportHtmlExporter.Render(MakeReport());
        foreach (var header in new[] { "Überblick", "Ergebnis", "Pace", "Sektor", "Vergleich",
                 "Stints", "Schaden", "Ereignisse", "Traces" })
        {
            Assert.Contains(header, html);
        }

        Assert.Contains("F1_Bahrain", html);
    }

    [Fact]
    public void Stored_names_are_html_escaped()
    {
        var html = RaceReportHtmlExporter.Render(MakeReport());
        Assert.Contains("&lt;b&gt;&amp;&quot;C", html);
        Assert.DoesNotContain("A<b>&\"C", html);
    }

    [Fact]
    public void No_external_assets_are_referenced()
    {
        var html = RaceReportHtmlExporter.Render(MakeReport());
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Numbers_are_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var html = RaceReportHtmlExporter.Render(MakeReport());
            Assert.Contains("−1.500", html);
            Assert.DoesNotContain("1,500", html);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Empty_report_renders_without_exception()
    {
        var html = RaceReportHtmlExporter.Render(
            new RaceReport(null, [], null, [], [], [], null, [], [], null, []));
        Assert.Contains("No session data", html);
    }
}
