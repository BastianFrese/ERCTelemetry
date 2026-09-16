namespace ERCTelemetry.Core.Share;

/// <summary>Shared constants of the ERC race-result upload (app ↔ erdi-erc.de).</summary>
public static class ErcConstants
{
    /// <summary>Built-in default API base when settings hold no override URL. The race
    /// POST and the leagues GET hang off this base (+/race, +/leagues).</summary>
    public const string DefaultApiBaseUrl = "https://erdi-erc.de/api/telemetry";

    /// <summary>Header carrying the driver's personal API key on all /api/telemetry/* requests.</summary>
    public const string ApiKeyHeader = "X-Api-Key";
}
