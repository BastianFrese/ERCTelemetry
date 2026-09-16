using System.Globalization;
using System.Text;
using System.Text.Json;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Persistence;

/// <summary>Writes end-of-session results as JSON and standings as CSV. Pure functions
/// over rows — the history tab calls these on Export button clicks.</summary>
public static class ResultsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void WriteJson(string path, SessionMeta? meta,
        IReadOnlyList<FinalResultRow> results)
    {
        var payload = new
        {
            session = meta is null
                ? null
                : new
                {
                    uid = meta.SessionUid,
                    type = meta.SessionType.ToString(),
                    track = meta.Track.ToString(),
                    totalLaps = meta.TotalLaps,
                    isNetworkGame = meta.IsNetworkGame,
                },
            results = results.Select(r => new
            {
                position = r.Position,
                name = r.Name,
                team = r.Team.Display(),
                raceNumber = r.RaceNumber,
                numLaps = r.NumLaps,
                gridPosition = r.GridPosition,
                points = r.Points,
                resultStatus = r.ResultStatus.ToString(),
                bestLapTimeMs = r.BestLapTimeMs,
                totalRaceTimeSeconds = r.TotalRaceTimeSeconds,
                penaltiesTime = r.PenaltiesTime,
                numPenalties = r.NumPenalties,
            }),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void WriteCsv(string path, IReadOnlyList<FinalResultRow> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("position;name;team;raceNumber;numLaps;gridPosition;points;resultStatus;bestLapMs;totalRaceSeconds;penaltiesTime;numPenalties");
        foreach (var r in results)
        {
            sb.Append(r.Position).Append(';')
              .Append(EscapeCsv(r.Name)).Append(';')
              .Append(r.Team.Display()).Append(';')
              .Append(r.RaceNumber).Append(';')
              .Append(r.NumLaps).Append(';')
              .Append(r.GridPosition).Append(';')
              .Append(r.Points.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(r.ResultStatus.ToString()).Append(';')
              .Append(r.BestLapTimeMs).Append(';')
              .Append(r.TotalRaceTimeSeconds.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(r.PenaltiesTime).Append(';')
              .Append(r.NumPenalties)
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Writes one driver's laps as CSV (raw ms + formatted lap time, the same
    /// columns the history lap table shows).</summary>
    public static void WriteLapsCsv(string path, IReadOnlyList<LapCompleted> laps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("lap;lapTimeMs;lapTime;s1Ms;s2Ms;tyre;tyreAgeLaps;position;ersUsedKj");
        foreach (var l in laps)
        {
            sb.Append(l.LapNumber).Append(';')
              .Append(l.LapTimeMs).Append(';')
              .Append(FormatLap(l.LapTimeMs)).Append(';')
              .Append(l.Sector1TimeMs).Append(';')
              .Append(l.Sector2TimeMs).Append(';')
              .Append(l.TyreCompound.ToString()).Append(';')
              .Append(l.TyreAgeLaps).Append(';')
              .Append(l.Position).Append(';')
              .Append(l.ErsUsedJoules / 1000.0)
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string FormatLap(uint ms) => ms <= 0
        ? string.Empty
        : $"{ms / 60000:D}:{ms % 60000 / 1000:D2}.{ms % 1000:D3}";

    private static string EscapeCsv(string value) =>
        value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
}