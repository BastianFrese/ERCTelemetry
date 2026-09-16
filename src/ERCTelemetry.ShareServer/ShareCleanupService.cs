using System.Globalization;

namespace ERCTelemetry.ShareServer;

/// <summary>Deletes shared sessions older than the retention window. Runs hourly; a
/// session's age is the last-write time of its manifest.json. Every file it deletes must be
/// matched with the in-memory byte accounting: orphaned chunks and expired sessions had
/// their bytes claimed against the per-session and server-global budgets when uploaded, so
/// the sweep reports each reap back to the caller via callbacks and the caller refunds the
/// budgets. Without that, the budgets would drift toward exhaustion on a long-lived server
/// (HIGH, 2026-09-16).</summary>
public sealed class ShareCleanupService : BackgroundService
{
    private readonly string _rootPath;
    private readonly TimeSpan _retention;
    private readonly ILogger<ShareCleanupService> _logger;
    private readonly Action<ulong, long>? _onOrphanPartReaped;
    private readonly Action<ulong>? _onSessionReleased;

    /// <summary><paramref name="onOrphanPartReaped"/> receives (session uid, reaped bytes) for
    /// each orphaned chunk file deleted; <paramref name="onSessionReleased"/> receives the uid
    /// of each expired session whose directory was removed. Both are optional — the tests and
    /// the production wiring use them to keep the budgets consistent.</summary>
    public ShareCleanupService(
        string rootPath, int retentionDays, ILogger<ShareCleanupService> logger,
        Action<ulong, long>? onOrphanPartReaped = null, Action<ulong>? onSessionReleased = null)
    {
        _rootPath = rootPath;
        _retention = TimeSpan.FromDays(retentionDays);
        _logger = logger;
        _onOrphanPartReaped = onOrphanPartReaped;
        _onSessionReleased = onSessionReleased;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                CleanupOnce();
            }
            catch (Exception ex)
            {
                // A failed sweep must not kill the background service.
                _logger.LogWarning(ex, "Share cleanup failed");
            }
        }
    }

    /// <summary>How old a stray .part* file must be before it counts as orphaned. A real
    /// multi-part upload finishes within seconds/minutes; anything older than an hour is
    /// a crashed upload that never assembled (the last part never arrived).</summary>
    private static readonly TimeSpan OrphanPartAge = TimeSpan.FromHours(1);

    // Internal so the tests can drive a sweep synchronously (the timer would otherwise make
    // the accounting path untestable without a real hour-long wait).
    internal void CleanupOnce()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _retention;
        var partCutoff = DateTime.UtcNow - OrphanPartAge;
        foreach (var dir in Directory.EnumerateDirectories(_rootPath))
        {
            try
            {
                // Session dirs are named by uid. Without a parseable uid the accounting callbacks
                // cannot attribute bytes to a session, so the files are still cleaned up but the
                // budgets are left untouched — such a dir is not one the upload path created.
                var uid = ulong.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (ulong?)null;

                // Orphaned chunk scratch files from a crashed upload: remove them so they
                // cannot accumulate inside an otherwise live session (the assembly step that
                // would normally delete them never ran). Their bytes were counted when uploaded,
                // so the reaped size must be refunded to the budgets (HIGH budget-leak fix).
                foreach (var part in Directory.EnumerateFiles(dir, "*.part*"))
                {
                    if (File.GetLastWriteTimeUtc(part) < partCutoff)
                    {
                        var size = new FileInfo(part).Length;
                        File.Delete(part);
                        _logger.LogInformation("Removed orphaned upload part {Part}", part);
                        if (uid is { } sessionUid)
                        {
                            _onOrphanPartReaped?.Invoke(sessionUid, size);
                        }
                    }
                }

                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(manifest) < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("Removed expired shared session {Dir}", dir);
                    if (uid is { } sessionUid)
                    {
                        _onSessionReleased?.Invoke(sessionUid);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A concurrent DELETE can remove the session directory (or a file in it) while
                // we sweep; that one session must not abort the whole hourly run.
                _logger.LogWarning(ex, "Share cleanup skipped session directory {Dir}", dir);
            }
        }
    }
}
