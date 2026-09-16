namespace ERCTelemetry.Core.Clips;

/// <summary>On-disk layout of recorded clips: one folder per session under
/// %LOCALAPPDATA%\ERCTelemetry\clips\{sessionUid}\. Path logic is pure (testable);
/// the App service does the actual capture/encode.</summary>
public static class ClipStorage
{
    /// <summary>Test hook: overrides the root folder (null = default %LOCALAPPDATA%).</summary>
    internal static string? RootOverride { get; set; }

    /// <summary>Root folder holding one subfolder per session.</summary>
    public static string RootPath() => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERCTelemetry", "clips");

    /// <summary>Folder for one session's clips.</summary>
    public static string SessionFolder(ulong sessionUid) =>
        Path.Combine(RootPath(), sessionUid.ToString());

    /// <summary>File name for one clip: clip-{utc}-L{lap}.mp4 (lap 0 = unknown).</summary>
    public static string BuildFileName(ClipMetadata metadata) =>
        $"clip-{metadata.Utc:yyyyMMdd-HHmmss}-L{metadata.LapNumber}.mp4";

    /// <summary>Full path of one clip file.</summary>
    public static string BuildPath(ClipMetadata metadata) =>
        Path.Combine(SessionFolder(metadata.SessionUid), BuildFileName(metadata));

    /// <summary>Deletes clip files older than <paramref name="maxAge"/> (startup cleanup)
    /// and prunes the now-empty session folders. Returns the number of deleted files.
    /// A clip currently open in a player is skipped — the next startup retries it.</summary>
    public static int CleanupOldClips(TimeSpan maxAge)
    {
        var root = RootPath();
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // In use — skip, next startup retries.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any() is false)
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}
