using System.Diagnostics;
using System.Windows;
using ERCTelemetry.Core.Discord;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Composition;

/// <summary>App-side host of the Discord login. Mirrors the Twitch login flow: the
/// ShareServer owns the OAuth handshake (code exchange with the client secret from its
/// config) — the app starts the attempt, opens the browser and polls the server for the
/// result. Unlike Twitch, the Discord access token is kept in RAM only (never persisted):
/// the user re-logs in after an app restart. The token feeds the website's setups API
/// (<c>GET /api/setups</c>, Bearer auth) and the key-owner check.</summary>
public sealed class DiscordLoginHost : IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly ShareTokenResolver _tokenResolver;
    private readonly object _lock = new();
    private string _status = "Nicht eingeloggt";
    private DiscordUser? _user;
    private string? _accessToken;

    /// <summary>How long the app polls the server for the login result before giving up.</summary>
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How often the app asks the server whether the login finished.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public DiscordLoginHost(AppSettingsService settings)
    {
        _settings = settings;
        // One resolver per host (shares its /api/config HttpClient + TTL cache across
        // repeated login attempts) — see TwitchChatHost for the rationale.
        _tokenResolver = new ShareTokenResolver(
            () => _settings.Current.ShareBaseUrl, () => _settings.Current.ShareToken);
    }

    /// <summary>Human-readable login status for the Settings/Setups tabs.</summary>
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

    /// <summary>The logged-in Discord user, null while logged out.</summary>
    public DiscordUser? User
    {
        get
        {
            lock (_lock)
            {
                return _user;
            }
        }
    }

    /// <summary>The Discord access token for the website's setups API, null while logged
    /// out. RAM only — never persisted.</summary>
    public string? AccessToken
    {
        get
        {
            lock (_lock)
            {
                return _accessToken;
            }
        }
    }

    /// <summary>Raised when <see cref="Status"/> changes (Settings/Setups tab refresh).</summary>
    public event Action? StatusChanged;

    /// <summary>Raised when the logged-in user or token changes (login/logout).</summary>
    public event Action? UserChanged;

    /// <summary>Runs the OAuth login flow via the ShareServer handshake: starts the attempt
    /// on the server, opens the browser to the authorize page, then polls the server until
    /// the callback finished the exchange. On success the token + user are kept in RAM.
    /// Returns a status message for the UI.</summary>
    public async Task<string> StartLoginAsync()
    {
        EnsureInstallId();
        using var client = new DiscordLoginClient(
            () => _settings.Current.ShareBaseUrl, _tokenResolver,
            installId: () => _settings.Current.InstallId,
            installSecret: () => _settings.Current.InstallSecret);
        try
        {
            var start = await StartWithPossessionSelfHealAsync(client).ConfigureAwait(false);
            // S2: the server may have onboarded this installation on first contact — persist
            // the issued possession secret so the status polls prove the same install.
            if (!string.IsNullOrWhiteSpace(start.InstallSecret))
            {
                _settings.Update(settings => settings with { InstallSecret = start.InstallSecret });
            }

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
                        lock (_lock)
                        {
                            _user = status.User;
                            _accessToken = status.Token;
                            _status = $"Eingeloggt als {status.User?.GlobalName ?? status.User?.Username ?? "?"}";
                        }

                        await client.DeleteAsync(start.State, start.Nonce, _stop.Token).ConfigureAwait(false);
                        RaiseLoginChanged();
                        return $"Login erfolgreich — eingeloggt als {status.User?.GlobalName ?? status.User?.Username}.";
                    case "error":
                        await client.DeleteAsync(start.State, start.Nonce, _stop.Token).ConfigureAwait(false);
                        App.Log($"Discord login failed: {status.Error}");
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
            App.Log($"Discord login failed: {ex.Message}");
            return $"Login fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>Runs the share-server login start, self-healing the S2 possession binding on
    /// a 403: the server then still holds a secret for our install id that this app no
    /// longer has (secret lost, first-contact response dropped, or a stale onboarding-race
    /// write). It refuses with Forbidden — so the app abandons the dead identity (fresh id
    /// + cleared secret in one atomic update) and retries once: an unknown id always
    /// re-onboards cleanly. The client feeds off the settings Funcs, so the retry already
    /// uses the new identity.</summary>
    private static bool IsPossessionRejection(Exception ex) =>
        ex is InvalidOperationException && ex.Message.Contains("Forbidden");

    private async Task<DiscordLoginStart> StartWithPossessionSelfHealAsync(DiscordLoginClient client)
    {
        try
        {
            return await client.StartAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPossessionRejection(ex))
        {
            App.Log($"Share login possession binding stale, refreshing install id: {ex.Message}");
            _settings.Update(settings => settings with
            {
                InstallId = Guid.NewGuid().ToString("N"),
                InstallSecret = null,
            });
            return await client.StartAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Generates (and persists) the per-install identity on first login so the
    /// share server can bind the OAuth flow to this installation (S2). Afterwards the
    /// settings Funcs feed each client with the id + the possession secret the server
    /// issued on first contact. Idempotent inside the atomic AppSettingsService.Update: a
    /// concurrent first login that already generated the id is left in place instead of
    /// being overwritten with a second fresh id.</summary>
    private void EnsureInstallId()
    {
        _settings.Update(settings =>
            string.IsNullOrWhiteSpace(settings.InstallId)
                ? settings with { InstallId = Guid.NewGuid().ToString("N") }
                : settings);
    }

    /// <summary>Clears the in-RAM token + user (the server's 15-minute expiry cleans up the
    /// attempt). The user must re-login to use the setups API again.</summary>
    public void Logout()
    {
        lock (_lock)
        {
            _user = null;
            _accessToken = null;
            _status = "Nicht eingeloggt";
        }

        RaiseLoginChanged();
    }

    /// <summary>Raises the Status/User events on the WPF dispatcher. <see cref="StartLoginAsync"/>
    /// polls with <c>ConfigureAwait(false)</c>, so its success continuation runs on the thread
    /// pool — and subscribers like SetupsViewModel mutate bound ObservableCollections that throw
    /// <c>NotSupportedException</c> off the UI thread. Logout is UI-thread-called and runs inline.</summary>
    private void RaiseLoginChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            StatusChanged?.Invoke();
            UserChanged?.Invoke();
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            StatusChanged?.Invoke();
            UserChanged?.Invoke();
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
