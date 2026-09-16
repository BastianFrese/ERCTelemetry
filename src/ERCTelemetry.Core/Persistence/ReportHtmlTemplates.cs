namespace ERCTelemetry.Core.Persistence;

/// <summary>Self-contained HTML report styling: inline CSS custom properties in the
/// erdi-erc.de palette, no external assets (fonts degrade to system stacks). Numbers are
/// always rendered by the exporter with CultureInfo.InvariantCulture.</summary>
public static class ReportHtmlTemplates
{
    public const string PageHead = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
:root {
  --bg: #030407; --panel: #0C1018; --border: #232833;
  --text: #F4F6FB; --muted: #B8BFCB; --dim: #6B7480;
  --accent: #E10600; --blue: #6EA8FE; --green: #35D07F;
  --yellow: #F3D02F; --red: #FF5A4E; --purple: #A26BF0;
  --radius: 8px;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text);
  font-family: "Segoe UI", system-ui, sans-serif; font-size: 14px; }
.wrap { max-width: 1280px; margin: 0 auto; padding: 24px 16px 48px; }
h1, h2, .brand { font-family: Orbitron, "Cascadia Mono", monospace; letter-spacing: 1px; }
h1 { font-size: 26px; margin: 0 0 4px; }
h2 { font-size: 15px; text-transform: uppercase; margin: 0 0 12px; color: var(--muted);
  border-bottom: 1px solid var(--border); padding-bottom: 8px; }
h2 .bar { color: var(--accent); margin-right: 8px; }
section { background: var(--panel); border: 1px solid var(--border);
  border-radius: var(--radius); padding: 16px 18px; margin: 0 0 16px; }
.badge { display: inline-block; border: 1px solid var(--border); border-radius: var(--radius);
  padding: 3px 10px; margin: 2px 6px 2px 0; color: var(--muted);
  font-family: "Cascadia Mono", monospace; font-size: 12px; }
.kpi { display: inline-block; background: var(--bg); border: 1px solid var(--border);
  border-radius: var(--radius); padding: 8px 12px; margin: 2px 6px 2px 0; }
.kpi b { display: block; font-family: "Cascadia Mono", monospace; font-size: 15px; }
.kpi span { color: var(--dim); font-size: 11px; text-transform: uppercase; }
table { width: 100%; border-collapse: collapse; font-family: "Cascadia Mono", monospace;
  font-size: 12px; }
th { text-align: left; color: var(--dim); border-bottom: 1px solid var(--border);
  padding: 6px 8px; text-transform: uppercase; font-weight: normal; }
td { border-bottom: 1px solid var(--border); padding: 5px 8px; color: var(--muted); }
td.num { text-align: right; }
tr.p1 td { color: var(--text); }
.pos { color: var(--text); font-weight: bold; }
.gain { color: var(--green); } .loss { color: var(--red); }
.fast { color: var(--purple); }
svg { max-width: 100%; height: auto; display: block; }
.tip { border-left: 3px solid var(--accent); padding: 6px 10px; margin: 6px 0;
  background: var(--bg); color: var(--muted); font-family: "Cascadia Mono", monospace;
  font-size: 12px; }
.dim { color: var(--dim); } .muted { color: var(--muted); }
footer { color: var(--dim); font-size: 11px; margin-top: 24px; text-align: center; }
</style>
""";

    public const string PageTail = """
<footer>ERCTelemetry · generated locally from stored session telemetry</footer>
</div>
</body>
</html>
""";
}
