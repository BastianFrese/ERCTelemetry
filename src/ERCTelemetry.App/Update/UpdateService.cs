using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using ERCTelemetry.Core.Update;

namespace ERCTelemetry.App.Update;

/// <summary>Checks the update server for a newer version and applies it. Preferred path is
/// the Discord-style delta update: only the changed files are downloaded to a staging folder
/// and applied by apply-update.ps1 on exit (no UAC, no reinstall). When the file manifest is
/// missing or the delta fails, it falls back to the full Setup.exe (UAC-elevated Inno
/// installer). Settings/history in %LOCALAPPDATA% survive either way.</summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>Built-in default when settings hold no override URL.</summary>
    public const string DefaultBaseUrl = "https://erdi-erc.de/downloads";

    /// <summary>Result of <see cref="CheckAsync"/>.</summary>
    public enum CheckResult
    {
        UpToDate,
        UpdateAvailable,
        Unreachable,
        Invalid,
    }

    /// <summary>Outcome of <see cref="DownloadDeltaAsync"/>.</summary>
    public enum DeltaResult
    {
        /// <summary>Staging complete, apply.json written, apply-update.ps1 copied — the
        /// update applies on exit (or immediately via <see cref="LaunchDeltaUpdater"/>).</summary>
        Ready,
        /// <summary>No usable file manifest, nothing changed, or a download failed — the
        /// caller should fall back to the full Setup.exe.</summary>
        Fallback,
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly UpdateDownloader _downloader;
    private readonly Func<string?> _baseUrl;

    /// <summary>Feeds the service the settings URL (null/empty → built-in default); the
    /// running version is read from the entry assembly.</summary>
    public UpdateService(Func<string?> baseUrl)
    {
        _baseUrl = baseUrl;
        _downloader = new UpdateDownloader(_http);
    }

    /// <summary>Releases the shared <see cref="HttpClient"/> (app lifetime).</summary>
    public void Dispose() => _http.Dispose();

    /// <summary>Queries the server manifest and compares it against the running version.
    /// Never throws — network errors map to <see cref="CheckResult.Unreachable"/>,
    /// unusable responses to <see cref="CheckResult.Invalid"/>.</summary>
    public async Task<(CheckResult Result, UpdateManifest? Manifest)> CheckAsync(CancellationToken ct = default)
    {
        var url = $"{ResolveBaseUrl()}/ERCTelemetry.version.json";
        string? json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return (CheckResult.Unreachable, null);
        }

        var manifest = UpdateManifestParser.Parse(json);
        if (manifest is null)
        {
            return (CheckResult.Invalid, null);
        }

        return UpdateEvaluator.Evaluate(EntryVersion(), manifest) == UpdateDecision.Available
            ? (CheckResult.UpdateAvailable, manifest)
            : (CheckResult.UpToDate, manifest);
    }

    /// <summary>Full-Setup fallback: streams the manifest's installer file to
    /// %LOCALAPPDATA%\ERCTelemetry\update\ERCTelemetry-Setup.exe (atomic via .part file) and
    /// verifies its SHA256 against the manifest. The manifest file name is reduced to a plain
    /// *.exe name so a tampered manifest can never point the download outside the update
    /// folder.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double> progress, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(manifest.FileName);
        if (fileName.Length == 0 || fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) is false)
        {
            fileName = UpdateManifestParser.DefaultFileName;
        }

        return await _downloader.DownloadFileAsync(
            $"{ResolveBaseUrl()}/{fileName}", TargetPath(fileName), manifest.Sha256, progress, ct);
    }

    /// <summary>True when the install directory is user-writable (the app lives in
    /// %LOCALAPPDATA%\Programs\ERCTelemetry). Program-Files installs (old installer) are
    /// read-only for the user, so the delta update cannot apply there — the caller falls
    /// back to the full Setup.exe.</summary>
    public static bool CanWriteToInstallDir()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Delta update: fetches ERCTelemetry.files.json, compares the local install
    /// directory, downloads only the changed files into the staging folder (each SHA256-
    /// verified), writes apply.json and copies apply-update.ps1 next to it. Returns
    /// <see cref="DeltaResult.Ready"/> when the update is staged and will apply on exit;
    /// <see cref="DeltaResult.Fallback"/> when the file manifest is unusable, nothing
    /// changed, or a download failed.</summary>
    public async Task<DeltaResult> DownloadDeltaAsync(UpdateManifest manifest, IProgress<double> progress, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl();
        var fileManifest = await _downloader.FetchFileManifestAsync(baseUrl, ct);
        if (fileManifest is null)
        {
            return DeltaResult.Fallback;
        }

        var installDir = AppContext.BaseDirectory;
        var changed = UpdateFileComparer.FindChanged(fileManifest, installDir);
        if (changed.Count == 0)
        {
            return DeltaResult.Fallback;
        }

        var stagingRoot = StagingRoot();
        Directory.CreateDirectory(stagingRoot);
        ClearDirectory(stagingRoot);

        var total = changed.Sum(e => e.Size);
        long done = 0;
        foreach (var entry in changed)
        {
            var localPath = UpdateFileComparer.ResolveLocalPath(stagingRoot, entry.Path)!;
            // The manifest paths come from the app's own file layout, but one segment
            // can still contain spaces or special chars (e.g. a username in %LOCALAPPDATA%
            // or a session file). Escaping each segment keeps the download URL well-formed.
            var relativePath = string.Join("/", entry.Path.Replace('\\', '/')
                .Split('/').Select(p => Uri.EscapeDataString(p)));
            var url = $"{baseUrl}/{relativePath}";
            var fileProgress = new Progress<double>(p =>
                progress.Report(total > 0 ? 100.0 * (done + entry.Size * p / 100.0) / total : 0));
            await _downloader.DownloadFileAsync(url, localPath, entry.Sha256, fileProgress, ct);
            done += entry.Size;
        }

        WriteApplyJson(manifest, installDir, stagingRoot, fileManifest);
        CopyApplyScript();
        progress.Report(100);
        return DeltaResult.Ready;
    }

    /// <summary>Starts apply-update.ps1 (hidden PowerShell, no UAC — the install dir is
    /// user-writable) and shuts the app down so the script can replace its files and restart
    /// the app. Used from the update card's install button.</summary>
    public void LaunchDeltaUpdater(string applyScriptPath)
    {
        StartDeltaUpdater(applyScriptPath);
        Application.Current.Shutdown();
    }

    /// <summary>Starts apply-update.ps1 without shutting the app down — for the apply-on-exit
    /// path in OnClosing, where the window is already closing and the app exits naturally.</summary>
    public void StartDeltaUpdater(string applyScriptPath)
    {
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{applyScriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>Starts the downloaded installer via the shell (UAC prompt for the elevated
    /// Inno Setup run) and shuts the app down so the installer can replace its files.</summary>
    public void LaunchInstaller(string installerPath)
    {
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
        });
        Application.Current.Shutdown();
    }

    private Version? EntryVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;

    /// <summary>Settings URL when present, else the built-in default; always without a
    /// trailing slash so appended paths stay well-formed.</summary>
    private string ResolveBaseUrl()
    {
        var fromSettings = _baseUrl();
        var baseUrl = string.IsNullOrWhiteSpace(fromSettings) ? DefaultBaseUrl : fromSettings!;
        return baseUrl.TrimEnd('/');
    }

    /// <summary>User-visible update log (uploaded by the release pipeline next to the
    /// manifest); the Updates card links to it so users can see what changed.</summary>
    public string UpdateLogUrl => $"{ResolveBaseUrl()}/UPDATELOG.md";

    /// <summary>%LOCALAPPDATA%\ERCTelemetry\update — shared by the Setup download, the delta
    /// staging folder and the apply script/result files.</summary>
    public static string UpdateRootPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERCTelemetry", "update");

    /// <summary>Path of the copied apply-update.ps1 that the app launches on exit.</summary>
    public static string ApplyScriptPath() => Path.Combine(UpdateRootPath(), "apply-update.ps1");

    private static string StagingRoot() => Path.Combine(UpdateRootPath(), "staging");

    private static string TargetPath(string fileName) => Path.Combine(UpdateRootPath(), fileName);

    private void WriteApplyJson(UpdateManifest manifest, string installDir, string stagingRoot, UpdateFileManifest fileManifest)
    {
        var apply = new
        {
            version = manifest.Version,
            staging = stagingRoot,
            target = installDir,
            appExe = Environment.ProcessPath ?? Path.Combine(installDir, "ERCTelemetry.exe"),
            files = fileManifest.Files.Select(f => new { f.Path, f.Size, f.Sha256 }).ToArray(),
        };
        File.WriteAllText(Path.Combine(UpdateRootPath(), "apply.json"),
            JsonSerializer.Serialize(apply, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void CopyApplyScript()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "apply-update.ps1");
        File.Copy(source, ApplyScriptPath(), overwrite: true);
    }

    /// <summary>Best-effort wipe of the staging folder before a fresh delta download; a
    /// leftover from a failed attempt is harmless (overwritten anyway), so failures are
    /// ignored.</summary>
    private static void ClearDirectory(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                Directory.Delete(sub, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore — stale staging files are overwritten by the download anyway
        }
    }
}
