using System.Text.Json;
using ERCTelemetry.Core.Share;
using Xunit;

namespace ERCTelemetry.Core.Tests.Share;

/// <summary>The ERC race-result payload serializes with the Web defaults — camelCase keys
/// exactly as the ERC-side parser reads them (league, track, date, season, fastestLap,
/// finishes[].position/driver/raceTimeMs/qualifyingPosition/dnf). This pins that contract so
/// an options change (naming policy, null handling) cannot silently break the upload.</summary>
public sealed class ErcResultPayloadTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Payload_serializes_with_camelCase_keys_the_site_parser_reads()
    {
        var payload = new ErcRaceResultPayload(
            Track: "Bahrain",
            Date: new DateTimeOffset(2026, 9, 9, 18, 30, 0, TimeSpan.Zero),
            League: "Nordamerika",
            Season: "S1 2026",
            FastestLap: "A. Fahrer",
            Finishes:
            [
                new ErcRaceFinish(1, "A. Fahrer", RaceTimeMs: 3_123_456, QualifyingPosition: 2),
                new ErcRaceFinish(0, "B. Fahrer", Dnf: true),
            ]);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Json));
        var root = doc.RootElement;

        Assert.Equal("Bahrain", root.GetProperty("track").GetString());
        Assert.Equal("Nordamerika", root.GetProperty("league").GetString());
        Assert.Equal("S1 2026", root.GetProperty("season").GetString());
        Assert.Equal("A. Fahrer", root.GetProperty("fastestLap").GetString());
        Assert.True(root.TryGetProperty("date", out _));
        // The server parser reads the session start straight from JSON — pin the ISO-8601
        // round-trip (GetDateTimeOffset on the serialized value), not just the key's presence.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 9, 18, 30, 0, TimeSpan.Zero),
            root.GetProperty("date").GetDateTimeOffset());

        var finishes = root.GetProperty("finishes");
        Assert.Equal(2, finishes.GetArrayLength());

        var first = finishes[0];
        Assert.Equal(1, first.GetProperty("position").GetInt32());
        Assert.Equal("A. Fahrer", first.GetProperty("driver").GetString());
        Assert.Equal(3_123_456, first.GetProperty("raceTimeMs").GetInt64());
        Assert.Equal(2, first.GetProperty("qualifyingPosition").GetInt32());

        var dnf = finishes[1];
        Assert.Equal(0, dnf.GetProperty("position").GetInt32());
        Assert.True(dnf.GetProperty("dnf").GetBoolean());
    }

    [Fact]
    public void Optional_fields_serialize_as_json_null_in_web_defaults()
    {
        var payload = new ErcRaceResultPayload(
            Track: "Baku",
            Date: null,
            League: null,
            Season: null,
            FastestLap: null,
            Finishes: [new ErcRaceFinish(1, "A. Fahrer")]);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Json));
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("league").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("season").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("date").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fastestLap").ValueKind);

        var finish = root.GetProperty("finishes")[0];
        Assert.Equal(JsonValueKind.Null, finish.GetProperty("raceTimeMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, finish.GetProperty("qualifyingPosition").ValueKind);
        Assert.False(finish.GetProperty("dnf").GetBoolean());
    }
}
