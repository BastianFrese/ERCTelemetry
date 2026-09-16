using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERCTelemetry.Core.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON. Load never throws: a missing,
/// corrupt or out-of-range file yields defaults (a broken settings.json must not stop the
/// app from launching). Path is injectable for tests. The secret fields (Twitch token,
/// ERC/LLM API keys) are persisted DPAPI-encrypted (CurrentUser) with a <c>dpapi:</c>
/// marker; legacy plaintext files load fine and are encrypted on the next save.</summary>
public static class AppSettingsStore
{
    /// <summary>Marker prefix for a DPAPI-encrypted secret. Anything without it is treated
    /// as legacy plaintext — it keeps working while loading, and the next save encrypts it.
    /// Public so the app can detect a legacy secret (for the one-time disk upgrade) without
    /// duplicating the magic string.</summary>
    public const string EncryptedPrefix = "dpapi:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>%LOCALAPPDATA%\ERCTelemetry\settings.json — the app's real settings file.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERCTelemetry", "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            if (loaded is null)
            {
                return new AppSettings();
            }

            return (loaded with
            {
                TwitchToken = DecryptSecret(loaded.TwitchToken),
                ErcApiKey = DecryptSecret(loaded.ErcApiKey),
                LlmApiKey = DecryptSecret(loaded.LlmApiKey),
            }).Sanitized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>True when the settings file on disk holds any DPAPI-able secret (Twitch token,
    /// ERC or LLM API key) that is NOT yet encrypted — a legacy plaintext value written before
    /// the L1 fix. Reads the raw disk values, not the decrypted in-memory ones (Load always
    /// returns plaintext, so the in-memory values cannot be used as the discriminator): after
    /// the first migration the file already carries the <c>dpapi:</c> marker on every stored
    /// secret and later launches skip the save. Never throws — a missing/unreadable file
    /// simply means nothing to migrate.</summary>
    public static bool NeedsSecretMigration(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return SecretOnDiskNeedsMigration(doc.RootElement, "twitchToken") ||
                   SecretOnDiskNeedsMigration(doc.RootElement, "ercApiKey") ||
                   SecretOnDiskNeedsMigration(doc.RootElement, "llmApiKey");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the named JSON property on disk holds a non-empty value that is not
    /// yet DPAPI-encrypted (a legacy plaintext secret).</summary>
    private static bool SecretOnDiskNeedsMigration(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var elem) || elem.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = elem.GetString();
        return !string.IsNullOrEmpty(raw) &&
               !raw.StartsWith(EncryptedPrefix, StringComparison.Ordinal);
    }

    /// <summary>Atomic save: writes to a temp file next to the target, then moves it over
    /// the real file. A crash mid-write can never leave a half-written settings.json behind
    /// that Load would have to fall back from. The Twitch token is encrypted before writing.</summary>
    public static void Save(AppSettings settings, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var forDisk = settings with
        {
            TwitchToken = EncryptSecret(settings.TwitchToken),
            ErcApiKey = EncryptSecret(settings.ErcApiKey),
            LlmApiKey = EncryptSecret(settings.LlmApiKey),
        };
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(forDisk.Sanitized(), JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>DPAPI-encrypts a secret (Twitch token, ERC/LLM API key) for the current
    /// Windows user. Anything that must not be encrypted — null/blank, already-encrypted, or
    /// a non-Windows machine — is returned unchanged (on non-Windows there is no DPAPI, so
    /// the secret stays plaintext).</summary>
    private static string? EncryptSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.StartsWith(EncryptedPrefix, StringComparison.Ordinal) ||
            !OperatingSystem.IsWindows())
        {
            return value;
        }

        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>Decrypts a DPAPI-encrypted secret. Returns non-encrypted values as-is
    /// (a legacy plaintext secret stays usable and is turned into an encrypted one on the next
    /// save). If the blob does not decrypt — different user, reimaged machine, tampered file —
    /// the value is dropped (null) rather than crashing the app.</summary>
    private static string? DecryptSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith(EncryptedPrefix, StringComparison.Ordinal) ||
            !OperatingSystem.IsWindows())
        {
            return value;
        }

        try
        {
            var bytes = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}