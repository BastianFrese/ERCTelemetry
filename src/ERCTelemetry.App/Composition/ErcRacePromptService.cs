using System.Threading.Channels;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Composition;

/// <summary>A league race ended and can be sent to the ERC admin-review inbox. Raised by
/// <see cref="ErcRacePromptService"/> on its background reader; the UI thread marshals
/// to the send dialog via the Dispatcher.</summary>
public sealed record ErcRaceEndedEvent(
    string Track,
    DateTimeOffset StartedUtc,
    string? FastestLap,
    IReadOnlyList<ErcRaceFinish> Finishes);

/// <summary>Reads the persistence pump's "league race ended" feed and raises
/// <see cref="RaceEnded"/> for the UI (per the competing-consumer rule: the pump writes
/// the store events, this service is the only reader of <see cref="AppServices.ErcRaceEnded"/>).</summary>
public sealed class ErcRacePromptService : IDisposable
{
    private readonly ChannelReader<ErcRaceEndedEvent> _feed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;

    /// <summary>Raised on the service's background thread — the handler must marshal to
    /// the UI thread (Dispatcher) before touching the window.</summary>
    public event Action<ErcRaceEndedEvent>? RaceEnded;

    public ErcRacePromptService(AppServices services)
    {
        _feed = services.ErcRaceEnded.Reader;
        _task = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _feed.WaitToReadAsync(ct))
            {
                while (_feed.TryRead(out var raceEnded))
                {
                    try
                    {
                        RaceEnded?.Invoke(raceEnded);
                    }
                    catch (Exception ex)
                    {
                        // A broken prompt handler must not kill the feed.
                        App.Log($"ERC race prompt failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Feed stopped mid-delivery.
        }

        _stop.Dispose();
    }
}
