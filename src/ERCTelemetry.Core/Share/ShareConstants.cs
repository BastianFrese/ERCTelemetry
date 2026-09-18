namespace ERCTelemetry.Core.Share;

/// <summary>Shared constants of the session-sharing feature (app + share server).</summary>
public static class ShareConstants
{
    /// <summary>Built-in default when settings hold no override URL.</summary>
    public const string DefaultBaseUrl = "https://telemetrie.erdi-erc.de";

    /// <summary>Built-in upload-token fallback matching the share server's deployed
    /// Share__Token, so Enduser can share sessions with zero setup. The app prefers an
    /// explicitly configured token (rotation), then the server's public GET /api/config
    /// answer, and falls back to this value only when the endpoint is unreachable or
    /// answers without a token. Public by design: the token ships in the app for
    /// zero-setup sharing, so it protects against uploads without the app — not against
    /// extracting it (it is also readable via /api/config). MUST be kept in sync with the
    /// server's Share__Token on release, otherwise the fallback only works while the
    /// server's token equals this value.</summary>
    public const string DefaultToken = "b654aafd228e71c987832f790c3c710c2272212da33fdab18f55d2609dca0592";

    /// <summary>Header carrying the upload token on all /api/* requests.</summary>
    public const string TokenHeader = "X-Share-Token";

    /// <summary>Header carrying the session-owner secret on mutating /api/sessions/*
    /// requests (clip PUT, session DELETE). Issued by the server at session creation
    /// (see the <c>ownerSecret</c> field of the POST /api/sessions response) and required
    /// again on every mutation — so a holder of the public upload token cannot overwrite,
    /// append to or delete another user's session.</summary>
    public const string OwnerHeader = "X-Session-Owner";

    /// <summary>Header binding a Twitch/Discord login status/delete call to the app
    /// session that started the login. Issued alongside <c>state</c> by the
    /// POST …/login/start response (<c>nonce</c> field) and required on every status poll
    /// and delete — a third party that learns the state cannot fetch the OAuth result.</summary>
    public const string LoginNonceHeader = "X-Login-Nonce";

    /// <summary>Header carrying the persistent per-install identity on every Twitch/Discord
    /// login call. Generated once per installation (settings), stored alongside the share
    /// token. The server issues a possession secret for it on first contact
    /// (<see cref="InstallSecretHeader"/>), so a caller must prove it is the SAME
    /// installation that started a login — a script holding only the public upload token
    /// cannot start a login flow or poll its result away (S2).</summary>
    public const string InstallIdHeader = "X-Install-Id";

    /// <summary>Header carrying the per-install possession secret the server issued for
    /// <see cref="InstallIdHeader"/> on the install's first contact. Required (with the id)
    /// on every login start, status poll and delete — the possession proof of the S2 login
    /// hardening. Also returned by the start endpoint (<c>installSecret</c> field) when it
    /// onboards an install, so the app can persist it.</summary>
    public const string InstallSecretHeader = "X-Install-Secret";
}
