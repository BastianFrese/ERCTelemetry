using System.Globalization;

namespace ERCTelemetry.Core.Share;

/// <summary>Server-side path logic for shared sessions — pure and testable. Every path
/// is derived from a validated uid/file name, so a request can never escape the root.</summary>
public static class SharePaths
{
    /// <summary>Folder holding one shared session's manifest.json + clip MP4s.</summary>
    public static string SessionDir(string root, ulong uid) =>
        Path.Combine(root, uid.ToString(CultureInfo.InvariantCulture));

    /// <summary>The session's manifest.json (header facts + results + clip list).</summary>
    public static string ManifestPath(string root, ulong uid) =>
        Path.Combine(SessionDir(root, uid), "manifest.json");

    /// <summary>Absolute path of one uploaded clip file.</summary>
    public static string ClipPath(string root, ulong uid, string fileName) =>
        Path.Combine(SessionDir(root, uid), fileName);

    /// <summary>True when the uid is a plain digit string that fits a ulong (the only
    /// shape the app generates). Rejects letters, separators, signs and overflow.</summary>
    public static bool IsValidUid(string uid) =>
        uid.Length is > 0 and <= 20 &&
        ulong.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>True when the file name is a plain clip-*.mp4 — no path separators, no
    /// "..", no control characters (a NUL/control byte in a file name is a smuggling
    /// vector on some filesystems and breaks logs/tools), and no characters Windows cannot
    /// store in a file name (: * ? " &lt; &gt; |), nor a trailing dot or space (which the
    /// Win32 API silently strips, so "clip-1.mp4." and "clip-1.mp4" would map to the same
    /// path — an alternative-streams ambiguity). The app only ever uploads clip-{n}.mp4.</summary>
    public static bool IsValidFileName(string fileName) =>
        fileName.StartsWith("clip-", StringComparison.Ordinal) &&
        fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
        fileName.IndexOfAny(['/', '\\']) < 0 &&
        fileName.IndexOf("..", StringComparison.Ordinal) < 0 &&
        !fileName.Any(char.IsControl) &&
        fileName.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) < 0 &&
        !fileName.EndsWith(' ') &&
        !fileName.EndsWith('.');
}
