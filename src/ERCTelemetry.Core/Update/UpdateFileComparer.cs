using System.Security.Cryptography;

namespace ERCTelemetry.Core.Update;

/// <summary>Compares the local install directory against a published file manifest and
/// reports which files are missing or changed. Pure logic (no I/O beyond reading local
/// files), kept in Core so it is unit-testable.</summary>
public static class UpdateFileComparer
{
    /// <summary>Entries of <paramref name="manifest"/> whose local file is missing or whose
    /// SHA256 differs from the published one. Entries whose path would escape
    /// <paramref name="localRoot"/> (path traversal in a tampered manifest) are skipped.</summary>
    public static IReadOnlyList<UpdateFileEntry> FindChanged(UpdateFileManifest manifest, string localRoot)
    {
        var changed = new List<UpdateFileEntry>();
        foreach (var entry in manifest.Files)
        {
            var localPath = ResolveLocalPath(localRoot, entry.Path);
            if (localPath is null)
            {
                continue; // unsafe path — never touch anything outside the install dir
            }

            if (File.Exists(localPath) is false || Sha256Of(localPath) != entry.Sha256)
            {
                changed.Add(entry);
            }
        }

        return changed;
    }

    /// <summary>Combines the manifest's forward-slash path with the install root and verifies
    /// the result stays inside it; null when the path is rooted or escapes via "..".</summary>
    public static string? ResolveLocalPath(string localRoot, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(localRoot, normalized));
        var root = Path.GetFullPath(localRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) is false)
        {
            return null;
        }

        return full;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
