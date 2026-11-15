using System.Globalization;
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
