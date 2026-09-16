using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Share;

/// <summary>Uploads a session (manifest + collision clips) to the share server and
/// returns the public link. Never throws — network errors map to a failed ShareResult.</summary>
public sealed class ShareService
{
    /// <summary>Result of <see cref="ShareSessionAsync"/>.</summary>
    public sealed record ShareResult(bool Success, string? Url, string? Error);

    // MP4 uploads are large — a 5-minute timeout instead of UpdateService's 15 s.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Func<string?> _baseUrl;
    private readonly Func<string?>? _token;
    private readonly ShareTokenResolver? _tokenResolver;

    /// <summary>Feeds the service the settings base URL + token (null/empty → built-in
    /// default / not configured).</summary>
    public ShareService(Func<string?> baseUrl, Func<string?> token)
    {
        _baseUrl = baseUrl;
        _token = token;
    }

    /// <summary>Feeds the service the settings base URL + an auto-resolving token so
    /// Enduser need no manual setup (see <see cref="ShareTokenResolver"/>).</summary>
    public ShareService(Func<string?> baseUrl, ShareTokenResolver tokenResolver)
    {
        _baseUrl = baseUrl;
        _tokenResolver = tokenResolver;
    }

    /// <summary>Uploads the manifest, then each clip (PUT). On any failure the session
    /// is deleted again on the server so no half-uploaded session stays public.</summary>
    public async Task<ShareResult> ShareSessionAsync(
        ShareManifest manifest,
        IReadOnlyList<TelemetryDb.StoredClip> clips,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        // Hoisted so the cancellation handler can best-effort delete a session whose
        // upload was interrupted mid-way (see catch below).
        string? token = null;
        string? ownerSecret = null;
        var baseUrl = "";
        var sessionCreated = false;
        try
        {
            // Token resolution lives inside the try so the documented "never throws"
            // contract holds: a resolver failure (unreachable server, malformed configured
            // URL) maps to a failed ShareResult like any other network error instead of
            // escaping the method.
            token = _tokenResolver is not null
                ? await _tokenResolver.GetTokenAsync(ct).ConfigureAwait(false)
                : _token?.Invoke()?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new ShareResult(false, null,
                    "Kein Share-Token konfiguriert — in den Einstellungen unter „Session teilen“ eintragen.");
            }

            baseUrl = ResolveBaseUrl();
            using var post = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sessions")
            {
                Content = JsonContent.Create(manifest),
            };
            post.Headers.Add(ShareConstants.TokenHeader, token);
            using var postResponse = await _http.SendAsync(post, ct);
            if (postResponse.IsSuccessStatusCode is false)
            {
                return new ShareResult(false, null,
                    postResponse.StatusCode == HttpStatusCode.Conflict
                        ? "Diese Session existiert auf dem Server bereits."
                        : $"Server antwortete {postResponse.StatusCode} beim Anlegen der Session.");
            }

            sessionCreated = true;

            var (url, owner) = await ReadUrlAndOwnerAsync(postResponse, ct);
            if (url is null)
            {
                return new ShareResult(false, null, "Server lieferte keine gültige Session-URL.");
            }

            ownerSecret = owner;

            // The server answers with a relative path (/s/{uid}); the app knows the
            // public base URL it configured, so it builds the full shareable link.
            var link = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : $"{baseUrl}{url}";

            foreach (var clip in clips)
            {
                var fileName = Path.GetFileName(clip.FilePath);
                if (fileName.Length == 0 || !File.Exists(clip.FilePath))
                {
                    await DeleteSessionAsync(baseUrl, manifest.SessionUid, token, ownerSecret, ct);
                    return new ShareResult(false, null, $"Clip-Datei fehlt: {clip.FilePath}");
                }

                var error = await UploadClipAsync(baseUrl, manifest.SessionUid, fileName, clip.FilePath, token, ownerSecret, progress, ct);
                if (error is not null)
                {
                    await DeleteSessionAsync(baseUrl, manifest.SessionUid, token, ownerSecret, ct);
                    return new ShareResult(false, null, $"Clip-Upload {fileName} fehlgeschlagen ({error}).");
                }
            }

            return new ShareResult(true, link, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The doc comment promises no half-uploaded session stays public: a user
            // cancel during a big clip upload would otherwise leave the empty/partial
            // session at /s/{uid} until the retention sweep. Best-effort delete, then
            // rethrow so the caller handles the actual cancellation.
            // sessionCreated is only ever true when the POST succeeded, which requires a
            // valid token, but the compiler can't infer that across the try — guard it.
            if (sessionCreated && token is not null)
            {
                await DeleteSessionAsync(baseUrl, manifest.SessionUid, token, ownerSecret, CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            // An HttpClient timeout (TaskCanceledException with the token NOT cancelled)
            // lands here, not in the OCE catch above — the session would otherwise stay
            // half-public until the retention sweep. Same best-effort cleanup.
            if (sessionCreated && token is not null)
            {
                await DeleteSessionAsync(baseUrl, manifest.SessionUid, token, ownerSecret, CancellationToken.None);
            }

            return new ShareResult(false, null, $"Teilen fehlgeschlagen: {ex.Message}");
        }
    }

    // Uploads one clip. Large files (> 80 MB) are split into parts — Cloudflare Free caps
    // a single request body at 100 MB, so a whole big clip would be cut off mid-stream
    // („Error while copying to a stream"). Each part is its own PUT (?part=N&parts=M); the
    // server assembles them when the last part arrives. Returns null on success, else a
    // short reason to append to the user-facing error.
    private async Task<string?> UploadClipAsync(
        string baseUrl, ulong sessionUid, string fileName, string filePath,
        string token, string? ownerSecret, IProgress<string>? progress, CancellationToken ct)
    {
        var fileLength = new FileInfo(filePath).Length;
        var partCount = ClipUpload.PartCount(fileLength);
        var uri = (int part) => partCount == 1
            ? $"{baseUrl}/api/sessions/{sessionUid}/clips/{Uri.EscapeDataString(fileName)}"
            : $"{baseUrl}/api/sessions/{sessionUid}/clips/{Uri.EscapeDataString(fileName)}?{ClipUpload.PartQuery}={part}&{ClipUpload.PartsQuery}={partCount}";

        for (var part = 0; part < partCount; part++)
        {
            progress?.Report(partCount == 1
                ? $"Lade {fileName} hoch …"
                : $"Lade {fileName} hoch (Teil {part + 1}/{partCount}) …");

            await using var stream = File.OpenRead(filePath);
            await using Stream body = partCount == 1
                ? stream
                : new ClipSubStream(stream, ClipUpload.PartOffset(part, fileLength), ClipUpload.PartLength(part, fileLength));

            using var put = new HttpRequestMessage(HttpMethod.Put, uri(part));
            put.Headers.Add(ShareConstants.TokenHeader, token);
            if (!string.IsNullOrWhiteSpace(ownerSecret))
            {
                put.Headers.Add(ShareConstants.OwnerHeader, ownerSecret);
            }

            put.Content = new StreamContent(body);
            using var putResponse = await _http.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, ct);
            if (putResponse.IsSuccessStatusCode is false)
            {
                return partCount == 1
                    ? $"Server {putResponse.StatusCode}"
                    : $"Server {putResponse.StatusCode} (Teil {part + 1}/{partCount})";
            }
        }

        return null;
    }

    /// <summary>Reads the created session's relative page path and the owner secret from
    /// the server's POST /api/sessions response. Either may be absent on an error body.</summary>
    private static async Task<(string? Url, string? OwnerSecret)> ReadUrlAndOwnerAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return (
                doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null,
                doc.RootElement.TryGetProperty("ownerSecret", out var owner) ? owner.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task DeleteSessionAsync(string baseUrl, ulong uid, string token, string? ownerSecret, CancellationToken ct)
    {
        try
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/sessions/{uid}");
            delete.Headers.Add(ShareConstants.TokenHeader, token);
            if (!string.IsNullOrWhiteSpace(ownerSecret))
            {
                delete.Headers.Add(ShareConstants.OwnerHeader, ownerSecret);
            }

            using var response = await _http.SendAsync(delete, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Best-effort cleanup — the server's retention sweep removes leftovers anyway.
        }
    }

    /// <summary>Settings URL when present, else the built-in default; always without a
    /// trailing slash so appended paths stay well-formed.</summary>
    private string ResolveBaseUrl()
    {
        var fromSettings = _baseUrl();
        var baseUrl = string.IsNullOrWhiteSpace(fromSettings) ? ShareConstants.DefaultBaseUrl : fromSettings!;
        return baseUrl.TrimEnd('/');
    }
}
