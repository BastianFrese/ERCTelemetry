using ERCTelemetry.Core.Share;
using Xunit;

namespace ERCTelemetry.Core.Tests.Share;

/// <summary>Maps the website's SetupGameSpec payload (flat or categories-nested) to the
/// app's CarSetupSnapshot. The app-only fields (engine braking, ballast, fuel, next
/// front-wing value) always fall back to game defaults.</summary>
public sealed class ErcSetupMapperTests
{
    [Fact]
    public void Maps_flat_payload_fields()
    {
        const string payload = """
            {
              "frontWing": 8, "rearWing": 12,
              "diffOnThrottle": 55, "diffOffThrottle": 60,
              "frontCamber": -3.0, "rearCamber": -1.5,
              "frontToe": 0.05, "rearToe": 0.15,
              "frontSuspension": 5, "rearSuspension": 7,
              "frontAntiRoll": 3, "rearAntiRoll": 5,
              "frontRideHeight": 20, "rearRideHeight": 45,
              "brakePressure": 90, "brakeBias": 58,
              "frontRightPressure": 26.5, "frontLeftPressure": 25.0,
              "rearRightPressure": 24.0, "rearLeftPressure": 23.5
            }
            """;

        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(payload);

        Assert.Equal(8, snapshot.FrontWing);
        Assert.Equal(12, snapshot.RearWing);
        Assert.Equal(55, snapshot.OnThrottle);
        Assert.Equal(60, snapshot.OffThrottle);
        Assert.Equal(-3.0f, snapshot.FrontCamber);
        Assert.Equal(-1.5f, snapshot.RearCamber);
        Assert.Equal(0.05f, snapshot.FrontToe);
        Assert.Equal(0.15f, snapshot.RearToe);
        Assert.Equal(5, snapshot.FrontSuspension);
        Assert.Equal(7, snapshot.RearSuspension);
        Assert.Equal(3, snapshot.FrontAntiRollBar);
        Assert.Equal(5, snapshot.RearAntiRollBar);
        Assert.Equal(20, snapshot.FrontSuspensionHeight);
        Assert.Equal(45, snapshot.RearSuspensionHeight);
        Assert.Equal(90, snapshot.BrakePressure);
        Assert.Equal(58, snapshot.BrakeBias);
    }

    [Fact]
    public void Maps_categories_nested_payload()
    {
        const string payload = """
            {
              "categories": {
                "aero": { "frontWing": 9, "rearWing": 14 },
                "transmission": { "diffOnThrottle": 50, "diffOffThrottle": 65 },
                "geometry": { "frontCamber": -3.2, "rearCamber": -1.4, "frontToe": 0.06, "rearToe": 0.18 },
                "suspension": { "frontSuspension": 6, "rearSuspension": 8, "frontAntiRoll": 4, "rearAntiRoll": 6, "frontRideHeight": 22, "rearRideHeight": 48 },
                "brakes": { "brakePressure": 88, "brakeBias": 57 },
                "tyres": { "frontRightPressure": 27.0, "frontLeftPressure": 26.0, "rearRightPressure": 25.0, "rearLeftPressure": 24.0 }
              }
            }
            """;

        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(payload);

        Assert.Equal(9, snapshot.FrontWing);
        Assert.Equal(14, snapshot.RearWing);
        Assert.Equal(50, snapshot.OnThrottle);
        Assert.Equal(65, snapshot.OffThrottle);
        Assert.Equal(-3.2f, snapshot.FrontCamber);
        Assert.Equal(4, snapshot.FrontAntiRollBar);
        Assert.Equal(22, snapshot.FrontSuspensionHeight);
        Assert.Equal(88, snapshot.BrakePressure);
        Assert.Equal(57, snapshot.BrakeBias);
    }

    [Fact]
    public void Tyres_pressure_order_is_FL_FR_RL_RR()
    {
        const string payload = """
            {
              "frontLeftPressure": 25.0, "frontRightPressure": 26.5,
              "rearLeftPressure": 23.5, "rearRightPressure": 24.0
            }
            """;

        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(payload);

        Assert.Equal(4, snapshot.TyresPressure.Length);
        Assert.Equal(25.0f, snapshot.TyresPressure[0]); // FL
        Assert.Equal(26.5f, snapshot.TyresPressure[1]); // FR
        Assert.Equal(23.5f, snapshot.TyresPressure[2]); // RL
        Assert.Equal(24.0f, snapshot.TyresPressure[3]); // RR
    }

    [Fact]
    public void Missing_fields_fall_back_to_game_defaults()
    {
        const string payload = """{"frontWing": 10}""";

        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(payload);

        Assert.Equal(10, snapshot.FrontWing);
        Assert.Equal(0, snapshot.RearWing);
        Assert.Equal(50, snapshot.EngineBraking);
        Assert.Equal(0f, snapshot.Ballast);
        Assert.Equal(0f, snapshot.FuelLoad);
        Assert.Equal(0, snapshot.NextFrontWingValue);
        Assert.Equal([0f, 0f, 0f, 0f], snapshot.TyresPressure);
    }

    [Fact]
    public void Malformed_json_returns_defaults()
    {
        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot("{ not json");

        Assert.Equal(0, snapshot.FrontWing);
        Assert.Equal(50, snapshot.EngineBraking);
    }

    [Fact]
    public void Empty_payload_returns_defaults()
    {
        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(string.Empty);

        Assert.Equal(0, snapshot.FrontWing);
        Assert.Equal(50, snapshot.EngineBraking);
        Assert.Equal([0f, 0f, 0f, 0f], snapshot.TyresPressure);
    }

    [Fact]
    public void Rounds_fractional_byte_fields()
    {
        const string payload = """{"frontWing": 8.6, "brakeBias": 57.4}""";

        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(payload);

        Assert.Equal(9, snapshot.FrontWing);   // 8.6 rounds up
        Assert.Equal(57, snapshot.BrakeBias); // 57.4 rounds down
    }
}
