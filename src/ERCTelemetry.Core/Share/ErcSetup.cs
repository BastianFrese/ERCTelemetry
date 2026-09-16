using System.Text.Json.Serialization;

namespace ERCTelemetry.Core.Share;

/// <summary>Result of <c>GET /api/setups</c>: the caller's setup access tier/role plus the
/// setups they may view. <see cref="Tier"/> 0 = no access, 1 = login + community guild,
/// 3–5 = higher tiers (2 is treated as 1 server-side).</summary>
public sealed record ErcSetupsResponse(int Tier, string? Role, IReadOnlyList<ErcSetup> Setups);

/// <summary>One track setup from <c>GET /api/setups</c>. <see cref="PayloadJson"/> is the
/// raw SetupGameSpec JSON the website stores; map it to a <c>CarSetupSnapshot</c> with
/// <see cref="ErcSetupMapper"/>.</summary>
public sealed record ErcSetup(
    int Id,
    string Track,
    string Title,
    string? GameYear,
    DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("payload")] string PayloadJson);
