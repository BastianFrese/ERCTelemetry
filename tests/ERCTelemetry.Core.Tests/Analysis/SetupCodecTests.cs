using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.Analysis;

/// <summary>SetupCodec: round-trips a CarSetupSnapshot through the share code, rejects
/// typos/foreign formats and handles negative floats (camber/toe).</summary>
public sealed class SetupCodecTests
{
    private static CarSetupSnapshot Sample() => new(
        FrontWing: 8, RearWing: 12, OnThrottle: 55, OffThrottle: 70,
        FrontCamber: -3.5f, RearCamber: -2.0f, FrontToe: 0.05f, RearToe: 0.2f,
        FrontSuspension: 4, RearSuspension: 6, FrontAntiRollBar: 5, RearAntiRollBar: 7,
        FrontSuspensionHeight: 3, RearSuspensionHeight: 5,
        BrakePressure: 90, BrakeBias: 58, EngineBraking: 40,
        Ballast: 12.5f, FuelLoad: 40.0f,
        TyresPressure: [21.5f, 21.0f, 20.5f, 20.0f],
        NextFrontWingValue: 8);

    [Fact]
    public void Encode_then_decode_round_trips_all_fields()
    {
        var code = SetupCodec.Encode(Sample());
        var decoded = SetupCodec.Decode(code);

        Assert.NotNull(decoded);
        Assert.Equal(Sample(), decoded);
    }

    [Fact]
    public void Code_is_compact_and_prefixed()
    {
        var code = SetupCodec.Encode(Sample());

        Assert.StartsWith("ERC1-", code);
        Assert.True(code.Length < 120, $"code too long: {code.Length}");
    }

    [Fact]
    public void Decode_rejects_wrong_prefix()
    {
        var code = SetupCodec.Encode(Sample());

        Assert.Null(SetupCodec.Decode("XXXX-" + code[5..]));
    }

    [Fact]
    public void Decode_rejects_tampered_checksum()
    {
        var code = SetupCodec.Encode(Sample());
        var tampered = code[..^1] + (code[^1] == '0' ? '1' : '0');

        Assert.Null(SetupCodec.Decode(tampered));
    }

    [Fact]
    public void Decode_rejects_garbage()
    {
        Assert.Null(SetupCodec.Decode(""));
        Assert.Null(SetupCodec.Decode("   "));
        Assert.Null(SetupCodec.Decode("not a code"));
        Assert.Null(SetupCodec.Decode("ERC1-1-2-3"));
    }

    [Fact]
    public void Decode_rejects_truncated_token_list()
    {
        var code = SetupCodec.Encode(Sample());
        var tokens = code.Split('-');
        var truncated = string.Join("-", tokens[..^2]); // drop checksum + last token

        Assert.Null(SetupCodec.Decode(truncated));
    }

    [Fact]
    public void Negative_floats_round_trip()
    {
        var setup = Sample() with { FrontCamber = -4.0f, RearToe = -0.1f };
        var decoded = SetupCodec.Decode(SetupCodec.Encode(setup));

        Assert.NotNull(decoded);
        Assert.Equal(-4.0f, decoded.FrontCamber);
        Assert.Equal(-0.1f, decoded.RearToe);
    }

    [Fact]
    public void Zero_values_round_trip()
    {
        var setup = new CarSetupSnapshot(
            FrontWing: 0, RearWing: 0, OnThrottle: 0, OffThrottle: 0,
            FrontCamber: 0, RearCamber: 0, FrontToe: 0, RearToe: 0,
            FrontSuspension: 0, RearSuspension: 0, FrontAntiRollBar: 0, RearAntiRollBar: 0,
            FrontSuspensionHeight: 0, RearSuspensionHeight: 0,
            BrakePressure: 0, BrakeBias: 0, EngineBraking: 0,
            Ballast: 0, FuelLoad: 0,
            TyresPressure: [0, 0, 0, 0],
            NextFrontWingValue: 0);
        var decoded = SetupCodec.Decode(SetupCodec.Encode(setup));

        Assert.NotNull(decoded);
        Assert.Equal(setup, decoded);
    }
}
