namespace ERCTelemetry.ShareServer;

/// <summary>One per-install possession entry (S2): the issued secret plus the last-seen
/// timestamp used for idle eviction, so a token-holder cannot grow the registry without
/// bound (see Program.PurgeIdleInstallSecrets). <see cref="LastSeenUtc"/> is refreshed on
/// every successful possession use (login start, status poll, delete). Immutable —
/// transitions use <c>with</c>.</summary>
internal record struct InstallSecretEntry(string Secret, DateTimeOffset LastSeenUtc);
