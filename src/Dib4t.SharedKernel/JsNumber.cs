using System.Globalization;
using System.Numerics;
using System.Text;

namespace Dib4t.SharedKernel;

/// <summary>
/// JavaScript's <c>Number.prototype.toString()</c> (ECMAScript Number::toString, radix 10): the shortest digits that
/// round-trip, written as JavaScript writes them (<c>10</c>, <c>32.5</c>, <c>1e+21</c>, <c>1e-7</c>, <c>-0</c> → <c>0</c>).
/// The legacy app shows, casts and answers numbers this way: Mongoose <c>String</c> casts (<c>[1] → ["1"]</c>), Joi's
/// "safe number" check and messages, and every number in a legacy JSON answer.
/// </summary>
public static class JsNumber
{
    public static string ToString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (value == 0)
        {
            return "0"; // also -0
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value < 0)
        {
            return "-" + ToString(-value);
        }

        var (digits, n) = ShortestDigits(value);
        var k = digits.Length;
        var text = new StringBuilder(32);
        if (k <= n && n <= 21)
        {
            text.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            text.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            text.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var exponent = n - 1;
            text.Append(digits[0]);
            if (k > 1)
            {
                text.Append('.').Append(digits, 1, k - 1);
            }

            text.Append('e').Append(exponent < 0 ? '-' : '+').Append(Math.Abs(exponent).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// JavaScript <c>Number(text)</c> (ECMAScript StringToNumber): white space around is ignored; empty → <c>0</c>;
    /// a decimal literal (<c>12</c>, <c>-1.5</c>, <c>.5</c>, <c>5.</c>, <c>1e3</c>), <c>0x</c>/<c>0o</c>/<c>0b</c> integers
    /// (no sign) and <c>Infinity</c> with an optional sign; anything else → <c>NaN</c>. The legacy budget page computes
    /// with the inputs' texts this way (<c>"20" * "1.5"</c>).
    /// </summary>
    public static double FromText(string? text)
    {
        if (text is null)
        {
            return 0; // Number(undefined) is NaN, but an input never holds undefined; null → 0
        }

        var trimmed = text.Trim(WhiteSpace);
        if (trimmed.Length == 0)
        {
            return 0;
        }

        switch (trimmed)
        {
            case "Infinity" or "+Infinity":
                return double.PositiveInfinity;
            case "-Infinity":
                return double.NegativeInfinity;
        }

        if (trimmed.Length > 2 && trimmed[0] == '0' && char.ToLowerInvariant(trimmed[1]) is 'x' or 'o' or 'b')
        {
            var radix = char.ToLowerInvariant(trimmed[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            var value = BigInteger.Zero;
            foreach (var c in trimmed.AsSpan(2))
            {
                var digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => 99,
                };
                if (digit >= radix)
                {
                    return double.NaN;
                }

                value = (value * radix) + digit;
            }

            return (double)value;
        }

        return IsDecimalLiteral(trimmed) ? double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
    }

    /// <summary>
    /// JavaScript <c>Number.prototype.toFixed(digits)</c> (ECMAScript, digits 0–100): the exact value of the double
    /// rounded to <paramref name="digits"/> decimals, a tie rounding up (away from zero); <c>NaN</c>; magnitudes from
    /// 1e21 on as <see cref="ToString(double)"/>; a negative value keeps its sign even when it rounds to zero
    /// (<c>-0.001</c> → <c>-0.00</c>), <c>-0</c> does not.
    /// </summary>
    public static string ToFixed(double value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 100);
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (Math.Abs(value) >= 1e21 || double.IsInfinity(value))
        {
            return ToString(value);
        }

        var sign = value < 0 ? "-" : string.Empty;
        var x = Math.Abs(value);

        // x = mantissa · 2^exponent exactly; n = round_half_up(x · 10^digits).
        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        int exponent;
        if (exponentBits == 0)
        {
            exponent = -1074;
        }
        else
        {
            mantissa |= 1L << 52;
            exponent = exponentBits - 1075;
        }

        var scaled = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        BigInteger n;
        if (exponent >= 0)
        {
            n = scaled << exponent;
        }
        else
        {
            var denominator = BigInteger.One << -exponent;
            n = BigInteger.DivRem(scaled, denominator, out var remainder);
            if (remainder * 2 >= denominator)
            {
                n += 1;
            }
        }

        var text = n.ToString(CultureInfo.InvariantCulture);
        if (digits == 0)
        {
            return sign + text;
        }

        text = text.PadLeft(digits + 1, '0');
        return sign + text[..^digits] + "." + text[^digits..];
    }

    /// <summary>JavaScript white space and line terminators (<c>String.prototype.trim</c>).</summary>
    private static readonly char[] WhiteSpace =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005',
        '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff',
    ];

    /// <summary>StrDecimalLiteral: <c>[+-]? (digits [. digits?] | . digits) ([eE] [+-]? digits)?</c>.</summary>
    private static bool IsDecimalLiteral(string text)
    {
        var i = 0;
        if (text[i] is '+' or '-')
        {
            i++;
        }

        var integerDigits = Digits(text, ref i);
        var fractionDigits = 0;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            fractionDigits = Digits(text, ref i);
        }

        if (integerDigits + fractionDigits == 0)
        {
            return false;
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            i++;
            if (i < text.Length && text[i] is '+' or '-')
            {
                i++;
            }

            if (Digits(text, ref i) == 0)
            {
                return false;
            }
        }

        return i == text.Length;
    }

    private static int Digits(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        return i - start;
    }

    /// <summary>
    /// The shortest round-trip digits <c>s</c> (no leading or trailing zeros) and the decimal point position <c>n</c>
    /// with <c>value = 0.s × 10^n</c>, from .NET's shortest round-trip format ("R", exact since .NET Core 3.0).
    /// </summary>
    private static (string Digits, int PointPosition) ShortestDigits(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? text : text[..dot];
        var all = dot < 0 ? text : integerPart + text[(dot + 1)..];
        var point = integerPart.Length + exponent;
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0')
        {
            leading++;
        }

        return (all[leading..].TrimEnd('0'), point - leading);
    }
}
