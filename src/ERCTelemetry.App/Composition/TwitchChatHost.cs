using System.Diagnostics;
using System.Threading.Channels;
using ERCTelemetry.Core.Session;
using ERCTelemetry.Core.Share;
using ERCTelemetry.Core.TwitchChat;

namespace ERCTelemetry.App.Composition;

/// <summary>App-side host of the Twitch chat bot. Owns the Core <see cref="TwitchIrcClient"/>
/// + <see cref="TwitchChatService"/>, keeps the latest snapshot from its own
/// <see cref="AppServices.TwitchSnapshots"/> channel and connects/disconnects as the
/// settings change. Also runs the OAuth login flow: the ShareServer owns the handshake
/// (PKCE verifier, code exchange, user lookup) — the app starts the attempt, opens the
/// browser and polls the server for the result. No localhost callback port involved.</summary>
public sealed class TwitchChatHost : IDisposable
{
    private readonly AppServices _services;
    private readonly AppSettingsService _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly ShareTokenResolver _tokenResolver;
    private readonly object _lock = new();
    private TwitchIrcClient? _client;
    private TelemetrySnapshot? _latest;
    private Task? _snapshotTask;
    private string _status = "Aus";

    /// <summary>How long the app polls the server for the login result before giving up.</summary>
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How often the app asks the server whether the login finished.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public TwitchChatHost(AppServices services, AppSettingsService settings)
    {
        _services = services;
        _settings = settings;
        // One resolver per host, not per login attempt: the resolver owns the HttpClient
        // for /api/config and the fetched-token TTL cache, so repeated login attempts
        // share one cache instead of spinning up (and leaking) a fresh client each time.
        _tokenResolver = new ShareTokenResolver(
            () => _settings.Current.ShareBaseUrl, () => _settings.Current.ShareToken);
        _snapshotTask = Task.Run(() => SnapshotLoopAsync(_stop.Token));
        ApplySettings();
    }

    /// <summary>Human-readable connection status for the Settings tab.</summary>
    public string Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised when <see cref="Status"/> changes (Settings tab refresh).</summary>
    public event Action? StatusChanged;

    /// <summary>Re-reads the settings and connects/disconnects accordingly. Called on every
    /// settings save so a toggle or token change applies live.</summary>
    public void ApplySettings()
    {
        var s = _settings.Current;
        if (!s.TwitchEnabled || string.IsNullOrWhiteSpace(s.TwitchToken) || string.IsNullOrWhiteSpace(s.TwitchChannel))
        {
            Disconnect();
            SetStatus("Aus");
            return;
        }

        _ = ConnectAsync(s.TwitchChannel, s.TwitchToken);
    }

    /// <summary>Runs the OAuth login flow via the ShareServer handshake: starts the attempt
    /// on the server (it generates the PKCE verifier + state), opens the browser to the
    /// authorize page, then polls the server until the callback finished the exchange.
    /// On success the token + channel are saved and the bot connects. Returns a status
    /// message for the Settings tab.</summary>
    public async Task<string> StartLoginAsync()
    {
        using var client = new TwitchLoginClient(() => _settings.Current.ShareBaseUrl, _tokenResolver);
        try
        {
            var start = await client.StartAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                Process.Start(new ProcessStartInfo(start.AuthorizeUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                return $"Browser konnte nicht geöffnet werden: {ex.Message}";
            }

            var deadline = DateTime.UtcNow + LoginTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, _stop.Token).ConfigureAwait(false);
                var status = await client.GetStatusAsync(start.State, start.Nonce, _stop.Token).ConfigureAwait(false);
                switch (status.Status)
                {
                    case "success":
                        _settings.Update(settings => settings with
                        {
                            TwitchToken = status.Token,
                            TwitchChannel = status.User?.Login,
                            TwitchEnabled = true,
                        });
                        ApplySettings();
                        await client.DeleteAsync(start.State, start.Nonce, _stop.Token).ConfigureAwait(false);
                        return $"Login erfolgreich — verbunden als {status.User?.DisplayName} (Kanal #{status.User?.Login}).";
                    case "error":
                        await client.DeleteAsync(start.State, start.Nonce, _stop.Token).ConfigureAwait(false);
                        App.Log($"Twitch login failed: {status.Error}");
                        // The server stores a whitelisted message ("access_denied" or a
                        // fixed "Login fehlgeschlagen"), never raw provider input — so no
                        // double prefix here.
                        return status.Error == "access_denied"
                            ? "Login abgebrochen (Zugriff verweigert)."
                            : "Login fehlgeschlagen.";
                    case "notfound":
                        return "Login abgelaufen — bitte erneut versuchen.";
                }
            }

            await client.DeleteAsync(start.State, start.Nonce, _stop.Token).ConfigureAwait(false);
            return "Login abgelaufen — bitte erneut versuchen.";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return "Login abgebrochen.";
        }
        catch (Exception ex)
        {
            App.Log($"Twitch login failed: {ex.Message}");
            return $"Login fehlgeschlagen: {ex.Message}";
        }
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _services.TwitchSnapshots.Reader.WaitToReadAsync(ct))
            {
                while (_services.TwitchSnapshots.Reader.TryRead(out var snapshot))
                {
                    _latest = snapshot; // only the freshest matters for chat answers
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ConnectAsync(string channel, string token)
    {
        Disconnect();

        // NICK = channel: the token must belong to the channel account (the common
        // personal-bot case — the bot joins the streamer's own channel and posts as it).
        var client = new TwitchIrcClient(channel);
        client.AuthenticationFailed += () =>
        {
            // Runs on the read-loop thread, which DisconnectAsync awaits — so the
            // disconnect must not run inline. Hop to the thread pool instead.
            _ = Task.Run(() =>
            {
                App.Log("Twitch-Login vom Server abgelehnt (Token abgelaufen oder ungültig).");
                lock (_lock)
                {
                    if (_client != client)
                    {
                        return; // a newer connect replaced this client — don't kill it
                    }
                }

                Disconnect();
                SetStatus("Twitch-Token abgelaufen oder ungültig — bitte neu einloggen.");
            });
        };
        var chat = new TwitchChatService(client, () => _latest);
        lock (_lock)
        {
            _client = client;
        }

        SetStatus($"Verbinde mit #{channel}…");
        try
        {
            await client.ConnectAsync(channel, token, _stop.Token).ConfigureAwait(false);
            SetStatus($"Verbunden mit #{channel}");
        }
        catch (Exception ex)
        {
            SetStatus($"Fehler: {ex.Message}");
        }
    }

    private void Disconnect()
    {
        TwitchIrcClient? client;
        lock (_lock)
        {
            client = _client;
            _client = null;
        }

        if (client is not null)
        {
            try
            {
                client.DisconnectAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Socket already gone.
            }

            client.Dispose();
        }
    }

    private void SetStatus(string status)
    {
        lock (_lock)
        {
            _status = status;
        }

        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        Disconnect();
        try
        {
            _snapshotTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown mid-read.
        }

        _stop.Dispose();
    }
}
