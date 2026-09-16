using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ERCTelemetry.ShareServer.Tests;

/// <summary>The retention sweep must report every file it deletes back to the budget
/// accounting (HIGH, 2026-09-16): an orphaned chunk part refunds its bytes via
/// onOrphanPartReaped, an expired session's uid is reported via onSessionReleased, and a
/// live session is left alone. Drives a synchronous CleanupOnce with recording callbacks so
/// the hourly timer never plays a part. Shares the "ShareServer" collection because the
/// other classes reconfigure process env vars in their fixtures.</summary>
[Collection("ShareServer")]
public sealed class CleanupTests : IDisposable
{
    private readonly string _root;
    private readonly List<(ulong Uid, long Bytes)> _orphans = new();
    private readonly List<ulong> _released = new();

    public CleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"erc-cleanup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ShareCleanupService NewService(int retentionDays) => new(
        _root, retentionDays, NullLogger<ShareCleanupService>.Instance,
        (uid, bytes) => _orphans.Add((uid, bytes)),
        uid => _released.Add(uid));

    [Fact]
    public void An_orphaned_part_older_than_an_hour_is_reaped_and_its_bytes_refunded()
    {
        const ulong uid = 44_001;
        WritePart(_root, uid, age: TimeSpan.FromHours(2), content: "abcde"); // 5 bytes

        NewService(retentionDays: 30).CleanupOnce();

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, uid.ToString()), "*.part*"));
        Assert.Equal([(uid, 5L)], _orphans);
        Assert.Empty(_released); // the session itself is still live
    }

    [Fact]
    public void A_recent_part_is_left_in_place_and_not_refunded()
    {
        const ulong uid = 44_002;
        WritePart(_root, uid, age: TimeSpan.FromMinutes(10), content: "fresh");

        NewService(retentionDays: 30).CleanupOnce();

        Assert.Single(Directory.GetFiles(Path.Combine(_root, uid.ToString()), "*.part*"));
        Assert.Empty(_orphans);
    }

    [Fact]
    public void An_expired_session_is_removed_and_released_once()
    {
        const ulong uid = 44_003;
        WriteManifest(_root, uid, age: TimeSpan.FromDays(3)); // retention below is 2 days
        WriteClip(_root, uid, "clip-1.mp4"); // a file inside the directory tree

        NewService(retentionDays: 2).CleanupOnce();

        Assert.False(Directory.Exists(Path.Combine(_root, uid.ToString())));
        Assert.Equal([uid], _released);
        Assert.Empty(_orphans);
    }

    [Fact]
    public void A_live_session_is_untouched_and_never_released()
    {
        const ulong uid = 44_004;
        WriteManifest(_root, uid, age: TimeSpan.FromDays(1)); // fresh
        WriteClip(_root, uid, "clip-1.mp4");

        NewService(retentionDays: 30).CleanupOnce();

        Assert.True(File.Exists(Path.Combine(_root, uid.ToString(), "clip-1.mp4")));
        Assert.Empty(_released);
    }

    [Fact]
    public void An_expired_session_with_an_orphaned_part_is_reaped_and_released()
    {
        const ulong uid = 44_005;
        WriteManifest(_root, uid, age: TimeSpan.FromDays(3));
        WritePart(_root, uid, age: TimeSpan.FromHours(2), content: "xyz");

        NewService(retentionDays: 2).CleanupOnce();

        Assert.False(Directory.Exists(Path.Combine(_root, uid.ToString())));
        Assert.Equal([(uid, 3L)], _orphans);
        Assert.Equal([uid], _released);
    }

    [Fact]
    public void A_stale_part_in_a_non_uid_directory_is_reaped_without_callbacks()
    {
        // A directory the upload path never created cannot be attributed to a session, so
        // the file is cleaned up but nothing is refunded (no budget entry exists for it).
        // The uid parse also fails on the whole-dir name for the session-release callback.
        var dir = Path.Combine(_root, "garbage");
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "stray.part0");
        File.WriteAllText(part, "stale");
        File.SetLastWriteTimeUtc(part, DateTime.UtcNow - TimeSpan.FromHours(2));

        NewService(retentionDays: 30).CleanupOnce();

        Assert.False(File.Exists(part));
        Assert.Empty(_orphans);
        Assert.Empty(_released);
    }

    private static void WritePart(string root, ulong uid, TimeSpan age, string content)
    {
        var part = Path.Combine(SessionDir(root, uid), "clip-1.mp4.part0");
        File.WriteAllText(part, content);
        File.SetLastWriteTimeUtc(part, DateTime.UtcNow - age);
    }

    private static void WriteManifest(string root, ulong uid, TimeSpan age)
    {
        var manifest = Path.Combine(SessionDir(root, uid), "manifest.json");
        File.WriteAllText(manifest, "{}");
        File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow - age);
    }

    private static void WriteClip(string root, ulong uid, string name) =>
        File.WriteAllText(Path.Combine(SessionDir(root, uid), name), "mp4");

    private static string SessionDir(string root, ulong uid)
    {
        var dir = Path.Combine(root, uid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
