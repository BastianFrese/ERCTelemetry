using F1Game.UDP.Enums;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>JSON/CSV export round trips on temp files — verifies column order,
/// invariant-culture numbers and CSV escaping of ; " and newlines.</summary>
public sealed class ResultsExporterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"f1telemetry-export-{Guid.NewGuid():N}.out");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static readonly FinalResultRow Row = new(
        Position: 1,
        CarIndex: 3,
        Name: "Player;One \"Q\"",
        Team: Team.McLaren,
        RaceNumber: 81,
        NumLaps: 44,
        GridPosition: 2,
        Points: 25f,
        ResultStatus: ResultStatus.Finished,
        BestLapTimeMs: 90_123,
        TotalRaceTimeSeconds: 3600.5,
        PenaltiesTime: 0,
        NumPenalties: 0);

    [Fact]
    public void Writes_json_with_session_and_results()
    {
        var meta = new SessionMeta(
            42_000ul, SessionType.Race, Track.Spa, 44, 7004, true, 3,
            GameMode.OnlineCustom, Weather.LightRain, 27, 19, 0);

        ResultsExporter.WriteJson(_path, meta, [Row]);

        var json = File.ReadAllText(_path);
        Assert.Contains("\"uid\":42000", json);
        Assert.Contains("\"track\":\"Spa\"", json);
        Assert.Contains("\"resultStatus\":\"Finished\"", json);
    }

    [Fact]
    public void Writes_csv_with_header_and_escaped_values()
    {
        ResultsExporter.WriteCsv(_path, [Row]);

        var lines = File.ReadAllText(_path).Split('\n');
        Assert.StartsWith("position;name;team;", lines[0]);
        Assert.Contains("90123", lines[1]);
        // values with ; " or \n are quoted, embedded " doubled
        Assert.Contains("\"Player;One \"\"Q\"\"\"", lines[1]);
    }

    [Fact]
    public void Writes_laps_csv_with_header_and_formatted_time()
    {
        var lap = new LapCompleted(3, "Player", 7, 91_000, 33_110, 30_220,
            ActualCompound.F1C3, TyreAgeLaps: 4, Position: 2, ErsUsedJoules: 400_000);

        ResultsExporter.WriteLapsCsv(_path, [lap]);

        var lines = File.ReadAllText(_path).Split('\n');
        Assert.StartsWith("lap;lapTimeMs;lapTime;s1Ms;s2Ms;", lines[0]);
        Assert.Contains("7;91000;1:31.000;33110;30220;F1C3;4;2;400", lines[1]);
    }
}