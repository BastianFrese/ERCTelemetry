using System.Globalization;
using System.Net;

namespace ERCTelemetry.Core.Share;

/// <summary>Renders the public session page from a <see cref="ShareManifest"/> — pure
/// static, no JS/CORS needed. Every user-supplied string is HTML-escaped (XSS).</summary>
public static class SharePageRenderer
{
    /// <summary>Dark theme matching erdi-erc.de (BG #030407, panels #0C1018, accent
    /// #E10600). Kept as a plain const so the interpolated page needs no brace escaping.</summary>
    private const string Css = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin: 0; background: #030407; color: #E8E8E8;
               font-family: system-ui, -apple-system, "Segoe UI", sans-serif; line-height: 1.5; }
        main { max-width: 900px; margin: 0 auto; padding: 32px 20px 64px; }
        header { border-bottom: 1px solid #1A2230; padding-bottom: 20px; margin-bottom: 28px; }
        h1 { margin: 0 0 6px; font-size: 28px; letter-spacing: 0.5px; }
        h2 { font-size: 15px; text-transform: uppercase; letter-spacing: 2px;
             color: #8A93A6; margin: 0 0 14px; }
        .meta { margin: 0; color: #8A93A6; font-size: 14px; }
        section { margin-bottom: 40px; }
        table { width: 100%; border-collapse: collapse; font-size: 14px; }
        th { text-align: left; color: #8A93A6; font-weight: 500; font-size: 12px;
             text-transform: uppercase; letter-spacing: 1px; padding: 8px 10px;
             border-bottom: 1px solid #1A2230; }
        td { padding: 8px 10px; border-bottom: 1px solid #141B26; }
        .pos { color: #E10600; font-weight: 700; }
        .muted { color: #8A93A6; }
        .clip { background: #0C1018; border: 1px solid #1A2230; border-radius: 8px;
                padding: 16px; margin-bottom: 16px; }
        .clip video { width: 100%; border-radius: 6px; background: #000; }
        .clip .clip-meta { margin-top: 10px; font-size: 13px; color: #C6CBD6; }
        .clip .clip-meta b { color: #E8E8E8; }
        """;

    /// <summary>Renders the full HTML page. <paramref name="baseUrl"/> is the origin the
    /// page is served from (used for the clip video srcs, e.g. "https://telemetrie.erdi-erc.de").</summary>
    public static string Render(ShareManifest manifest, string baseUrl)
    {
        var track = Esc(manifest.Track);
        var sessionType = Esc(manifest.SessionType);
        var start = manifest.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var duration = FormatDuration(manifest.DurationSeconds);
        var baseUrlTrimmed = baseUrl.TrimEnd('/');

        var results = string.Join("\n", (manifest.Results ?? []).Select(r => $"""
            <tr>
              <td class="pos">{r.Position}</td>
              <td>{Esc(r.Name)}</td>
              <td class="muted">{Esc(r.Team)}</td>
              <td>{r.Laps}</td>
              <td>{FormatLap(r.BestLapMs)}</td>
              <td>{FormatTotal(r.TotalSeconds)}</td>
              <td class="muted">{Esc(r.Status)}</td>
              <td>{r.Points:0.#}</td>
            </tr>
            """));

        var clips = string.Join("\n", (manifest.Clips ?? []).Select(c => ClipCard(manifest, c, baseUrlTrimmed)));

        return $"""
            <!DOCTYPE html>
            <html lang="de">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Session — {track}</title>
            <style>
            {Css}
            </style>
            </head>
            <body>
            <main>
              <header>
                <h1>{track}</h1>
                <p class="meta">{sessionType} · {start} Uhr · {duration}</p>
              </header>
              <section>
                <h2>Ergebnis</h2>
                <table>
                  <thead>
                    <tr><th>Pos</th><th>Fahrer</th><th>Team</th><th>Runden</th><th>Beste Runde</th><th>Gesamt</th><th>Status</th><th>Punkte</th></tr>
                  </thead>
                  <tbody>
            {results}
                  </tbody>
                </table>
              </section>
              <section>
                <h2>Kollisions-Clips</h2>
            {clips}
              </section>
            </main>
            </body>
            </html>
            """;
    }

    private static string ClipCard(ShareManifest manifest, ShareClip clip, string baseUrl)
    {
        var src = $"{baseUrl}/s/{manifest.SessionUid}/clips/{Esc(clip.FileName)}";
        var drivers = clip.SecondDriverName is { } second
            ? $"{Esc(clip.DriverName)} vs {Esc(second)}"
            : $"{Esc(clip.DriverName)} vs Umgebung";
        var lap = clip.LapNumber > 0 ? $"Runde {clip.LapNumber}" : "—";
        var severity = clip.Severity switch
        {
            0 => "gering",
            1 => "mittel",
            _ => "hoch",
        };

        return $"""
            <div class="clip">
              <video controls preload="metadata" src="{src}"></video>
              <div class="clip-meta"><b>{lap}</b> · {drivers} · Schwere: {severity} · {clip.DurationSeconds:0.0} s</div>
            </div>
            """;
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);

    private static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00} h"
            : $"{t.Minutes}:{t.Seconds:00} min";
    }

    private static string FormatLap(uint ms) =>
        TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);

    private static string FormatTotal(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
}
