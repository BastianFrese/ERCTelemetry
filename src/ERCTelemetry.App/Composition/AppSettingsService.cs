using System.IO;
using System.Security.Cryptography;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.App.Composition;

/// <summary>Holds the effective <see cref="AppSettings"/> for the running app and persists
/// every update to %LOCALAPPDATA%\ERCTelemetry\settings.json. Created once in MainWindow's
/// constructor — before any consumer (theme, listener, overlay, tray) reads settings.</summary>
public sealed class AppSettingsService
{
    private readonly string _path;

    public AppSettingsService(string? path = null)
    {
        _path = path ?? AppSettingsStore.DefaultPath;
        Current = AppSettingsStore.Load(_path);
        // L1 one-time disk upgrade: secrets an older version wrote as plaintext (Twitch
        // token, ERC/LLM API keys) are encrypted on first load so this run never leaves them
        // lingering at rest. The check reads the raw disk values — after the first migration
        // the file already carries the dpapi: marker on every stored secret and later
        // launches skip the save. (The in-memory values are always the decrypted plaintext,
        // so they cannot be used as the discriminator.)
        if (OperatingSystem.IsWindows() && AppSettingsStore.NeedsSecretMigration(_path))
        {
            try
            {
                AppSettingsStore.Save(Current, _path);
            }
            catch
            {
                // Best effort: a failing disk write must never block app startup. The next
                // Update() will retry the write (and surface the error in the Settings tab).
            }
        }
    }

    public AppSettings Current { get; private set; }

    /// <summary>Non-null when the last <see cref="Update"/> could not persist to disk
    /// (the in-memory change still applies for this run). The Settings tab surfaces it.</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>Applies an immutable change and persists it immediately. A failing disk
    /// write must not throw into the UI: the change stays active in memory and the error
    /// is recorded for the caller to show.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        try
        {
            AppSettingsStore.Save(Current, _path);
            LastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or CryptographicException)
        {
            LastSaveError = $"settings.json could not be saved to {_path}: {ex.Message}";
        }

        return Current;
    }
}