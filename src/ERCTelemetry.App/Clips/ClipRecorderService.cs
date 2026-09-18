using System.IO;
using System.Threading.Channels;
using ERCTelemetry.App.Composition;
using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.App.Clips;

/// <summary>Consumes the clip event feed, filters collisions involving the player and
/// dispatches the screen-capture save. Tracks the session gate (network session active)
/// and applies a 5s cooldown so flashback/replay re-sends don't spam clips. Driver names
/// come from the DriversRegistered events (resolved names, overrides included) — the store
/// snapshot is single-writer and must not be read from a second thread.</summary>
public sealed class ClipRecorderService : IDisposable
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    private readonly AppServices _services;
    private readonly ScreenCaptureService _capture;
    private readonly ChannelReader<StoreEvent> _events;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _task;
    private ulong _sessionUid;
    private byte _playerCarIndex;
    private bool _sessionActive;
    private bool _isNetworkGame;
    private DateTimeOffset _lastClipAt;
    private IReadOnlyList<DriverEntry> _drivers = Array.Empty<DriverEntry>();
    private long _inFlightSaves; // Interlocked count of active SaveAsync calls

    public ClipRecorderService(AppServices services, ScreenCaptureService capture)
    {
        _services = services;
        _capture = capture;
        _events = services.ClipEvents.Reader;
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
                        // A single bad event must not kill the recorder — later collisions
                        // of the session would never become clips.
                        App.Log($"Clip recorder failed: {ex.Message}");
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
                _sessionUid = s.Meta.SessionUid;
                _playerCarIndex = s.Meta.PlayerCarIndex;
                _isNetworkGame = s.Meta.IsNetworkGame;
                _sessionActive = true;
                _capture.SetSessionActive(_isNetworkGame);
                break;

            case SessionEnded:
                _sessionActive = false;
                _capture.SetSessionActive(false);
                break;

            case DriversRegistered roster:
                _drivers = roster.Drivers;
                break;

            case RaceControl rc when rc.Event.Type == "Collision":
                MaybeRecord(rc.Event);
                break;
        }
    }

    private void MaybeRecord(RaceEventEntry entry)
    {
        if (!_sessionActive || !_isNetworkGame)
        {
            return;
        }

        var settings = _services.Settings?.Current.Clips ?? new ClipSettings();
        if (!settings.Enabled ||
            !CollisionClipDetector.ShouldRecord(entry, _playerCarIndex, settings))
        {
            return;
        }

        // Flashback/replay re-sends the same collision — one clip per 5s window.
        if (entry.Utc - _lastClipAt < Cooldown)
        {
            return;
        }

        _lastClipAt = entry.Utc;

        var metadata = new ClipMetadata(
            _sessionUid,
            entry.Utc,
            entry.LapNumber,
            entry.CarIndex ?? 0,
            entry.SecondCarIndex,
            Name(entry.CarIndex),
            entry.SecondCarIndex is { } second ? Name(second) : null,
            entry.DetailValue);

        _ = SaveAsync(metadata);
    }

    private async Task SaveAsync(ClipMetadata metadata)
    {
        // In-flight zählen, damit Dispose auf einen laufenden Encode wartet (bounded) statt
        // ihn verwaist weiterlaufen zu lassen.
        Interlocked.Increment(ref _inFlightSaves);
        try
        {
            try
            {
                await _saveGate.WaitAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return; // shutdown while queued behind another save — nothing to record
            }

            try
            {
                var result = await _capture.SaveClipAsync(metadata, _stop.Token);
                if (result is null)
                {
                    return;
                }

                var clip = new ClipSaved(
                    metadata.SessionUid, metadata.Utc, result.Path, metadata.LapNumber,
                    metadata.CarIndex, metadata.SecondCarIndex, metadata.DriverName,
                    metadata.SecondDriverName, metadata.Severity,
                    result.DurationSeconds, new FileInfo(result.Path).Length);
                await _services.Events.Writer.WriteAsync(clip, _stop.Token);
            }
            catch (Exception ex)
            {
                App.Log($"Clip save failed: {ex.Message}");
            }
            finally
            {
                _saveGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightSaves);
        }
    }

    private string Name(byte? carIndex)
    {
        if (carIndex is not { } index)
        {
            return "?";
        }

        return index < _drivers.Count ? _drivers[index].Name : $"Car {index + 1}";
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
        }

        // Ein zur Shutdown-Zeit laufender Encode ist durch _stop gekillt; hier bounded
        // auf sein Ende warten, damit kein halbes MP4 ohne DB-Eintrag übrig bleibt. Länger
        // als ~5 s hängt kein Save mehr am Gate (der Encode-Kill im Capture ist 2 s).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (Interlocked.Read(ref _inFlightSaves) > 0 && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        _stop.Dispose();
        // _saveGate is deliberately NOT disposed: a long-running clip encode (up to the
        // encode timeout) can still be inside SaveAsync's finally when Dispose runs and
        // then Release() the already-disposed gate. App-lifetime gate; GC cleans up.
    }
}
