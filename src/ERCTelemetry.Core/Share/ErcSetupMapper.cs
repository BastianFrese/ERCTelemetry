using System.Text.Json;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Share;

/// <summary>Maps a website SetupGameSpec payload (the raw JSON stored in
/// <see cref="ErcSetup.PayloadJson"/>) to the app's <see cref="CarSetupSnapshot"/>. The
/// website payload is a flat or <c>categories</c>-nested object of 20 numeric fields; the
/// app-only fields (engine braking, ballast, fuel, next front-wing value) have no website
/// equivalent and fall back to game defaults. Unknown/missing fields keep their default —
/// a malformed payload never throws.</summary>
public static class ErcSetupMapper
{
    // Game defaults for fields the website payload does not carry.
    private const byte DefaultEngineBraking = 50;
    private const float DefaultBallast = 0f;
    private const float DefaultFuelLoad = 0f;
    private const byte DefaultNextFrontWingValue = 0;

    /// <summary>Maps the website's SetupGameSpec JSON to a <see cref="CarSetupSnapshot"/>.
    /// Accepts both the flat legacy shape and the <c>categories</c>-nested shape the editor
    /// writes. Returns a snapshot with game defaults for any field the payload lacks.</summary>
    public static CarSetupSnapshot MapToCarSetupSnapshot(string payloadJson)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Newer payloads nest the fields under category objects; legacy ones
                    // are flat (the root itself is the field map).
                    if (root.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var category in c.EnumerateObject())
                        {
                            if (category.Value.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            CollectFields(category.Value, values);
                        }
                    }
                    else
                    {
                        CollectFields(root, values);
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed payload → all defaults.
            }
        }

        return new CarSetupSnapshot(
            FrontWing: ToByte(values, "frontWing"),
            RearWing: ToByte(values, "rearWing"),
            OnThrottle: ToByte(values, "diffOnThrottle"),
            OffThrottle: ToByte(values, "diffOffThrottle"),
            FrontCamber: ToFloat(values, "frontCamber"),
            RearCamber: ToFloat(values, "rearCamber"),
            FrontToe: ToFloat(values, "frontToe"),
            RearToe: ToFloat(values, "rearToe"),
            FrontSuspension: ToByte(values, "frontSuspension"),
            RearSuspension: ToByte(values, "rearSuspension"),
            FrontAntiRollBar: ToByte(values, "frontAntiRoll"),
            RearAntiRollBar: ToByte(values, "rearAntiRoll"),
            FrontSuspensionHeight: ToByte(values, "frontRideHeight"),
            RearSuspensionHeight: ToByte(values, "rearRideHeight"),
            BrakePressure: ToByte(values, "brakePressure"),
            BrakeBias: ToByte(values, "brakeBias"),
            EngineBraking: DefaultEngineBraking,
            Ballast: DefaultBallast,
            FuelLoad: DefaultFuelLoad,
            TyresPressure:
            [
                ToFloat(values, "frontLeftPressure"),
                ToFloat(values, "frontRightPressure"),
                ToFloat(values, "rearLeftPressure"),
                ToFloat(values, "rearRightPressure"),
            ],
            NextFrontWingValue: DefaultNextFrontWingValue);
    }

    /// <summary>Collects the numeric fields of a JSON object into the value map (used for
    /// both a category object and a flat payload root).</summary>
    private static void CollectFields(JsonElement fields, Dictionary<string, double> values)
    {
        foreach (var field in fields.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.Number
                && field.Value.TryGetDouble(out var number))
            {
                values[field.Name] = number;
            }
        }
    }

    private static byte ToByte(IReadOnlyDictionary<string, double> values, string key) =>
        values.TryGetValue(key, out var v) ? (byte)Math.Clamp(Math.Round(v), byte.MinValue, byte.MaxValue) : (byte)0;

    private static float ToFloat(IReadOnlyDictionary<string, double> values, string key) =>
        values.TryGetValue(key, out var v) ? (float)v : 0f;
}
