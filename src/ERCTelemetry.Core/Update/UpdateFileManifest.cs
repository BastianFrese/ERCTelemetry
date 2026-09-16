using System.Text.Json;

namespace ERCTelemetry.Core.Update;

/// <summary>One published file of the app, as listed in ERCTelemetry.files.json. The path is
/// relative to the install directory and always uses forward slashes (the pipeline writes
/// them; the app converts to platform separators when comparing/downloading).</summary>
/// <param name="Path">Relative path inside the install directory (e.g. "ERCTelemetry.dll").</param>
/// <param name="Size">File size in bytes — informational, the SHA256 is the real check.</param>
/// <param name="Sha256">SHA256 of the file (uppercase hex, 64 chars).</param>
public sealed record UpdateFileEntry(string Path, long Size, string Sha256);

/// <summary>Full file listing of a published release, used by the delta update to decide
/// which local files are missing or changed and must be downloaded.</summary>
/// <param name="Files">All files of the release, relative to the install directory.</param>
public sealed record UpdateFileManifest(IReadOnlyList<UpdateFileEntry> Files);

/// <summary>Tolerant parser for ERCTelemetry.files.json — external data, so anything
/// malformed degrades to "no file manifest" (the caller falls back to the full Setup.exe),
/// never throws.</summary>
public static class UpdateFileManifestParser
{
    public static UpdateFileManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || root.TryGetProperty("files", out var files) is false
                || files.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<UpdateFileEntry>();
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                var path = GetString(item, "path");
                var sha256 = (GetString(item, "sha256") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(path) || IsValidSha256(sha256) is false)
                {
                    continue; // skip unusable entries, keep the rest
                }

                entries.Add(new UpdateFileEntry(
                    Path: path,
                    Size: item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                        ? size.GetInt64()
                        : 0,
                    Sha256: sha256));
            }

            return entries.Count > 0 ? new UpdateFileManifest(entries) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsValidSha256(string sha256) =>
        sha256.Length == 64 && sha256.All(Uri.IsHexDigit);
}
