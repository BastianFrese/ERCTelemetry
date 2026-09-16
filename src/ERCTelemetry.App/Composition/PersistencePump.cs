using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Share;
using F1Game.UDP.Enums;
using System.Threading.Channels;

namespace ERCTelemetry.App.Composition;

/// <summary>Background consumer of the store's event feed for persistence. Laps go in as
/// they complete; results/events/closing happen on SessionEnded. A session left open by a
/// crash is abandoned on the next OpenSession (startup) — its laps stay in the DB.</summary>
public sealed class PersistencePump : IDisposable
{
    private readonly TelemetryDb _db;
    private readonly SessionStateStore _store;
    private readonly ChannelReader<StoreEvent> _events;
    private readonly CancellationTokenSource _stop = new();
    private Task? _task;
    private long? _sessionId;
    private string _track = string.Empty;
    private byte _playerCarIndex;
    private SessionType _sessionType;
    private DateTimeOffset _sessionStartUtc;
    private readonly AppSettingsService? _ercSettings;
    private readonly ChannelWriter<ErcRaceEndedEvent> _ercEvents;
    private readonly Func<bool> _isReplaying;

    public PersistencePump(AppServices services, TelemetryDb db)
    {
        _db = db;
        _store = services.SessionStore;
        _events = services.Events.Reader;
        _ercSettings = services.Settings;
        _ercEvents = services.ErcRaceEnded.Writer;
        _isReplaying = () => services.IsReplaying;
        _task = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _events.WaitToReadAsync(ct))
            {
                while (_events.TryRead(out var storeEvent))
                {
                    try
                    {
                        Handle(storeEvent);
                    }
                    catch (Exception ex)
                    {
                        // A single failed SQLite write (locked db, disk full) must not kill
                        // the pump — later events of the session would never persist.
                        App.Log($"Persistence failed for {storeEvent.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private void Handle(StoreEvent storeEvent)
    {
        switch (storeEvent)
        {
            case SessionStarted s:
                _sessionId = _db.OpenSession(s.Meta);
                _track = s.Meta.Track.ToString();
                _playerCarIndex = s.Meta.PlayerCarIndex;
                _sessionType = s.Meta.SessionType;
                _sessionStartUtc = DateTimeOffset.UtcNow;
                _db.UpdateSessionExtras(s.Meta);
                // Seed the store's PB threshold from the all-time track bests so the
                // store can announce "personal-best" events during this session.
                _store.AllTimeBestLapMs = _db.GetTrackBestLapMs(_track);
                break;

            case DriversRegistered roster:
                if (_sessionId is { } rosterId)
                {
                    _db.UpsertDrivers(rosterId, roster.Drivers);
                }

                break;

            case LapHistoryUpdated history:
                if (_sessionId is { } historyId)
                {
                    _db.MergeLapHistory(historyId, history);
                }

                break;

            case SetupCaptured setup:
                if (_sessionId is { } setupId)
                {
                    _db.UpsertSetup(setupId, setup.CarIndex, setup.Setup);
                }

                break;

            case LapPositionsChunk positions:
                if (_sessionId is { } positionsId)
                {
                    _db.PutLapPositions(positionsId, positions);
                }

                break;

            case CarDamageChanged damage:
                if (_sessionId is { } damageId)
                {
                    _db.AppendDamageLog(damageId, damage);
                }

                break;

            case LapMotionSummary motion:
                if (_sessionId is { } motionId)
                {
                    _db.AppendMotionSummary(motionId, motion);
                }

                break;

            case LapCompleted lap:
                if (_sessionId is { } id)
                {
                    _db.AppendLap(id, lap);
                    UpsertTrackBest(lap);
                }

                break;

            case LapTraced traced:
                if (_sessionId is { } traceId)
                {
                    _db.AppendLapTrace(traceId, traced.CarIndex, traced.Trace);
                }

                break;

            case Overtake o:
                if (_sessionId is { } overtakeId)
                {
                    _db.AppendOvertake(overtakeId, o);
                }

                break;

            case RaceControl rc:
                if (_sessionId is { } eventId)
                {
                    _db.AppendEvent(eventId, rc.Event);
                }

                break;

            case ClipSaved clip:
                // The clip event carries the session uid, not the row id — resolve it.
                if (_db.GetSessionIdByUid(clip.SessionUid) is { } clipSessionId)
                {
                    _db.AppendClip(clipSessionId, clip);
                }

                break;

            case SessionEnded ended:
                if (_sessionId is { } endedId)
                {
                    _db.FinalizeSession(endedId, ended.Reason, ended.Results);
                    _db.FinalizeSessionExtras(endedId, ended.Results);
                    TryPublishErcEnd(ended);
                    _sessionId = null;
                }

                break;
        }
    }

    /// <summary>Persists an all-time track best when the player completed a lap faster
    /// than anything recorded for this track. The store fires its "personal-best" event
    /// off the DB-seeded threshold, so both sides stay consistent.</summary>
    private void UpsertTrackBest(LapCompleted lap)
    {
        if (_track.Length == 0 || lap.CarIndex != _playerCarIndex || lap.LapTimeMs == 0)
        {
            return;
        }

        var best = _db.GetTrackBestLapMs(_track);
        if (best == 0 || lap.LapTimeMs < best)
        {
            _db.UpsertTrackBestLap(_track, (uint)lap.LapTimeMs);
        }
    }

    /// <summary>Announces a finished league race to the ERC prompt feed so the UI can ask
    /// „Ergebnis an erdi-erc.de senden?". Only the FinalClassification of a LIVE race session
    /// (not a replay) with a configured ERC key triggers — idles/replays/free-practice must
    /// never prompt. Names come from the store snapshot (rename overrides applied) with the
    /// raw game name as fallback; a DNF row is reported as position 0 (the parser's semantics).</summary>
    private void TryPublishErcEnd(SessionEnded ended)
    {
        if (ended.Reason != "FinalClassification"
            || _sessionType != SessionType.Race
            || _isReplaying()
            || string.IsNullOrWhiteSpace(_ercSettings?.Current.ErcApiKey))
        {
            return;
        }

        var drivers = _store.BuildSnapshot().Drivers
            .ToDictionary(d => d.CarIndex, d => d.Name);

        var finishes = new List<ErcRaceFinish>(ended.Results.Count);
        long? fastestMs = null;
        byte? fastestCarIndex = null;
        foreach (var row in ended.Results)
        {
            var dnf = row.ResultStatus != ResultStatus.Finished;
            finishes.Add(new ErcRaceFinish(
                Position: dnf ? 0 : row.Position,
                Driver: drivers.TryGetValue(row.CarIndex, out var name) ? name : row.Name,
                RaceTimeMs: row.TotalRaceTimeSeconds > 0 ? (long)Math.Round(row.TotalRaceTimeSeconds * 1000) : null,
                QualifyingPosition: row.GridPosition > 0 ? row.GridPosition : null,
                Dnf: dnf));

            if (row.BestLapTimeMs > 0 && (fastestMs is null || row.BestLapTimeMs < fastestMs))
            {
                fastestMs = row.BestLapTimeMs;
                fastestCarIndex = row.CarIndex;
            }
        }

        string? fastestLap = fastestCarIndex is null
            ? null
            : drivers.TryGetValue(fastestCarIndex.Value, out var fastestName) ? fastestName : null;

        _ercEvents.TryWrite(new ErcRaceEndedEvent(_track, _sessionStartUtc, fastestLap, finishes));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Pump stopped mid-write; SQLite is crash-safe (WAL).
        }

        _stop.Dispose();
    }
}