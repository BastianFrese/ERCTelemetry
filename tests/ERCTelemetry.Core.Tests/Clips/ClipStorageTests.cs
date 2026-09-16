using ERCTelemetry.Core.Clips;
using Xunit;

namespace ERCTelemetry.Core.Tests.Clips;

/// <summary>ClipStorage: file naming, folder layout and the age-based cleanup.</summary>
public class ClipStorageTests
{
    [Fact]
    public void Build_file_name_embeds_utc_and_lap()
    {
        var metadata = new ClipMetadata(
            42, new DateTimeOffset(2026, 9, 7, 18, 30, 0, TimeSpan.Zero), 12, 3, 7,
            "Basti", "Rival", 1);

        Assert.Equal("clip-20260907-183000-L12.mp4", ClipStorage.BuildFileName(metadata));
    }

    [Fact]
    public void Build_path_uses_the_session_folder()
    {
        var metadata = new ClipMetadata(
            42, new DateTimeOffset(2026, 9, 7, 18, 30, 0, TimeSpan.Zero), 12, 3, null,
            "Basti", null, 1);

        var path = ClipStorage.BuildPath(metadata);

        Assert.EndsWith(Path.Combine("clips", "42", "clip-20260907-183000-L12.mp4"), path);
    }

    [Fact]
    public void Cleanup_deletes_old_clips_and_keeps_fresh_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), $"erc-clips-{Guid.NewGuid():N}");
        var sessionFolder = Path.Combine(root, "42");
        Directory.CreateDirectory(sessionFolder);
        var oldFile = Path.Combine(sessionFolder, "old.mp4");
        var freshFile = Path.Combine(sessionFolder, "fresh.mp4");
        File.WriteAllText(oldFile, "x");
        File.WriteAllText(freshFile, "x");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow);

        try
        {
            // Point the storage at the temp root via the internal test hook.
            ClipStorage.RootOverride = root;
            try
            {
                var deleted = ClipStorage.CleanupOldClips(TimeSpan.FromDays(30));
                Assert.Equal(1, deleted);
                Assert.False(File.Exists(oldFile));
                Assert.True(File.Exists(freshFile));
            }
            finally
            {
                ClipStorage.RootOverride = null;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
