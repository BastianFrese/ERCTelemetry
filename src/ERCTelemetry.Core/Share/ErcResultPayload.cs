namespace ERCTelemetry.Core.Share;

/// <summary>Payload of one finished league race for the ERC admin-review inbox
/// (<c>POST /api/telemetry/race</c>). Serialized with the Web defaults so the property
/// names arrive in camelCase — exactly the keys the ERC-side parser reads
/// (<c>track</c>, <c>date</c>, <c>league</c>, <c>season</c>, <c>fastestLap</c>,
/// <c>finishes</c>). <see cref="League"/> is the league NAME the driver picked in the
/// send dialog; <see cref="Date"/> the session start.</summary>
public sealed record ErcRaceResultPayload(
    string Track,
    DateTimeOffset? Date,
    string? League,
    string? Season,
    string? FastestLap,
    IReadOnlyList<ErcRaceFinish> Finishes);

/// <summary>One final-classification row. <see cref="Dnf"/> sets Position to 0 on the
/// ERC side — the DNF semantics of RaceFinish there; <see cref="RaceTimeMs"/> and
/// <see cref="QualifyingPosition"/> are optional extras the review form pre-fills.</summary>
public sealed record ErcRaceFinish(
    int Position,
    string Driver,
    long? RaceTimeMs = null,
    int? QualifyingPosition = null,
    bool Dnf = false);

/// <summary>One league the driver may send a result for, from
/// <c>GET /api/telemetry/leagues</c>. The send dialog shows <see cref="Name"/>.</summary>
public sealed record ErcLeague(string Id, string Name);

/// <summary>Owner of an API key, from <c>GET /api/telemetry/me</c>. The app compares
/// <see cref="DiscordId"/> with the logged-in Discord user to verify the key belongs to
/// the person using it („Verbindung testen“).</summary>
public sealed record ErcKeyOwner(string DiscordId, string? Name, string? DisplayName);
