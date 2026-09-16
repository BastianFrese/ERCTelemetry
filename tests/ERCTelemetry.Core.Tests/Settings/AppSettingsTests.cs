using ERCTelemetry.Core.Clips;
using ERCTelemetry.Core.Settings;
using ERCTelemetry.Core.Telemetry;
using Xunit;

namespace ERCTelemetry.Core.Tests.Settings;

/// <summary>AppSettings JSON persistence: round trip on a temp file, defaults for
/// missing/corrupt files, sanitization of hand-edited junk.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"f1telemetry-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var settings = AppSettingsStore.Load(_path);

        Assert.Equal(20777, settings.UdpPort);
        Assert.Equal(8090, settings.OverlayPort);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Null(settings.RivalDriverName);
        Assert.False(settings.CloseToTray);
        Assert.True(settings.ShowNotifications);
        Assert.True(settings.AutoStartListening);
        // Overlay visibility blocks default to shown.
        Assert.True(settings.OverlayH2hHeader);
        Assert.True(settings.OverlayH2hLaps);
        Assert.True(settings.OverlayH2hSectors);
        Assert.True(settings.OverlayH2hTyres);
        Assert.True(settings.OverlayH2hPits);
        Assert.True(settings.OverlayH2hTally);
        Assert.True(settings.OverlayComSession);
        Assert.True(settings.OverlayComBattles);
        Assert.True(settings.OverlayComMovers);
        Assert.True(settings.OverlayComFastest);
        Assert.True(settings.OverlayComPits);
        Assert.True(settings.OverlayComTyres);
        Assert.True(settings.OverlayComFlags);
        Assert.True(settings.OverlayComWeather);
    }

    [Fact]
    public void Save_then_load_round_trips_all_fields()
    {
        // Arrange
        var settings = new AppSettings(
            udpPort: 20800,
            overlayPort: 8123,
            theme: AppTheme.Dark,
            rivalDriverName: "L. HAMILTON",
            closeToTray: true,
            showNotifications: false,
            autoStartListening: false);

        // Act
        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        // Assert
        Assert.Equal(settings, loaded);
    }

    [Fact]
    public void Save_then_load_round_trips_new_fields()
    {
        // Arrange
        var settings = new AppSettings(
            udpPort: 20800,
            overlayPort: 8123,
            theme: AppTheme.Dark,
            rivalDriverName: "L. HAMILTON",
            closeToTray: true,
            showNotifications: false,
            autoStartListening: false,
            colorScheme: OverlayColorScheme.German,
            hudShowRival: false,
            hudRivalCycleSeconds: 45,
            hudShowMap: true,
            hudShowWeather: false,
            driverNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Car 22"] = "Basti",
            },
            overlayH2hHeader: false,
            overlayH2hLaps: true,
            overlayH2hSectors: false,
            overlayH2hTyres: true,
            overlayH2hPits: false,
            overlayH2hTally: true,
            overlayComSession: false,
            overlayComBattles: true,
            overlayComMovers: false,
            overlayComFastest: true,
            overlayComPits: false,
            overlayComTyres: true,
            overlayComFlags: false,
            overlayComWeather: true);

        // Act
        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        // Assert — dictionaries are compared by value, records otherwise by field
        // (record equality compares the map property by reference).
        Assert.Equal(settings with { DriverNameOverrides = null }, loaded with { DriverNameOverrides = null });
        Assert.Equal(OverlayColorScheme.German, loaded.ColorScheme);
        Assert.False(loaded.HudShowRival);
        Assert.Equal(45, loaded.HudRivalCycleSeconds);
        Assert.True(loaded.HudShowMap);
        Assert.False(loaded.HudShowWeather);
        Assert.NotNull(loaded.DriverNameOverrides);
        Assert.Single(loaded.DriverNameOverrides!);
        Assert.Equal("Basti", loaded.DriverNameOverrides["Car 22"]);
        // Overlay visibility blocks round-trip (alternating pattern catches swapped pairs).
        Assert.False(loaded.OverlayH2hHeader);
        Assert.True(loaded.OverlayH2hLaps);
        Assert.False(loaded.OverlayH2hSectors);
        Assert.True(loaded.OverlayH2hTyres);
        Assert.False(loaded.OverlayH2hPits);
        Assert.True(loaded.OverlayH2hTally);
        Assert.False(loaded.OverlayComSession);
        Assert.True(loaded.OverlayComBattles);
        Assert.False(loaded.OverlayComMovers);
        Assert.True(loaded.OverlayComFastest);
        Assert.False(loaded.OverlayComPits);
        Assert.True(loaded.OverlayComTyres);
        Assert.False(loaded.OverlayComFlags);
        Assert.True(loaded.OverlayComWeather);
    }

    [Fact]
    public void Load_corrupt_file_returns_defaults()
    {
        File.WriteAllText(_path, "{ this is not json }");

        var settings = AppSettingsStore.Load(_path);

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(20777, settings.UdpPort);
    }

    [Fact]
    public void Load_sanitizes_out_of_range_values()
    {
        File.WriteAllText(
            _path,
            """{"udpPort": 99999, "overlayPort": -1, "theme": "plaid"}""");

        var settings = AppSettingsStore.Load(_path);

        Assert.Equal(20777, settings.UdpPort);
        Assert.Equal(8090, settings.OverlayPort);
        Assert.Equal(AppTheme.System, settings.Theme);
        // Flags not present in the file keep their defaults (shown = true / autoStart = true).
        Assert.True(settings.ShowNotifications);
        Assert.True(settings.AutoStartListening);
    }

    [Fact]
    public void Load_sanitizes_new_field_junk()
    {
        File.WriteAllText(
            _path,
            """{"udpPort": 20800, "colorScheme": "plaid", "hudRivalCycleSeconds": 0, "hudLayout": "compact", "hudLeft": -1}""");

        var settings = AppSettingsStore.Load(_path);

        Assert.Equal(OverlayColorScheme.Classic, settings.ColorScheme);
        Assert.Equal(30, settings.HudRivalCycleSeconds);
        Assert.Null(settings.DriverNameOverrides);
    }

    [Fact]
    public void Load_drops_empty_and_oversized_name_overrides()
    {
        var longName = new string('x', 50);
        var json = "{\"udpPort\": 20800, \"driverNameOverrides\": {\"Car 22\": \"  Basti  \", \"  \": \"x\", " +
                   $"\"Car 5\": \"\", \"Car 9\": \"{longName}\", \"Car 3\": \"Ana\"" + "}}";
        File.WriteAllText(_path, json);

        var settings = AppSettingsStore.Load(_path);

        Assert.NotNull(settings.DriverNameOverrides);
        Assert.Equal(2, settings.DriverNameOverrides!.Count);
        Assert.Equal("Basti", settings.DriverNameOverrides["Car 22"]);
        Assert.Equal("Ana", settings.DriverNameOverrides["Car 3"]);
        Assert.False(settings.DriverNameOverrides.ContainsKey("Car 5"));
    }

    [Fact]
    public void Load_drops_junk_widget_positions_and_unknown_widgets()
    {
        File.WriteAllText(
            _path,
            """
            {"udpPort": 20800, "hudWidgetPositions": {
              "timing": [120.5, 340.0],
              "drive": [-20, 10],
              "status": [1e400, 12],
              "map": [10],
              "rival": [50, 60, 70],
              "notAWidget": [1, 2]
            }}
            """);

        var settings = AppSettingsStore.Load(_path);

        Assert.NotNull(settings.HudWidgetPositions);
        var positions = settings.HudWidgetPositions!;
        Assert.Single(positions); // only the valid timing entry survives
        Assert.Equal(120.5, positions["timing"][0]);
        Assert.Equal(340.0, positions["timing"][1]);
    }

    [Fact]
    public void Save_then_load_round_trips_widget_positions()
    {
        // Arrange
        var positions = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["timing"] = [110.0, 24.0],
            ["battles"] = [32.0, 420.5],
        };
        var settings = new AppSettings() with { HudWidgetPositions = positions };

        // Act
        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        // Assert
        Assert.NotNull(loaded.HudWidgetPositions);
        Assert.Equal(2, loaded.HudWidgetPositions!.Count);
        Assert.Equal(110.0, loaded.HudWidgetPositions["timing"][0]);
        Assert.Equal(420.5, loaded.HudWidgetPositions["battles"][1]);
    }

    [Fact]
    public void Load_missing_clips_keeps_defaults()
    {
        var settings = AppSettingsStore.Load(_path);

        Assert.False(settings.Clips.Enabled);
        Assert.True(settings.Clips.OnlyPlayerCollisions);
        Assert.Equal(1, settings.Clips.MinSeverity);
        Assert.Equal(10, settings.Clips.PreRollSeconds);
        Assert.Equal(10, settings.Clips.PostRollSeconds);
        Assert.Equal(60, settings.Clips.Fps);
        Assert.Equal(1280, settings.Clips.MaxWidth);
    }

    [Fact]
    public void Save_then_load_round_trips_clip_settings()
    {
        // Arrange
        var settings = new AppSettings() with
        {
            Clips = new ClipSettings(
                Enabled: true,
                OnlyPlayerCollisions: false,
                MinSeverity: 2,
                PreRollSeconds: 15,
                PostRollSeconds: 5,
                Fps: 12,
                MaxWidth: 2560),
        };

        // Act
        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        // Assert
        Assert.True(loaded.Clips.Enabled);
        Assert.False(loaded.Clips.OnlyPlayerCollisions);
        Assert.Equal(2, loaded.Clips.MinSeverity);
        Assert.Equal(15, loaded.Clips.PreRollSeconds);
        Assert.Equal(5, loaded.Clips.PostRollSeconds);
        Assert.Equal(12, loaded.Clips.Fps);
        Assert.Equal(2560, loaded.Clips.MaxWidth);
    }

    [Fact]
    public void Load_sanitizes_out_of_range_clip_values()
    {
        File.WriteAllText(
            _path,
            """{"udpPort": 20800, "clips": {"enabled": true, "minSeverity": 9, "preRollSeconds": 0, "postRollSeconds": 200, "fps": 0, "maxWidth": 99999}}""");

        var settings = AppSettingsStore.Load(_path);

        Assert.True(settings.Clips.Enabled);
        Assert.Equal(2, settings.Clips.MinSeverity);
        Assert.Equal(1, settings.Clips.PreRollSeconds);
        Assert.Equal(60, settings.Clips.PostRollSeconds);
        Assert.Equal(1, settings.Clips.Fps);
        Assert.Equal(7680, settings.Clips.MaxWidth);
    }

    [Fact]
    public void Load_missing_share_settings_keeps_defaults()
    {
        var settings = AppSettingsStore.Load(_path);

        Assert.Null(settings.ShareBaseUrl);
        Assert.Null(settings.ShareToken);
    }

    [Fact]
    public void Save_then_load_round_trips_share_settings()
    {
        // Arrange
        var settings = new AppSettings() with
        {
            ShareBaseUrl = "https://telemetrie.erdi-erc.de",
            ShareToken = "geheim-token",
        };

        // Act
        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        // Assert
        Assert.Equal("https://telemetrie.erdi-erc.de", loaded.ShareBaseUrl);
        Assert.Equal("geheim-token", loaded.ShareToken);
    }

    [Fact]
    public void Load_sanitizes_whitespace_share_token()
    {
        File.WriteAllText(
            _path,
            """{"udpPort": 20800, "shareBaseUrl": "https://telemetrie.erdi-erc.de", "shareToken": "  "}""");

        var settings = AppSettingsStore.Load(_path);

        Assert.Equal("https://telemetrie.erdi-erc.de", settings.ShareBaseUrl);
        Assert.Null(settings.ShareToken);
    }

    [Fact]
    public void Save_then_load_round_trips_twitch_token()
    {
        // Arrange
        var settings = new AppSettings() with { TwitchToken = "oauth:supersecret-token" };

        // Act
        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        // Assert — whatever the store does to the token on disk (encrypt on Windows,
        // plaintext elsewhere), it must come back identical.
        Assert.Equal("oauth:supersecret-token", loaded.TwitchToken);
    }

    [Fact]
    public void Save_never_writes_the_twitch_token_in_plain_text_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only; plaintext persistence is the non-Windows fallback.
        }

        var settings = new AppSettings() with { TwitchToken = "oauth:dont-write-me-plain" };

        AppSettingsStore.Save(settings, _path);
        var onDisk = File.ReadAllText(_path);

        Assert.DoesNotContain("oauth:dont-write-me-plain", onDisk);
        Assert.Contains("\"twitchToken\": \"dpapi:", onDisk);
    }

    [Fact]
    public void Load_accepts_a_legacy_plaintext_token()
    {
        // Files written by older versions store the token in the clear; they must still load.
        File.WriteAllText(_path, """{"twitchToken": "oauth:legacy-plaintext"}""");

        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal("oauth:legacy-plaintext", loaded.TwitchToken);
    }

    [Fact]
    public void Saving_a_loaded_legacy_token_encrypts_it_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A legacy plaintext file loads fine and the next save upgrades it to DPAPI.
        File.WriteAllText(_path, """{"twitchToken": "oauth:legacy-plaintext"}""");
        var loaded = AppSettingsStore.Load(_path);
        Assert.Equal("oauth:legacy-plaintext", loaded.TwitchToken);

        AppSettingsStore.Save(loaded, _path);
        var onDisk = File.ReadAllText(_path);

        Assert.DoesNotContain("oauth:legacy-plaintext", onDisk);
        Assert.Contains("\"twitchToken\": \"dpapi:", onDisk);
        Assert.Equal("oauth:legacy-plaintext", AppSettingsStore.Load(_path).TwitchToken);
    }

    [Fact]
    public void Load_drops_a_corrupt_dpapi_token()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A blob that does not decrypt (different user, reimaged machine, tampered file)
        // must not crash the app — the token simply comes back null.
        File.WriteAllText(_path, """{"twitchToken": "dpapi:bm90LWFjcnlwdG9hYmxvYg=="}""");

        var loaded = AppSettingsStore.Load(_path);

        Assert.Null(loaded.TwitchToken);
    }

    [Fact]
    public void NeedsSecretMigration_returns_true_for_a_legacy_plaintext_secret()
    {
        File.WriteAllText(_path, """{"twitchToken": "oauth:legacy-plaintext"}""");
        Assert.True(AppSettingsStore.NeedsSecretMigration(_path));

        File.WriteAllText(_path, """{"ercApiKey": "erc-legacy-plaintext"}""");
        Assert.True(AppSettingsStore.NeedsSecretMigration(_path));

        File.WriteAllText(_path, """{"llmApiKey": "ollama-legacy-plaintext"}""");
        Assert.True(AppSettingsStore.NeedsSecretMigration(_path));
    }

    [Fact]
    public void NeedsSecretMigration_returns_false_once_the_legacy_secret_is_upgraded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // no DPAPI → Save cannot encrypt, nothing to upgrade (covered by Save tests).
        }

        // A legacy file upgrades on the first save; the on-disk dpapi: marker must make the
        // migration idempotent — this is what stops the "one-time" upgrade from re-writing
        // (and re-encrypting) the file on every launch.
        File.WriteAllText(_path, """{"twitchToken": "oauth:legacy-plaintext"}""");
        AppSettingsStore.Save(AppSettingsStore.Load(_path), _path);

        Assert.False(AppSettingsStore.NeedsSecretMigration(_path));
    }

    [Fact]
    public void NeedsSecretMigration_returns_false_for_an_already_encrypted_secret()
    {
        // The exact on-disk state after a prior run: dpapi: even though Load decrypts it to
        // plaintext in memory — the check must look at the raw disk value, not the decrypted one.
        File.WriteAllText(_path, """{"twitchToken": "dpapi:YWJj"}""");
        Assert.False(AppSettingsStore.NeedsSecretMigration(_path));

        File.WriteAllText(_path, """{"ercApiKey": "dpapi:YWJj", "llmApiKey": "dpapi:YWJj"}""");
        Assert.False(AppSettingsStore.NeedsSecretMigration(_path));
    }

    [Fact]
    public void NeedsSecretMigration_returns_false_for_missing_blank_null_or_corrupt()
    {
        Assert.False(AppSettingsStore.NeedsSecretMigration(_path)); // missing

        File.WriteAllText(_path, """{"twitchToken": ""}""");
        Assert.False(AppSettingsStore.NeedsSecretMigration(_path)); // blank

        File.WriteAllText(_path, """{"twitchToken": null}""");
        Assert.False(AppSettingsStore.NeedsSecretMigration(_path)); // null

        File.WriteAllText(_path, "{ this is not json }");
        Assert.False(AppSettingsStore.NeedsSecretMigration(_path)); // corrupt
    }

    [Fact]
    public void Save_never_writes_the_erc_or_llm_api_keys_in_plain_text_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only; plaintext persistence is the non-Windows fallback.
        }

        var settings = new AppSettings() with
        {
            ErcApiKey = "erc-do-not-write-me-plain",
            LlmApiKey = "ollama-do-not-write-me-plain",
        };

        AppSettingsStore.Save(settings, _path);
        var onDisk = File.ReadAllText(_path);

        Assert.DoesNotContain("erc-do-not-write-me-plain", onDisk);
        Assert.DoesNotContain("ollama-do-not-write-me-plain", onDisk);
        Assert.Contains("\"ercApiKey\": \"dpapi:", onDisk);
        Assert.Contains("\"llmApiKey\": \"dpapi:", onDisk);
    }

    [Fact]
    public void Save_then_load_round_trips_encrypted_api_keys()
    {
        var settings = new AppSettings() with
        {
            ErcApiKey = "erc-roundtrip",
            LlmApiKey = "ollama-roundtrip",
        };

        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal("erc-roundtrip", loaded.ErcApiKey);
        Assert.Equal("ollama-roundtrip", loaded.LlmApiKey);
    }

    [Fact]
    public void Load_accepts_legacy_plaintext_api_keys_and_next_save_encrypts_them_on_windows()
    {
        // Files written by older versions store the keys in the clear; they must still load.
        File.WriteAllText(_path, """{"ercApiKey": "erc-legacy", "llmApiKey": "ollama-legacy"}""");

        var loaded = AppSettingsStore.Load(_path);
        Assert.Equal("erc-legacy", loaded.ErcApiKey);
        Assert.Equal("ollama-legacy", loaded.LlmApiKey);

        if (OperatingSystem.IsWindows())
        {
            AppSettingsStore.Save(loaded, _path);
            var onDisk = File.ReadAllText(_path);
            Assert.DoesNotContain("erc-legacy", onDisk);
            Assert.DoesNotContain("ollama-legacy", onDisk);
            Assert.False(AppSettingsStore.NeedsSecretMigration(_path));
        }
    }

    [Fact]
    public void Save_then_load_round_trips_forwarding()
    {
        var settings = new AppSettings(
            udpPort: 20777,
            overlayPort: 8090,
            theme: AppTheme.System,
            rivalDriverName: null,
            closeToTray: false,
            showNotifications: true,
            autoStartListening: true,
            forwardingEnabled: true,
            forwardTargets:
            [
                new ForwardTarget { Address = "127.0.0.1", Port = 20779, Enabled = true },
                new ForwardTarget { Address = "127.0.0.1", Port = 20780, Enabled = false },
            ]);

        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        Assert.True(loaded.ForwardingEnabled);
        Assert.NotNull(loaded.ForwardTargets);
        Assert.Equal(2, loaded.ForwardTargets!.Count);
        Assert.Equal(
            new ForwardTarget { Address = "127.0.0.1", Port = 20779, Enabled = true },
            loaded.ForwardTargets![0]);
        Assert.Equal(
            new ForwardTarget { Address = "127.0.0.1", Port = 20780, Enabled = false },
            loaded.ForwardTargets![1]);
    }

    [Fact]
    public void Load_sanitizes_forward_targets()
    {
        File.WriteAllText(
            _path,
            """{"forwardingEnabled": true, "forwardTargets": [{"address": "127.0.0.1", "port": 20779, "enabled": true}, {"address": "  ", "port": 20780, "enabled": true}, {"address": "127.0.0.1", "port": 0, "enabled": true}, {"address": "127.0.0.1", "port": 70000, "enabled": true}]}""");

        var settings = AppSettingsStore.Load(_path);

        Assert.True(settings.ForwardingEnabled);
        Assert.NotNull(settings.ForwardTargets);
        var target = Assert.Single(settings.ForwardTargets!);
        Assert.Equal("127.0.0.1", target.Address);
        Assert.Equal(20779, target.Port);
        Assert.True(target.Enabled);
    }

    [Fact]
    public void Load_caps_forward_targets_at_8()
    {
        var entries = string.Join(",", Enumerable.Range(1, 12)
            .Select(i => $"{{\"address\": \"127.0.0.1\", \"port\": {20770 + i}, \"enabled\": true}}"));
        File.WriteAllText(_path, $"{{\"forwardingEnabled\": true, \"forwardTargets\": [{entries}]}}");

        var settings = AppSettingsStore.Load(_path);

        Assert.NotNull(settings.ForwardTargets);
        Assert.Equal(AppSettings.MaxForwardTargets, settings.ForwardTargets!.Count);
    }

    [Fact]
    public void Load_missing_file_keeps_voice_alerts_off()
    {
        var settings = AppSettingsStore.Load(_path);

        Assert.False(settings.VoiceAlertsEnabled);
    }

    [Fact]
    public void Save_then_load_round_trips_voice_alerts()
    {
        var settings = new AppSettings() with { VoiceAlertsEnabled = true };

        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        Assert.True(loaded.VoiceAlertsEnabled);
    }

    [Fact]
    public void Save_then_load_round_trips_voice()
    {
        var settings = new AppSettings() with { Voice = "de-DE-ConradNeural" };

        AppSettingsStore.Save(settings, _path);
        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal("de-DE-ConradNeural", loaded.Voice);
    }

    [Fact]
    public void Load_missing_voice_keeps_null_default()
    {
        var settings = AppSettingsStore.Load(_path);

        Assert.Null(settings.Voice);
    }

    [Fact]
    public void Load_sanitizes_whitespace_voice_to_null()
    {
        File.WriteAllText(_path, """{"udpPort": 20800, "voice": "   "}""");

        var settings = AppSettingsStore.Load(_path);

        Assert.Null(settings.Voice);
    }

    [Fact]
    public void Sanitized_trims_and_empties_voice()
    {
        Assert.Equal("de-DE-KatjaNeural", (new AppSettings() with { Voice = "  de-DE-KatjaNeural  " }).Sanitized().Voice);
        Assert.Null((new AppSettings() with { Voice = " " }).Sanitized().Voice);
    }
}