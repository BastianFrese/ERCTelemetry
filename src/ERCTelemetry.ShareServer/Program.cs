using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ERCTelemetry.Core.Discord;
using ERCTelemetry.Core.Share;
using ERCTelemetry.Core.TwitchChat;
using ERCTelemetry.ShareServer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Clip uploads are large, but a single request is never bigger than one part
// (ClipUpload.MaxPartBytes — the app splits bigger clips into parts so each request stays
// under the edge server's body cap). Kestrel's default 30 MB limit is too small, so set
// it to the part size; the manifest POST gets its own much smaller per-request cap below.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = ClipUpload.MaxPartBytes);

// DoS guard on the write-facing endpoints: the share and login endpoints accept uploads and
// OAuth handshakes, so they get a generous per-IP fixed window (120 requests/minute — far
// below what a legitimate share or login bursts, but it stops one client hammering the
// server). Public reads (/s/…) are deliberately not rate-limited here.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", ctx =>
    {
        var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(remote, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 120,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
        });
    });
});

var share = builder.Configuration.GetSection("Share");
var token = share["Token"] ?? string.Empty;
var configuredRoot = share["RootPath"];
var rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
    ? Path.Combine(builder.Environment.ContentRootPath, "share")
    : configuredRoot);
var retentionDays = int.TryParse(share["RetentionDays"], out var days) && days > 0 ? days : 30;
// Per-session upload budget, default 2 GiB (ClipUpload.MaxSessionBytes). An operator can
// lower it via Share__LimitSessionBytes; the upload-budget tests use a tiny value to
// exercise the 413 path without multi-GB request bodies. The session-bytes accounting in
// the upload handler counts against this cap.
var maxSessionBytes = long.TryParse(share["LimitSessionBytes"], out var sessionCap) && sessionCap > 0
    ? sessionCap
    : ClipUpload.MaxSessionBytes;
// Server-global upload budget (default 20 GiB = ClipUpload.MaxGlobalBytes), configurable via
// Share__LimitGlobalBytes. Bounds the TOTAL disk across all sessions, so a holder of the
// public upload token who opens unlimited sessions (each with its own maxSessionBytes) still
// cannot fill the disk. The upload-budget tests use a tiny value to exercise the 413 path.
var maxGlobalBytes = long.TryParse(share["LimitGlobalBytes"], out var globalCap) && globalCap > 0
    ? globalCap
    : ClipUpload.MaxGlobalBytes;
// Server-side Twitch OAuth: the server owns the code exchange (with the client secret
// from its config), so the app needs no localhost callback. The redirect URI must be
// registered in dev.twitch.tv; the token/users endpoints are overridable for tests (stub).
var twitchRedirectUri = string.IsNullOrWhiteSpace(share["TwitchRedirectUri"])
    ? TwitchOAuth.RedirectUri
    : share["TwitchRedirectUri"]!;
var twitchTokenEndpoint = string.IsNullOrWhiteSpace(share["TwitchTokenEndpoint"])
    ? TwitchOAuth.TokenEndpoint
    : share["TwitchTokenEndpoint"]!;
var twitchUsersEndpoint = string.IsNullOrWhiteSpace(share["TwitchUsersEndpoint"])
    ? TwitchOAuth.UsersEndpoint
    : share["TwitchUsersEndpoint"]!;
// Twitch does not support PKCE — the authorization-code exchange requires the client
// secret. It lives here in the server config (systemd env), never in the app.
var twitchClientSecret = share["TwitchClientSecret"] ?? string.Empty;
// Server-side Discord OAuth: the server owns the code exchange (with the client secret
// from its config), so the app needs no localhost callback. The redirect URI must be
// registered in the Discord Developer Portal; the token/users endpoints are overridable
// for tests (stub).
var discordRedirectUri = string.IsNullOrWhiteSpace(share["DiscordRedirectUri"])
    ? DiscordOAuth.RedirectUri
    : share["DiscordRedirectUri"]!;
var discordTokenEndpoint = string.IsNullOrWhiteSpace(share["DiscordTokenEndpoint"])
    ? DiscordOAuth.TokenEndpoint
    : share["DiscordTokenEndpoint"]!;
var discordUsersEndpoint = string.IsNullOrWhiteSpace(share["DiscordUsersEndpoint"])
    ? DiscordOAuth.UsersEndpoint
    : share["DiscordUsersEndpoint"]!;
var discordClientId = share["DiscordClientId"] ?? string.Empty;
var discordClientSecret = share["DiscordClientSecret"] ?? string.Empty;

// Session owner secrets (session uid → secret): issued at POST /api/sessions and required
// (X-Session-Owner) for every mutation of that session — clip uploads and the DELETE. A
// session's owner is whoever created it, so a holder of the public upload token cannot
// overwrite, append to or delete someone else's session. In-memory by design: the server
// only needs it per process; a restart makes pre-existing sessions immutable (public page
// GET stays up; the retention sweep removes them) instead of loosening the check.
var sessionOwners = new ConcurrentDictionary<ulong, string>();

// Uploaded bytes per session (uid → total). Bounds how much disk a single session can
// occupy across the many chunked part requests an attacker can fire — a per-request body
// cap alone still allows 4096 × 80 MB. Reset on session create, removed on DELETE.
var sessionBytes = new ConcurrentDictionary<ulong, long>();

// Server-global bytes actually claimed across all sessions (see ClipUpload.MaxGlobalBytes).
// Updated atomically via Interlocked in the ClipUpload helpers; decremented when a session
// is removed so a freed budget is reused.
long globalClaimedBytes = 0;

// Claims bytes against both budgets after a copy. The global claim comes first and is
// refunded if the per-session claim fails, keeping the two counters exactly consistent
// across the two independently-atomic CAS calls (H1 / MEDIUM-1).
bool ClaimBytes(ulong uid, long bytes)
{
    if (!ClipUpload.TryClaimGlobalBytes(ref globalClaimedBytes, bytes, maxGlobalBytes))
    {
        return false;
    }

    if (ClipUpload.TryClaimSessionBytes(sessionBytes, uid, bytes, maxSessionBytes))
    {
        return true;
    }

    ClipUpload.ReleaseGlobalBytes(ref globalClaimedBytes, bytes);
    return false;
}

// Returns one reaped orphaned part's bytes to the budgets: the per-session entry is reduced
// by exactly the amount it actually held (TryReleaseSessionBytes), and only that amount is
// released from the server-global counter. Releasing exactly the released amount keeps the
// two counters consistent in every case: a DELETE that already removed the entry releases
// nothing here (no double-count), and a session reset to 0 by a POST re-create — while its
// old stale parts are still on disk — releases nothing when the sweep reaps them, because
// the reconcile already returned those bytes to the global budget. Wired as the sweep's
// onOrphanPartReaped callback (HIGH): files the sweep deletes must stop counting against the
// budgets or the server drifts toward a permanent 413.
void RefundOrphanPart(ulong uid, long bytes)
{
    var released = ClipUpload.TryReleaseSessionBytes(sessionBytes, uid, bytes);
    if (released > 0)
    {
        ClipUpload.ReleaseGlobalBytes(ref globalClaimedBytes, released);
    }
}

// Removes a session's in-memory ownership and byte accounting, returning its claimed bytes
// to the global budget. Used by DELETE, by the POST reconciler when it finds a uid the
// retention sweep already cleaned off disk, and directly as the sweep's onSessionReleased
// callback so an expired session's claimed bytes stop counting the moment its files vanish.
void ReleaseSession(ulong uid)
{
    sessionOwners.TryRemove(uid, out _);
    if (sessionBytes.TryRemove(uid, out var claimed) && claimed > 0)
    {
        ClipUpload.ReleaseGlobalBytes(ref globalClaimedBytes, claimed);
    }
}

// The retention sweep (ShareCleanupService) runs on its own timer and deletes files on
// disk. The callbacks keep that disk sweep matched with the in-memory budget accounting:
// bytes that no longer occupy disk are returned to the caps instead of leaking.
builder.Services.AddHostedService(sp =>
    new ShareCleanupService(rootPath, retentionDays, sp.GetRequiredService<ILogger<ShareCleanupService>>(),
        RefundOrphanPart, ReleaseSession));

var app = builder.Build();
Directory.CreateDirectory(rootPath);

// Forwarded headers are only honored when the operator explicitly opts in
// (Share__ForwardedHeaders=true) — the server runs behind a proxy (Cloudflare/nginx) that
// sets X-Forwarded-*; on a plain HTTP deployment (incl. all HTTP tests) leaving the
// default off keeps the app from trusting spoofable client-sent headers.
if (bool.TryParse(share["ForwardedHeaders"], out var trustForwarded) && trustForwarded)
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        // Loopback is the default trust for proxies/networks; the option exists so an
        // operator behind a real proxy can extend it (KnownNetworks/KnownProxies).
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });
}

app.UseRateLimiter();

// The OAuth client secrets live only in the server config (env), never in committed
// appsettings files. A missing one does not stop the server (share/upload still work) —
// but the Discord/Twitch login exchange would fail at runtime, so it gets a visible
// warning at startup instead of a confusing dead login button later.
if (string.IsNullOrWhiteSpace(twitchClientSecret))
{
    app.Logger.LogWarning("Share__TwitchClientSecret (TwitchClientSecret) fehlt — der Twitch-Login-Code-Austausch wird fehlschlagen.");
}

if (string.IsNullOrWhiteSpace(discordClientSecret))
{
    app.Logger.LogWarning("Share__DiscordClientSecret (DiscordClientSecret) fehlt — der Discord-Login-Code-Austausch wird fehlschlagen.");
}

// In-flight Twitch login attempts (state → attempt). In-memory: a server restart mid-login
// just means the streamer retries. Entries expire after 5 minutes (purged on each start);
// a successful status poll removes the attempt so the token is delivered exactly once.
var twitchLogins = new ConcurrentDictionary<string, TwitchLoginAttempt>();

// In-flight Discord login attempts (state → attempt). Same lifecycle as the Twitch logins.
var discordLogins = new ConcurrentDictionary<string, DiscordLoginAttempt>();

// Token check: constant-time comparison, no early exit on a wrong first byte.
bool Authorized(HttpRequest request) =>
    token.Length > 0 &&
    request.Headers.TryGetValue(ShareConstants.TokenHeader, out var provided) &&
    Program.FixedTimeEquals(token, provided.ToString());

// Owner check: the X-Session-Owner header must match the secret the session creator was
// given. Constant-time; a missing header compares against the empty string and fails.
bool SessionOwned(HttpRequest request, ulong uid) =>
    sessionOwners.TryGetValue(uid, out var owner) &&
    request.Headers.TryGetValue(ShareConstants.OwnerHeader, out var provided) &&
    Program.FixedTimeEquals(owner, provided.ToString());

// GET /api/config — public bootstrap: tells the app the current upload token so Enduser
// can share a session without configuring anything. Public by design: the same token ships
// in the app as the built-in fallback, so this endpoint exposes nothing the app binary does
// not already contain — and it lets the operator rotate the token without an app update.
app.MapGet("/api/config", () => Results.Ok(new { shareToken = token }));

// POST /api/sessions — create a session from the manifest JSON, answer with the
// relative page path (/s/{uid}) and the owner secret the app must send back on every
// mutation of this session. Creating over an existing session is refused (409): a session
// belongs to its creator — overwriting someone else's session would be a public-token DoS.
app.MapPost("/api/sessions", async (HttpContext ctx) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    // A manifest is small JSON — cap this request well below the clip-body limit so a
    // giant (or maliciously padded) manifest cannot force a huge deserialization.
    if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { } maxBody)
    {
        maxBody.MaxRequestBodySize = 1024 * 1024; // 1 MB
    }

    ShareManifest? manifest;
    try
    {
        manifest = await ctx.Request.ReadFromJsonAsync<ShareManifest>();
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid manifest JSON.");
    }

    if (manifest is null)
    {
        return Results.BadRequest("Missing manifest.");
    }

    var uid = manifest.SessionUid;
    var manifestPath = SharePaths.ManifestPath(rootPath, uid);
    if (sessionOwners.ContainsKey(uid) && !File.Exists(manifestPath))
    {
        // The retention sweep deleted this session's files but its ownership stayed in
        // memory (the sweep only touches disk). Free the stale entry and its byte
        // accounting so the uid can be shared again (LOW 4) — ReleaseSession returns the
        // claimed bytes to the global budget.
        ReleaseSession(uid);
    }

    if (sessionOwners.ContainsKey(uid) ||
        File.Exists(manifestPath))
    {
        // 409, not 200-with-overwrite: the session is someone's (or an orphan of a
        // crashed create) — a second share of the same game session goes through DELETE
        // first if it needs to be replaced.
        return Results.Conflict("Session exists already.");
    }

    var dir = SharePaths.SessionDir(rootPath, uid);
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(
        SharePaths.ManifestPath(rootPath, uid),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    // Server-issued owner secret (48 hex chars from 24 random bytes) — the app echoes it
    // via X-Session-Owner on clip uploads and the session delete.
    var ownerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    // TryAdd instead of index-assignment (LOW, TOCTOU): two concurrent POSTs of the same uid
    // must not last-writer-win the ownership — the loser gets a 409, and its manifest write
    // above was for the same game session, so nothing is corrupted.
    if (!sessionOwners.TryAdd(uid, ownerSecret))
    {
        return Results.Conflict("Session exists already.");
    }

    // MEDIUM: a clip PUT that cleared the owner check before a concurrent DELETE can still
    // land its byte claim afterwards, stranding sessionBytes[uid] that nothing will ever
    // release. Refund whatever residue is currently claimed before seeding the fresh entry —
    // overwriting the counter without a refund would leak those bytes out of the global
    // budget permanently. Any claim that lands after this belongs to the live session: its
    // owner secret is not out yet, and an in-flight old-owner PUT now fails the owner check.
    if (sessionBytes.TryRemove(uid, out var stranded) && stranded > 0)
    {
        ClipUpload.ReleaseGlobalBytes(ref globalClaimedBytes, stranded);
    }

    sessionBytes.TryAdd(uid, 0);

    var url = $"/s/{uid}";
    return Results.Created(url, new { url, ownerSecret });
}).RequireRateLimiting("api");

// PUT /api/sessions/{uid}/clips/{fileName} — stream a clip MP4 to disk (no buffering).
// Large clips arrive chunked: the app splits files above ~80 MB into parts so every single
// request body stays under the 100 MB cap of the edge server in front (Cloudflare Free).
// Each part is its own PUT (?part=N&parts=M); the last part assembles the parts into the
// final MP4 and removes the .part* scratch files. A PUT without part/parts stays the
// direct single-shot upload.
app.MapPut("/api/sessions/{uid}/clips/{fileName}", async (HttpContext ctx, string uid, string fileName) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    if (!SharePaths.IsValidUid(uid) || !SharePaths.IsValidFileName(fileName))
    {
        return Results.BadRequest("Invalid uid or file name.");
    }

    var sessionUid = ulong.Parse(uid);
    if (!SessionOwned(ctx.Request, sessionUid))
    {
        // 403, not 401: the caller is a valid uploader (share token) but not the session
        // creator — appending a clip to someone else's session is the ownership violation.
        // A bare StatusCode, not Results.Forbid(): that one needs an ASP.NET auth scheme,
        // which this server does not configure (its token check is manual).
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    // Explicit Content-Length check: a body known to exceed the part cap is rejected with
    // 413 before any byte is read, instead of tripping Kestrel's limit mid-upload.
    if (ctx.Request.ContentLength > ClipUpload.MaxPartBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    // The per-session budget (see ClipUpload.MaxSessionBytes) bounds the disk one session
    // can occupy across the many chunked part requests of a single clip. This pre-check
    // only covers a body with a declared length; the copy path below recounts the bytes
    // that are actually written, which is what closes the chunked/lying-length bypass.
    var bodyBytes = ctx.Request.ContentLength ?? 0;
    if (sessionBytes.TryGetValue(sessionUid, out var used) &&
        used + bodyBytes > maxSessionBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var sessionDir = SharePaths.SessionDir(rootPath, sessionUid);
    Directory.CreateDirectory(sessionDir);

    var hasPart = ctx.Request.Query.TryGetValue(ClipUpload.PartQuery, out var partValue);
    var hasParts = ctx.Request.Query.TryGetValue(ClipUpload.PartsQuery, out var partsValue);

    // No chunk parameters → direct single-shot upload (unchanged behaviour).
    if (!hasPart && !hasParts)
    {
        var clipPath = SharePaths.ClipPath(rootPath, sessionUid, fileName);
        var copyOk = false;
        try
        {
            await using (var output = File.Create(clipPath))
            {
                var result = await ClipUpload.CopyWithinBudgetAsync(
                    ctx.Request.Body, output, maxSessionBytes, ctx.RequestAborted);
                // The copy already stopped at the per-session budget; claiming the exact byte
                // count against both counters is the atomic part — concurrent part uploads
                // cannot collectively overshoot the caps, per-session (sessionBytes) or
                // server-global (globalClaimedBytes). ClaimBytes refunds the global claim if
                // the session claim fails, so both stay consistent (H1 / MEDIUM-1).
                copyOk = result.Ok && ClaimBytes(sessionUid, result.Bytes);
            }
        }
        catch
        {
            TryDelete(clipPath);
            throw;
        }

        if (!copyOk)
        {
            // Budget exceeded: the partial file was not counted against the session budget,
            // so it must not stay behind — a retry has to start clean instead of appending
            // to a stub.
            TryDelete(clipPath);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // fileName is already validated, but escaping keeps the Location header
        // well-formed if it ever contains characters that are legal in a file name but
        // not in a URL (spaces).
        return Results.Created($"/s/{uid}/clips/{Uri.EscapeDataString(fileName)}", null);
    }

    // Chunk parameters must come as a pair, parse cleanly and be in range.
    if (hasPart != hasParts ||
        !int.TryParse(partValue.ToString(), out var part) ||
        !int.TryParse(partsValue.ToString(), out var parts) ||
        parts is < 1 or > ClipUpload.MaxParts ||
        part is < 0 ||
        part >= parts)
    {
        // Keep the message fixed — echoing the query values would reflect attacker input
        // back into the response body (LOW-NOTE).
        return Results.BadRequest("Invalid part range.");
    }

    // Same budget-aware, aborting copy as the single-shot path — a chunked part without a
    // Content-Length is counted by its real size, and the claim is atomic against the
    // session counter so parallel parts cannot collectively exceed the cap (H1).
    var partPath = SharePaths.ClipPath(rootPath, sessionUid, $"{fileName}.part{part}");
    var partCopyOk = false;
    try
    {
        await using (var partOut = File.Create(partPath))
        {
            var result = await ClipUpload.CopyWithinBudgetAsync(
                ctx.Request.Body, partOut, maxSessionBytes, ctx.RequestAborted);
            partCopyOk = result.Ok && ClaimBytes(sessionUid, result.Bytes);
        }
    }
    catch
    {
        TryDelete(partPath);
        throw;
    }

    if (!partCopyOk)
    {
        TryDelete(partPath);
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    // Last part: check every part arrived, assemble them into the final MP4 and delete the
    // scratch files. Missing parts are a client/proxy failure — reject instead of serving a
    // half-assembled file.
    if (part == parts - 1)
    {
        for (var i = 0; i < parts; i++)
        {
            if (!File.Exists(SharePaths.ClipPath(rootPath, sessionUid, $"{fileName}.part{i}")))
            {
                return Results.BadRequest($"Missing upload part {i}.");
            }
        }

        await using (var output = File.Create(SharePaths.ClipPath(rootPath, sessionUid, fileName)))
        {
            for (var i = 0; i < parts; i++)
            {
                await using var partStream = File.OpenRead(SharePaths.ClipPath(rootPath, sessionUid, $"{fileName}.part{i}"));
                await partStream.CopyToAsync(output);
            }
        }

        for (var i = 0; i < parts; i++)
        {
            File.Delete(SharePaths.ClipPath(rootPath, sessionUid, $"{fileName}.part{i}"));
        }
    }

    return Results.Created($"/s/{uid}/clips/{Uri.EscapeDataString(fileName)}", null);
}).RequireRateLimiting("api");

// DELETE /api/sessions/{uid} — remove a session (failed-upload cleanup from the app).
// Gated on the owner secret: without it, a share-token holder could delete anyone's
// session. Idempotent — deleting an already-gone session still answers NoContent.
app.MapDelete("/api/sessions/{uid}", (HttpContext ctx, string uid) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    if (!SharePaths.IsValidUid(uid))
    {
        return Results.BadRequest("Invalid uid.");
    }

    var sessionUid = ulong.Parse(uid);
    if (!SessionOwned(ctx.Request, sessionUid))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var dir = SharePaths.SessionDir(rootPath, sessionUid);
    if (Directory.Exists(dir))
    {
        Directory.Delete(dir, recursive: true);
    }

    ReleaseSession(sessionUid);
    return Results.NoContent();
}).RequireRateLimiting("api");

// GET /s/{uid} — the public session page. Root-relative clip srcs (empty baseUrl)
// resolve against the page origin, so they work behind an HTTPS reverse proxy too.
app.MapGet("/s/{uid}", async (string uid) =>
{
    if (!SharePaths.IsValidUid(uid))
    {
        return Results.NotFound();
    }

    var manifestPath = SharePaths.ManifestPath(rootPath, ulong.Parse(uid));
    if (!File.Exists(manifestPath))
    {
        return Results.NotFound();
    }

    ShareManifest? manifest;
    try
    {
        manifest = JsonSerializer.Deserialize<ShareManifest>(await File.ReadAllTextAsync(manifestPath));
    }
    catch (JsonException)
    {
        return Results.NotFound();
    }

    if (manifest is null)
    {
        return Results.NotFound();
    }

    return Results.Content(SharePageRenderer.Render(manifest, ""), "text/html; charset=utf-8");
});

// GET /s/{uid}/clips/{fileName} — the MP4 with Range support (seeking in the player).
app.MapGet("/s/{uid}/clips/{fileName}", (string uid, string fileName) =>
{
    if (!SharePaths.IsValidUid(uid) || !SharePaths.IsValidFileName(fileName))
    {
        return Results.NotFound();
    }

    var path = SharePaths.ClipPath(rootPath, ulong.Parse(uid), fileName);
    return File.Exists(path)
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound();
});

// POST /twitch/login/start — the app asks the server to start a Twitch login. The server
// generates a state token, stores the attempt and answers with the authorize URL the app
// opens in the browser. The client secret stays on the server (config), never in the app.
app.MapPost("/twitch/login/start", (HttpContext ctx) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    PurgeExpiredLogins();
    var state = Guid.NewGuid().ToString("N");
    var nonce = Guid.NewGuid().ToString("N");
    twitchLogins[state] = new TwitchLoginAttempt(state, DateTimeOffset.UtcNow, "pending", nonce);
    var authorizeUrl = TwitchOAuth.BuildAuthorizeUrl(
        TwitchOAuth.ClientId, twitchRedirectUri, TwitchOAuth.ChatScopes, state);
    return Results.Ok(new { state, authorizeUrl, nonce });
}).RequireRateLimiting("api");

// GET /twitch/callback — OAuth redirect target for the Twitch login (Twitch requires
// HTTPS redirect URIs, so the callback cannot land on localhost directly). The server
// completes the flow: it exchanges the code with the client secret from its config,
// stores the result keyed by state and shows a result page. The app polls
// /twitch/login/status for that result. Public by design: the browser lands here
// without a token, and the code is single-use.
app.MapGet("/twitch/callback", async (string? code, string? error, string? state, ILogger<Program> logger) =>
{
    if (string.IsNullOrEmpty(state) || !twitchLogins.TryGetValue(state, out var attempt))
    {
        return Results.Content(
            LoginPage("Login abgelaufen oder ungültig", "Bitte in der App erneut versuchen."),
            "text/html; charset=utf-8");
    }

    if (!string.IsNullOrEmpty(error))
    {
        // The provider echoes two kinds of value here: the known user cancel (access_denied)
        // and arbitrary provider error strings. Only the known cancel is surfaced by name;
        // anything else stores the generic message — never raw provider input, which flows
        // back to the app verbatim via the status poll (M2). The error is only written while
        // the attempt is still pending: a parallel callback that already stored the success
        // holds the definitive result and must not be overwritten (LOW 3, same as the
        // exchange-error path below).
        var message = error == "access_denied" ? "access_denied" : "Login fehlgeschlagen";
        if (twitchLogins.TryGetValue(state, out var latest) && latest.Status == "pending")
        {
            // Atomic write: the TryUpdate compare-and-swap re-checks the stored attempt, so a
            // success that landed between the read and this write is never clobbered (LOW 3).
            twitchLogins.TryUpdate(state, attempt with { Status = "error", Error = message }, latest);
        }

        return Results.Content(
            LoginPage(message == "access_denied" ? "Login abgebrochen" : "Login fehlgeschlagen",
                "Du kannst dieses Fenster schließen."),
            "text/html; charset=utf-8");
    }

    if (string.IsNullOrEmpty(code))
    {
        return Results.Content(
            LoginPage("Login fehlgeschlagen", "Kein Code empfangen."),
            "text/html; charset=utf-8");
    }

    // Twitch's known double-redirect bug sends the same code twice. The first delivery
    // exchanges it; a second one (already-redeemed code → "Invalid code") must not
    // overwrite the result — just re-show the page.
    if (attempt.Status is "success" or "error")
    {
        return Results.Content(
            LoginPage(attempt.Status == "success" ? "Login erfolgreich ✓" : "Login fehlgeschlagen",
                "Du kannst dieses Fenster schließen."),
            "text/html; charset=utf-8");
    }

    try
    {
        var token = await TwitchOAuth.ExchangeCodeAsync(
            TwitchOAuth.ClientId, twitchClientSecret, code, twitchRedirectUri,
            tokenEndpoint: twitchTokenEndpoint);
        var user = await TwitchOAuth.GetUserAsync(
            token.AccessToken, TwitchOAuth.ClientId, usersEndpoint: twitchUsersEndpoint);
        // A provider-issued 200 is the definitive result — write it unconditionally. The
        // error path below re-reads before writing, so a losing parallel callback (same
        // code, already redeemed → "invalid code") cannot overwrite this success (LOW 3).
        twitchLogins[state] = attempt with
        {
            Status = "success",
            Token = token.AccessToken,
            RefreshToken = token.RefreshToken,
            User = user,
        };
        return Results.Content(
            LoginPage("Login erfolgreich ✓", "Du kannst dieses Fenster schließen."),
            "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        // The OAuth helpers embed the raw provider response in ex.Message — never log that
        // verbatim (a provider echo could inject fake log lines); sanitize it first (LOW 6).
        logger.LogError("Twitch login exchange failed for state {State}: {Error}",
            state, SanitizeLogText(ex.Message));
        // No raw ex.Message in the stored error (it is returned to the app via the status
        // poll) — the app only needs to know the login failed; the details stay in the log.
        // Only write the error if the attempt is still pending: a parallel callback that
        // already stored the success holds the definitive result and must not be
        // overwritten (LOW 3).
        if (twitchLogins.TryGetValue(state, out var latest) && latest.Status == "pending")
        {
            // Atomic write (LOW 3): the compare-and-swap re-checks that the stored attempt is
            // still the unchanged pending one, so a parallel success is never overwritten.
            twitchLogins.TryUpdate(state, attempt with { Status = "error", Error = "Login fehlgeschlagen" }, latest);
        }

        return Results.Content(
            LoginPage("Login fehlgeschlagen", "Bitte in der App erneut versuchen."),
            "text/html; charset=utf-8");
    }
});

// GET /twitch/login/status?state=… — the app polls this until the callback finished the
// attempt. Returns pending / success (token + user) / error (message); 404 when the state
// is unknown (expired, never started or already delivered). The X-Login-Nonce header
// (issued by the start call) binds the poll to the app session that started the login, so
// a third party that learned the state cannot fetch the OAuth result.
app.MapGet("/twitch/login/status", (HttpContext ctx, string state) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    if (!ctx.Request.Headers.TryGetValue(ShareConstants.LoginNonceHeader, out var nonce))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (!twitchLogins.TryGetValue(state, out var attempt))
    {
        return Results.NotFound();
    }

    if (!Program.FixedTimeEquals(attempt.Nonce ?? string.Empty, nonce.ToString()))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    switch (attempt.Status)
    {
        case "success":
            // The token is single-use: deliver it exactly once — remove the attempt before
            // answering, so a second poll of the same state gets a 404 ("notfound" in the
            // app) instead of the token a second time.
            if (!twitchLogins.TryRemove(state, out _))
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                status = "success",
                token = attempt.Token,
                refreshToken = attempt.RefreshToken,
                user = attempt.User is null ? null : new { attempt.User.Id, attempt.User.Login, attempt.User.DisplayName },
            });
        case "error":
            return Results.Ok(new { status = "error", message = attempt.Error });
        default:
            return Results.Ok(new { status = "pending" });
    }
}).RequireRateLimiting("api");

// DELETE /twitch/login/{state} — the app removes the attempt once it has the result (the
// token is single-use; the 5-minute expiry cleans up leftovers). Requires the X-Login-Nonce
// before removing, so a third party that learned the state cannot delete the attempt a
// streamer is mid-flight on. Unknown state is still an idempotent NoContent.
app.MapDelete("/twitch/login/{state}", (HttpContext ctx, string state) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    if (ctx.Request.Headers.TryGetValue(ShareConstants.LoginNonceHeader, out var nonce) &&
        twitchLogins.TryGetValue(state, out var attempt) &&
        Program.FixedTimeEquals(attempt.Nonce ?? string.Empty, nonce.ToString()))
    {
        twitchLogins.TryRemove(state, out _);
    }

    return Results.NoContent();
}).RequireRateLimiting("api");

// POST /discord/login/start — the app asks the server to start a Discord login. The server
// generates a state token, stores the attempt and answers with the authorize URL the app
// opens in the browser. The client secret stays on the server (config), never in the app.
app.MapPost("/discord/login/start", (HttpContext ctx) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    PurgeExpiredDiscordLogins();
    var state = Guid.NewGuid().ToString("N");
    var nonce = Guid.NewGuid().ToString("N");
    // PKCE (RFC 7636): the verifier is generated here and kept on the attempt; its S256
    // challenge goes into the authorize URL. Discord then binds the code to the verifier,
    // so a code a third party intercepts off the callback cannot be redeemed without it —
    // even though (like Twitch) the callback is public by design.
    var codeVerifier = DiscordOAuth.NewCodeVerifier();
    discordLogins[state] = new DiscordLoginAttempt(state, DateTimeOffset.UtcNow, "pending", nonce)
        with { CodeVerifier = codeVerifier };
    var authorizeUrl = DiscordOAuth.BuildAuthorizeUrl(
        discordClientId, discordRedirectUri, DiscordOAuth.Scopes, state,
        codeChallenge: DiscordOAuth.CodeChallenge(codeVerifier));
    return Results.Ok(new { state, authorizeUrl, nonce });
}).RequireRateLimiting("api");

// GET /discord/callback — OAuth redirect target for the Discord login (Discord requires
// HTTPS redirect URIs, so the callback cannot land on localhost directly). The server
// completes the flow: it exchanges the code with the client secret from its config,
// stores the result keyed by state and shows a result page. The app polls
// /discord/login/status for that result. Public by design: the browser lands here
// without a token, and the code is single-use.
app.MapGet("/discord/callback", async (string? code, string? error, string? state, ILogger<Program> logger) =>
{
    if (string.IsNullOrEmpty(state) || !discordLogins.TryGetValue(state, out var attempt))
    {
        return Results.Content(
            LoginPage("Login abgelaufen oder ungültig", "Bitte in der App erneut versuchen."),
            "text/html; charset=utf-8");
    }

    if (!string.IsNullOrEmpty(error))
    {
        // Same policy as the Twitch callback: only the known user cancel (access_denied) is
        // surfaced by name, anything else stores the generic message — never raw provider
        // input (M2) — and the error is only written while the attempt is still pending so a
        // parallel callback that already stored the success is not overwritten (LOW 3).
        var message = error == "access_denied" ? "access_denied" : "Login fehlgeschlagen";
        if (discordLogins.TryGetValue(state, out var latest) && latest.Status == "pending")
        {
            // Atomic write: the TryUpdate compare-and-swap re-checks the stored attempt, so a
            // success that landed between the read and this write is never clobbered (LOW 3).
            discordLogins.TryUpdate(state, attempt with { Status = "error", Error = message }, latest);
        }

        return Results.Content(
            LoginPage(message == "access_denied" ? "Login abgebrochen" : "Login fehlgeschlagen",
                "Du kannst dieses Fenster schließen."),
            "text/html; charset=utf-8");
    }

    if (string.IsNullOrEmpty(code))
    {
        return Results.Content(
            LoginPage("Login fehlgeschlagen", "Kein Code empfangen."),
            "text/html; charset=utf-8");
    }

    // Discord's known double-redirect bug sends the same code twice. The first delivery
    // exchanges it; a second one (already-redeemed code → "Invalid code") must not
    // overwrite the result — just re-show the page.
    if (attempt.Status is "success" or "error")
    {
        return Results.Content(
            LoginPage(attempt.Status == "success" ? "Login erfolgreich ✓" : "Login fehlgeschlagen",
                "Du kannst dieses Fenster schließen."),
            "text/html; charset=utf-8");
    }

    try
    {
        var token = await DiscordOAuth.ExchangeCodeAsync(
            discordClientId, discordClientSecret, code, discordRedirectUri,
            codeVerifier: attempt.CodeVerifier,
            tokenEndpoint: discordTokenEndpoint);
        var user = await DiscordOAuth.GetUserAsync(
            token.AccessToken, usersEndpoint: discordUsersEndpoint);
        // A provider-issued 200 is the definitive result — write it unconditionally. The
        // error path below re-reads before writing, so a losing parallel callback (same
        // code, already redeemed → "invalid grant") cannot overwrite this success (LOW 3).
        discordLogins[state] = attempt with
        {
            Status = "success",
            Token = token.AccessToken,
            RefreshToken = token.RefreshToken,
            User = user,
        };
        return Results.Content(
            LoginPage("Login erfolgreich ✓", "Du kannst dieses Fenster schließen."),
            "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        // The OAuth helpers embed the raw provider response in ex.Message — never log that
        // verbatim (a provider echo could inject fake log lines); sanitize it first (LOW 6).
        logger.LogError("Discord login exchange failed for state {State}: {Error}",
            state, SanitizeLogText(ex.Message));
        // No raw ex.Message in the stored error (it is returned to the app via the status
        // poll) — the app only needs to know the login failed; the details stay in the log.
        // Only write the error if the attempt is still pending: a parallel callback that
        // already stored the success holds the definitive result and must not be
        // overwritten (LOW 3).
        if (discordLogins.TryGetValue(state, out var latest) && latest.Status == "pending")
        {
            // Atomic write (LOW 3): the compare-and-swap re-checks that the stored attempt is
            // still the unchanged pending one, so a parallel success is never overwritten.
            discordLogins.TryUpdate(state, attempt with { Status = "error", Error = "Login fehlgeschlagen" }, latest);
        }

        return Results.Content(
            LoginPage("Login fehlgeschlagen", "Bitte in der App erneut versuchen."),
            "text/html; charset=utf-8");
    }
});

// GET /discord/login/status?state=… — the app polls this until the callback finished the
// attempt. Returns pending / success (token + user) / error (message); 404 when the state
// is unknown (expired, never started or already delivered). The X-Login-Nonce header
// (issued by the start call) binds the poll to the app session that started the login, so
// a third party that learned the state cannot fetch the OAuth result.
app.MapGet("/discord/login/status", (HttpContext ctx, string state) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    if (!ctx.Request.Headers.TryGetValue(ShareConstants.LoginNonceHeader, out var nonce))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (!discordLogins.TryGetValue(state, out var attempt))
    {
        return Results.NotFound();
    }

    if (!Program.FixedTimeEquals(attempt.Nonce ?? string.Empty, nonce.ToString()))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    switch (attempt.Status)
    {
        case "success":
            // The token is single-use: deliver it exactly once — remove the attempt before
            // answering, so a second poll of the same state gets a 404 ("notfound" in the
            // app) instead of the token a second time.
            if (!discordLogins.TryRemove(state, out _))
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                status = "success",
                token = attempt.Token,
                refreshToken = attempt.RefreshToken,
                user = attempt.User is null ? null : new { attempt.User.Id, attempt.User.Username, attempt.User.GlobalName },
            });
        case "error":
            return Results.Ok(new { status = "error", message = attempt.Error });
        default:
            return Results.Ok(new { status = "pending" });
    }
}).RequireRateLimiting("api");

// DELETE /discord/login/{state} — the app removes the attempt once it has the result (the
// token is single-use; the 5-minute expiry cleans up leftovers). Requires the X-Login-Nonce
// before removing, so a third party that learned the state cannot delete the attempt a user
// is mid-flight on. Unknown state is still an idempotent NoContent.
app.MapDelete("/discord/login/{state}", (HttpContext ctx, string state) =>
{
    if (!Authorized(ctx.Request))
    {
        return Results.Unauthorized();
    }

    if (ctx.Request.Headers.TryGetValue(ShareConstants.LoginNonceHeader, out var nonce) &&
        discordLogins.TryGetValue(state, out var attempt) &&
        Program.FixedTimeEquals(attempt.Nonce ?? string.Empty, nonce.ToString()))
    {
        discordLogins.TryRemove(state, out _);
    }

    return Results.NoContent();
}).RequireRateLimiting("api");

// Login attempts expire after 5 minutes — purged lazily on each new start so a stale
// state (streamer never finished authorizing) cannot linger forever.
void PurgeExpiredLogins()
{
    var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
    foreach (var (state, attempt) in twitchLogins)
    {
        if (attempt.CreatedAt < cutoff)
        {
            twitchLogins.TryRemove(state, out _);
        }
    }
}

// Discord login attempts expire after 5 minutes — purged lazily on each new start so a
// stale state (user never finished authorizing) cannot linger forever.
void PurgeExpiredDiscordLogins()
{
    var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
    foreach (var (state, attempt) in discordLogins)
    {
        if (attempt.CreatedAt < cutoff)
        {
            discordLogins.TryRemove(state, out _);
        }
    }
}

// Small result page shown in the browser after the OAuth callback lands.
static string LoginPage(string title, string message) =>
    $"<html><body style='font-family:sans-serif'><h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

// Log lines are records separated by newlines — a provider error body that echoes
// attacker-shaped text must not be able to inject fake log lines (LOW 6). Strip control
// characters and cap the length so one malformed response cannot flood the log.
static string SanitizeLogText(string? text)
{
    if (string.IsNullOrEmpty(text))
    {
        return string.Empty;
    }

    var sb = new StringBuilder(text.Length);
    foreach (var c in text)
    {
        if (sb.Length >= 1024)
        {
            break;
        }

        if (!char.IsControl(c))
        {
            sb.Append(c);
        }
    }

    return sb.ToString();
}

// Best-effort file delete used by the upload handler to remove a partial file after a
// budget-aborted or failed copy. The FileStream is disposed before this runs (the copy
// uses a using block), so the delete succeeds on Windows.
static void TryDelete(string path)
{
    try
    {
        File.Delete(path);
    }
    catch
    {
        // Best effort — a leftover orphan is better than failing a request over it.
    }
}

app.Run();

/// <summary>Entry-point marker for WebApplicationFactory in the server tests.</summary>
public partial class Program
{
    /// <summary>Constant-time token comparison (no early exit on a wrong first byte).</summary>
    internal static bool FixedTimeEquals(string expected, string provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
