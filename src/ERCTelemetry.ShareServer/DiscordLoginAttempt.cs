using ERCTelemetry.Core.Discord;

namespace ERCTelemetry.ShareServer;

/// <summary>One in-flight server-side Discord login attempt, keyed by its OAuth <c>state</c>.
/// The server exchanges the code when the callback lands (using the client secret from its
/// config) and stores the result here for the app to poll. <see cref="Status"/> is
/// <c>"pending"</c> until the callback finishes the attempt, then <c>"success"</c> (with
/// token + user) or <c>"error"</c> (with a message). <see cref="Nonce"/> is a second
/// random token issued to the app at start and required (X-Login-Nonce) on every status /
/// delete call, so a third party that learns the state cannot poll the result away.
/// <see cref="CodeVerifier"/> is the PKCE verifier generated at start and sent with the
/// code exchange, so a stolen code cannot be redeemed without it.
/// <see cref="InstallId"/> binds the attempt to the installation that started it (S2): a
/// status/delete call must present the SAME install id the start used.
/// Immutable — transitions use <c>with</c>.</summary>
public sealed record DiscordLoginAttempt(
    string State,
    DateTimeOffset CreatedAt,
    string Status,
    string? Nonce = null,
    string? Token = null,
    string? RefreshToken = null,
    DiscordUser? User = null,
    string? Error = null,
    string? CodeVerifier = null,
    string? InstallId = null);
