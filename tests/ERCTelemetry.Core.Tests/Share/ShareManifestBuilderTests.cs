using System.Globalization;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Share;
using F1Game.UDP.Enums;
using Xunit;

namespace ERCTelemetry.Core.Tests.Share;

/// <summary>ShareManifestBuilder maps the stored session facts to the share manifest;
/// clip file names are reduced to their plain file name.</summary>
public sealed class ShareManifestBuilderTests
{
    [Fact]
    public void Build_maps_session_results_and_clips()
    {
        var session = new TelemetryDb.SessionRow(
            1, 42_000, "Race", "Spa", 44, 7004, "GrandPrix", "Clear", true, 3,
            30, 25, "2026-09-07T18:30:00Z", 1, "final classification", 0, "F1", 80,
            0, 0, 2, null, "Standard", 3720);

        var results = new[]
        {
            new FinalResultRow(1, 3, "Player One", Team.McLaren, 81, 44, 2, 25f,
                ResultStatus.Finished, 90_123, 3600.5, 0, 0),
            new FinalResultRow(2, 4, "Rival", Team.RedBullRacing, 1, 44, 1, 18f,
                ResultStatus.Finished, 90_500, 3601, 0, 0),
        };

        var clips = new[]
        {
            new TelemetryDb.StoredClip(1,
                DateTimeOffset.Parse("2026-09-07T19:00:00Z", CultureInfo.InvariantCulture),
                @"C:\clips\42\clip-1.mp4", 7, 3, 5, "Player One", "Rival", 2, 12.5, 1_234_567),
        };

        var manifest = ShareManifestBuilder.Build(session, results, clips);

        Assert.Equal(42_000ul, manifest.SessionUid);
        Assert.Equal("Spa", manifest.Track);
        Assert.Equal("Race", manifest.SessionType);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-07T18:30:00Z", CultureInfo.InvariantCulture),
            manifest.StartUtc);
        Assert.Equal(3720, manifest.DurationSeconds);
        Assert.Equal(3, manifest.PlayerCarIndex);

        Assert.Equal(2, manifest.Results.Count);
        Assert.Equal(1, manifest.Results[0].Position);
        Assert.Equal("Player One", manifest.Results[0].Name);
        Assert.Equal("McLaren", manifest.Results[0].Team);
        Assert.Equal(44, manifest.Results[0].Laps);
        Assert.Equal(90_123u, manifest.Results[0].BestLapMs);
        Assert.Equal(3600.5, manifest.Results[0].TotalSeconds);
        Assert.Equal("Finished", manifest.Results[0].Status);
        Assert.Equal(25f, manifest.Results[0].Points);

        var clip = Assert.Single(manifest.Clips);
        Assert.Equal("clip-1.mp4", clip.FileName); // only the file name, not the full path
        Assert.Equal(7, clip.LapNumber);
        Assert.Equal(3, clip.CarIndex);
        Assert.Equal((byte?)5, clip.SecondCarIndex);
        Assert.Equal("Player One", clip.DriverName);
        Assert.Equal("Rival", clip.SecondDriverName);
        Assert.Equal(2, clip.Severity);
        Assert.Equal(12.5, clip.DurationSeconds);
    }
}
