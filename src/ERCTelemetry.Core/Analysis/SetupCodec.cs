using ERCTelemetry.Core.Session;

namespace ERCTelemetry.Core.Analysis;

/// <summary>Encodes a <see cref="CarSetupSnapshot"/> into a compact, shareable code (like
/// F1's own setup share codes) and decodes it back. Pure text — paste it in chat/Discord,
/// or import a friend's code to see the values. A checksum rejects typos and foreign
/// formats; unknown/truncated codes decode to null (never throw).</summary>
public static class SetupCodec
{
    private const string Prefix = "ERC1";
    private const char Sep = '-';
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Encode(CarSetupSnapshot s)
    {
        var tokens = new List<string>(24)
        {
            Int(s.FrontWing), Int(s.RearWing), Int(s.OnThrottle), Int(s.OffThrottle),
            Float(s.FrontCamber, 100), Float(s.RearCamber, 100),
            Float(s.FrontToe, 1000), Float(s.RearToe, 1000),
            Int(s.FrontSuspension), Int(s.RearSuspension),
            Int(s.FrontAntiRollBar), Int(s.RearAntiRollBar),
            Int(s.FrontSuspensionHeight), Int(s.RearSuspensionHeight),
            Int(s.BrakePressure), Int(s.BrakeBias), Int(s.EngineBraking),
            Float(s.Ballast, 10), Float(s.FuelLoad, 10),
            Float(s.TyresPressure[0], 100), Float(s.TyresPressure[1], 100),
            Float(s.TyresPressure[2], 100), Float(s.TyresPressure[3], 100),
            Int(s.NextFrontWingValue),
        };
        var body = string.Join(Sep, tokens);
        return $"{Prefix}{Sep}{body}{Sep}{Checksum(body)}";
    }

    public static CarSetupSnapshot? Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var parts = code.Trim().Split(Sep);
        if (parts.Length < 2 || parts[0] != Prefix)
        {
            return null;
        }

        var body = string.Join(Sep, parts[1..^1]);
        if (parts[^1] != Checksum(body))
        {
            return null;
        }

        var tokens = body.Split(Sep);
        if (tokens.Length != 24)
        {
            return null;
        }

        try
        {
            return new CarSetupSnapshot(
                (byte)Int(tokens[0]), (byte)Int(tokens[1]),
                (byte)Int(tokens[2]), (byte)Int(tokens[3]),
                Float(tokens[4], 100), Float(tokens[5], 100),
                Float(tokens[6], 1000), Float(tokens[7], 1000),
                (byte)Int(tokens[8]), (byte)Int(tokens[9]),
                (byte)Int(tokens[10]), (byte)Int(tokens[11]),
                (byte)Int(tokens[12]), (byte)Int(tokens[13]),
                (byte)Int(tokens[14]), (byte)Int(tokens[15]),
                (byte)Int(tokens[16]),
                Float(tokens[17], 10), Float(tokens[18], 10),
                [Float(tokens[19], 100), Float(tokens[20], 100),
                 Float(tokens[21], 100), Float(tokens[22], 100)],
                (byte)Int(tokens[23]));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string Int(byte value) => ToBase36(value);

    private static string Float(float value, int scale)
    {
        var scaled = (int)Math.Round(value * scale);
        return scaled < 0 ? "n" + ToBase36(-scaled) : ToBase36(scaled);
    }

    private static int Int(string token) => FromBase36(token);

    private static float Float(string token, int scale)
    {
        var negative = token.StartsWith('n');
        var digits = negative ? token[1..] : token;
        var scaled = FromBase36(digits);
        return (negative ? -scaled : scaled) / (float)scale;
    }

    /// <summary>Base36 (0-9, A-Z) — .NET's Convert only supports bases 2/8/10/16.</summary>
    private static string ToBase36(int value)
    {
        if (value == 0)
        {
            return "0";
        }

        Span<char> buffer = stackalloc char[6];
        var i = buffer.Length;
        while (value > 0)
        {
            buffer[--i] = Digits[value % 36];
            value /= 36;
        }

        return new string(buffer[i..]);
    }

    private static int FromBase36(string token)
    {
        var value = 0;
        foreach (var c in token)
        {
            var digit = Digits.IndexOf(char.ToUpperInvariant(c));
            if (digit < 0)
            {
                throw new FormatException($"invalid base36 digit '{c}'");
            }

            value = checked(value * 36 + digit);
        }

        return value;
    }

    /// <summary>Simple sum-of-chars checksum (base36) — catches typos and foreign codes,
    /// not a security feature.</summary>
    private static string Checksum(string body)
    {
        var sum = 0;
        foreach (var c in body)
        {
            sum = (sum + c) % 36;
        }

        return ToBase36(sum);
    }
}
