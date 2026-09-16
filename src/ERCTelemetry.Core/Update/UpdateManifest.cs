using System.Text.Json;

namespace ERCTelemetry.Core.Update;

/// <summary>Version manifest published next to the Setup.exe on the update server
/// (erdi-erc.de/downloads/ERCTelemetry.version.json). Written by the installer build
/// pipeline; read by the app's UpdateService to decide whether an update exists and to
/// verify the downloaded Setup.exe.</summary>
/// <param name="Version">Version of the published Setup.exe (e.g. "1.0.1").</param>
/// <param name="PublishedUtc">Upload time (ISO 8601 UTC), informational only.</param>
/// <param name="Sha256">SHA256 of the Setup.exe file (uppercase hex, 64 chars) — the
/// downloaded installer is verified against this before it is launched.</param>
/// <param name="FileName">Name of the downloadable installer file on the server.</param>
public sealed record UpdateManifest(
    string Version,
    string PublishedUtc,
    string Sha256,
    string FileName);

/// <summary>Tolerant manifest parser — the server response is external data, so anything
/// malformed or incomplete must degrade to "no manifest", never throw.</summary>
public static class UpdateManifestParser
{
    /// <summary>Default installer file name when the manifest omits <c>fileName</c>.</summary>
    public const string DefaultFileName = "ERCTelemetry-Setup.exe";

    public static UpdateManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var version = GetString(root, "version");
            if (version is null || System.Version.TryParse(version, out _) is false)
            {
                return null; // without a parseable version the manifest is useless
            }

            var sha256 = (GetString(root, "sha256") ?? string.Empty).Trim();
            if (IsValidSha256(sha256) is false)
            {
                return null; // without a verifiable hash the downloaded Setup.exe cannot be trusted
            }

            return new UpdateManifest(
                Version: version,
                PublishedUtc: GetString(root, "publishedUtc") ?? string.Empty,
                Sha256: sha256,
                FileName: GetString(root, "fileName") is { Length: > 0 } f ? f : DefaultFileName);
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

    /// <summary>True for a 64-char uppercase/lowercase hex SHA256 — the only shape the
    /// download verification can work with. Anything else fails fast as "invalid manifest"
    /// instead of surfacing a confusing hash-mismatch at download time.</summary>
    private static bool IsValidSha256(string sha256) =>
        sha256.Length == 64 && sha256.All(Uri.IsHexDigit);
}