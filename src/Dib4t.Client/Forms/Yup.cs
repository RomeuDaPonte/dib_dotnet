using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Dib4t.Client.Forms;

/// <summary>
/// The parts of yup 0.29.3 the legacy forms use (<c>front-end/src/schemas</c>), with yup's own messages (checked by
/// running the legacy front-end's yup). Client-side only: the server's Joi check stays authoritative.
/// </summary>
public static partial class Yup
{
    /// <summary><c>string().email()</c> (an empty value is left to <c>required()</c>).</summary>
    public static bool IsEmail(string value) => YupEmail().IsMatch(value);

    /// <summary>
    /// yup <c>number()</c>'s cast of a text field: white space removed, <c>""</c> is NaN, otherwise JavaScript
    /// <c>Number(text)</c> (so <c>"0x10"</c> is 16 and <c>"32,5"</c> is NaN). <c>null</c> = NaN.
    /// </summary>
    public static double? Number(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var compact = JsSpace().Replace(text, string.Empty);
        if (compact.Length == 0)
        {
            return null;
        }

        var value = JsStringToNumber(compact);
        return double.IsNaN(value) ? null : value;
    }

    /// <summary>yup's <c>number()</c> type error for a text that is not a number.</summary>
    public static string NumberTypeError(string label, string? text) =>
        $"{label} must be a `number` type, but the final value was: `NaN` (cast from the value `\"{text}\"`).";

    /// <summary>yup's <c>required()</c> message.</summary>
    public static string Required(string label) => $"{label} is a required field";

    /// <summary>JavaScript <c>Number(text)</c> for a text without white space (ECMAScript StringToNumber).</summary>
    private static double JsStringToNumber(string text)
    {
        if (RadixLiteral().Match(text) is { Success: true } radix)
        {
            var digits = radix.Groups[2].Value;
            var @base = char.ToLowerInvariant(radix.Groups[1].Value[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            var number = BigInteger.Zero;
            foreach (var digit in digits)
            {
                var value = Convert.ToInt32(digit.ToString(), 16);
                if (value >= @base)
                {
                    return double.NaN; // e.g. "0b12"
                }

                number = (number * @base) + value;
            }

            return (double)number;
        }

        if (text is "Infinity" or "+Infinity")
        {
            return double.PositiveInfinity;
        }

        if (text == "-Infinity")
        {
            return double.NegativeInfinity;
        }

        return DecimalLiteral().IsMatch(text) ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
    }

    /// <summary>yup 0.29.3 <c>rEmail</c> (case-insensitive), with JavaScript's ASCII <c>\d</c> as <c>[0-9]</c> and <c>$</c> as <c>\z</c>.</summary>
    [GeneratedRegex("""^((([a-z]|[0-9]|[!#\$%&'\*\+\-\/=\?\^_`{\|}~]|[ -퟿豈-﷏ﷰ-￯])+(\.([a-z]|[0-9]|[!#\$%&'\*\+\-\/=\?\^_`{\|}~]|[ -퟿豈-﷏ﷰ-￯])+)*)|((\x22)((((\x20|\x09)*(\x0d\x0a))?(\x20|\x09)+)?(([\x01-\x08\x0b\x0c\x0e-\x1f\x7f]|\x21|[\x23-\x5b]|[\x5d-\x7e]|[ -퟿豈-﷏ﷰ-￯])|(\\([\x01-\x09\x0b\x0c\x0d-\x7f]|[ -퟿豈-﷏ﷰ-￯]))))*(((\x20|\x09)*(\x0d\x0a))?(\x20|\x09)+)?(\x22)))@((([a-z]|[0-9]|[ -퟿豈-﷏ﷰ-￯])|(([a-z]|[0-9]|[ -퟿豈-﷏ﷰ-￯])([a-z]|[0-9]|-|\.|_|~|[ -퟿豈-﷏ﷰ-￯])*([a-z]|[0-9]|[ -퟿豈-﷏ﷰ-￯])))\.)+(([a-z]|[ -퟿豈-﷏ﷰ-￯])|(([a-z]|[ -퟿豈-﷏ﷰ-￯])([a-z]|[0-9]|-|\.|_|~|[ -퟿豈-﷏ﷰ-￯])*([a-z]|[ -퟿豈-﷏ﷰ-￯])))\z""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YupEmail();

    [GeneratedRegex("[\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff]")]
    private static partial Regex JsSpace();

    [GeneratedRegex("^(0[xX]|0[oO]|0[bB])([0-9a-fA-F]+)\\z")]
    private static partial Regex RadixLiteral();

    [GeneratedRegex("^[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?\\z")]
    private static partial Regex DecimalLiteral();
}
