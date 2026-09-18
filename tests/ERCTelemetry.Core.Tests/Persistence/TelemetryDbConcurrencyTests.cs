using System.Collections.Concurrent;
using F1Game.UDP.Enums;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Persistence;

/// <summary>Concurrent write + reads on one TelemetryDb instance. Readers must run on
/// pooled reader connections while the writer keeps the single writer connection, so a
/// History-tab read never shares — and races on — the connection the telemetry loop
/// writes through (HIGH, 2026-09-16). On the old shared single connection this threw
/// "command already in progress"-style errors under parallel load.</summary>
public sealed class TelemetryDbConcurrencyTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"f1telemetry-concurrency-{Guid.NewGuid():N}.db");

    private readonly TelemetryDb _db;

    public TelemetryDbConcurrencyTests()
    {
        _db = new TelemetryDb(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_path);
    }

    private static SessionMeta Meta => new(
        SessionUid: 77_001,
        SessionType: SessionType.Race,
        Track: Track.Monza,
        TotalLaps: 53,
        TrackLength: 5793,
        IsNetworkGame: true,
        PlayerCarIndex: 3,
        GameMode: GameMode.OnlineCustom,
        Weather: Weather.LightRain,
        TrackTemperature: 24,
        AirTemperature: 18,
        SessionTimeLeft: 0);

    [Fact]
    public async Task Concurrent_readers_and_writer_do_not_throw_and_commit_everything()
    {
        var id = _db.OpenSession(Meta);
        _db.FinalizeSession(id, "final classification",
        [
            new FinalResultRow(1, 3, "Player One", Team.Ferrari, 16, 53, 2, 25f,
                ResultStatus.Finished, 80_250, 5400.5, 0, 0),
            new FinalResultRow(5, 9, "Rival", Team.Mercedes, 44, 52, 4, 10f,
                ResultStatus.Finished, 80_900, 5403, 0, 0),
        ]);

        // LapNumber is a byte — keep the count at a race-plausible ≤255 so (byte)lap
        // cannot wrap: lap 257 aliases lap 1 and is dropped by INSERT OR IGNORE (the
        // real 400-lap count only landed 256 rows for exactly that reason).
        const int expectedLaps = 250;
        var failures = new ConcurrentQueue<Exception>();

        // One serialized writer (the app's telemetry loop is single-threaded): appends
        // lap after lap while the readers hammer the same instance from other threads.
        var writer = Task.Run(() =>
        {
            try
            {
                for (var lap = 1; lap <= expectedLaps; lap++)
                {
                    _db.AppendLap(id, new LapCompleted(3, "Player One", (byte)lap,
                        (uint)(80_000 + lap), 28_000, 27_000,
                        ActualCompound.F1C3, (byte)(lap % 6), Position: 2, ErsUsedJoules: 300_000f));
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });

        var readerTasks = Enumerable.Range(0, 8).Select(taskIndex => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    _ = _db.GetLaps(id);
                    _ = _db.GetResults(id);
                    _ = _db.GetEvents(id);
                    _ = _db.GetOvertakes(id);
                    _ = _db.GetSessionCards(new TelemetryDb.SessionCardFilter());
                    _ = _db.GetCareerStats();
                    _ = _db.GetChampionship();
                    _ = _db.GetTrackBestLapMs("Monza");
                    _ = _db.GetPlayerName();
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        }));

        await writer;
        await Task.WhenAll(readerTasks);

        Assert.Empty(failures); // no reader collided with the writer
        Assert.Equal(expectedLaps, _db.GetLaps(id).Count);
    }
}
