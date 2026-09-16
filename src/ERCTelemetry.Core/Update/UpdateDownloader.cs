using System.Security.Cryptography;

namespace ERCTelemetry.Core.Update;

/// <summary>Downloads a single file atomically (via a .part file) and verifies its SHA256
/// against the expected value before moving it into place. Used by the delta update for
/// each changed file and by the full-Setup fallback. The HttpClient is injected so tests
/// can supply a fake handler.</summary>
public sealed class UpdateDownloader
{
    /// <summary>Stream copy chunk (80 KB) — also the progress-report granularity.</summary>
    private const int BufferSize = 81920;

    private readonly HttpClient _http;

    public UpdateDownloader(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="targetPath"/> (atomic via
    /// a .part file) and verifies its SHA256 against <paramref name="expectedSha256"/>.
    /// Throws on network error, hash mismatch or cancellation; the .part file is removed on
    /// any failure so a stale partial download never survives.</summary>
    public async Task<string> DownloadFileAsync(
        string url,
        string targetPath,
        string expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + ".part";

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var sink = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (total > 0)
                    {
                        progress?.Report(100.0 * written / total);
                    }
                }
            }

            await using (var stream = File.OpenRead(partPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) is false)
                {
                    throw new InvalidOperationException(
                        $"SHA256-Prüfung fehlgeschlagen für {url}: erwartet {expectedSha256}, erhalten {actual}.");
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            progress?.Report(100);
            return targetPath;
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    /// <summary>Loads and parses ERCTelemetry.files.json from the update server. Returns null
    /// when the file is missing, unreachable or malformed — the caller then falls back to the
    /// full Setup.exe download. Never throws.</summary>
    public async Task<UpdateFileManifest?> FetchFileManifestAsync(string baseUrl, CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/ERCTelemetry.files.json";
        string? json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return null;
        }

        return UpdateFileManifestParser.Parse(json);
    }

    /// <summary>Best-effort cleanup of a leftover download; a stale .part is harmless and
    /// simply overwritten on the next attempt.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore — nothing to do about a locked file here
        }
    }
}
