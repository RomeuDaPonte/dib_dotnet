using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.SharedKernel;

namespace Dib4t.Application.Common.Validation;

/// <summary>
/// The subset of Joi 14 the legacy validations use (<c>back-end/validations/*.js</c>): <c>Joi.validate(body, {…})</c>
/// with the legacy defaults, i.e. <c>abortEarly</c> (the first error only), <c>convert</c> on, and no unknown keys.
/// The message is the legacy 400 text, e.g. <c>"email" is required</c>. Rules are added here as the feature slices
/// need them; each one is checked against the legacy code's own answers (golden files in <c>tests/fixtures/legacy</c>).
/// </summary>
/// <remarks>
/// JavaScript semantics kept: the body is read like <c>JSON.parse</c> builds the object (see <see cref="JsObject"/>),
/// the schema keys are checked in schema order (a <see cref="JoiRef"/> key is always declared after the key it
/// refers to, which is also the order Joi's reference sorting gives), then the first unknown key is reported;
/// lengths are UTF-16 code units (<c>string.length</c>).
/// </remarks>
internal sealed class JoiObjectSchema(params (string Key, JoiRule Rule)[] children)
{
    /// <summary>The first error message, or <c>null</c> when <paramref name="body"/> is valid.</summary>
    public string? Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return "\"value\" must be an object";
        }

        var properties = JsObject.Properties(body);
        foreach (var (key, rule) in children)
        {
            var value = properties.TryGetValue(key, out var element) ? element : (JsonElement?)null;
            if (rule.Validate(key, value, properties) is { } error)
            {
                return error;
            }
        }

        foreach (var key in properties.Keys)
        {
            if (!children.Any(child => child.Key == key))
            {
                return $"\"{key}\" is not allowed";
            }
        }

        return null;
    }
}

/// <summary>A Joi rule for one key. <c>null</c> value = the key is missing (<c>undefined</c>).</summary>
internal abstract class JoiRule
{
    /// <param name="label">The key (Joi's label).</param>
    /// <param name="value">The key's value; <c>null</c> when the key is missing.</param>
    /// <param name="parent">The whole object (for references).</param>
    public abstract string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent);

    protected static string Limit(double limit) => JsNumber.ToString(limit);
}

/// <summary>
/// <c>Joi.string()</c>: in Joi's order, presence (<c>.required()</c>), an allowed value (<c>.valid(…)</c>) passes at
/// once, type (<c>null</c> and non-strings → "must be a string"), the empty string (never allowed), <c>.valid(…)</c>,
/// then the rules in the order they were added.
/// </summary>
internal sealed class JoiString : JoiRule
{
    private readonly List<Func<string, string, string?>> _rules = [];
    private string[]? _valid;
    private bool _required;

    public static JoiString Create() => new();

    public JoiString Required()
    {
        _required = true;
        return this;
    }

    /// <summary><c>.valid(a, b, …)</c>: only these exact strings.</summary>
    public JoiString Valid(IEnumerable<string> values)
    {
        _valid = [.. values];
        return this;
    }

    public JoiString Min(int limit) => Add((label, value) =>
        value.Length < limit ? $"\"{label}\" length must be at least {Limit(limit)} characters long" : null);

    public JoiString Max(int limit) => Add((label, value) =>
        value.Length > limit ? $"\"{label}\" length must be less than or equal to {Limit(limit)} characters long" : null);

    /// <summary><c>.email()</c> with Joi's defaults: isemail 3.2.0 (<see cref="LegacyEmail"/>).</summary>
    public JoiString Email() => Add((label, value) => LegacyEmail.IsValid(value) ? null : $"\"{label}\" must be a valid email");

    /// <summary><c>.ip()</c> with Joi's defaults: IPv4, IPv6 or IPvFuture, CIDR optional (<see cref="JoiIp"/>).</summary>
    public JoiString Ip() => Add((label, value) =>
        JoiIp.IsValid(value) ? null : $"\"{label}\" must be a valid ip address with a optional CIDR");

    /// <summary><c>.regex(pattern, name)</c>: the value must match <paramref name="pattern"/>.</summary>
    public JoiString Regex(Regex pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Add((label, value) =>
            pattern.IsMatch(value) ? null : $"\"{label}\" with value \"{value}\" fails to match the {name} pattern");
    }

    public override string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent)
    {
        if (value is null)
        {
            return _required ? $"\"{label}\" is required" : null;
        }

        if (value.Value.ValueKind != JsonValueKind.String)
        {
            return $"\"{label}\" must be a string";
        }

        var text = value.Value.GetString()!;
        if (_valid is not null && _valid.Contains(text, StringComparer.Ordinal))
        {
            return null;
        }

        if (text.Length == 0)
        {
            return $"\"{label}\" is not allowed to be empty";
        }

        if (_valid is not null)
        {
            return $"\"{label}\" must be one of [{string.Join(", ", _valid)}]";
        }

        foreach (var rule in _rules)
        {
            if (rule(label, text) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private JoiString Add(Func<string, string, string?> rule)
    {
        _rules.Add(rule);
        return this;
    }
}

/// <summary>
/// <c>Joi.number()</c>: presence, then the type with Joi's string conversion (<see cref="Convert"/>: a decimal text
/// becomes its number; anything else "must be a number"), the "safe number" checks, then the rules in order.
/// </summary>
internal sealed partial class JoiNumber : JoiRule
{
    private const double MaxSafeInteger = 9007199254740991;

    private readonly List<Func<string, double, string?>> _rules = [];
    private bool _required;

    public static JoiNumber Create() => new();

    public JoiNumber Required()
    {
        _required = true;
        return this;
    }

    public JoiNumber Min(double limit) => Add((label, value) =>
        value >= limit ? null : $"\"{label}\" must be larger than or equal to {Limit(limit)}");

    public JoiNumber Max(double limit) => Add((label, value) =>
        value <= limit ? null : $"\"{label}\" must be less than or equal to {Limit(limit)}");

    public JoiNumber Integer() => Add((label, value) =>
        Math.Truncate(value) - value == 0 ? null : $"\"{label}\" must be an integer");

    public override string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent)
    {
        if (value is null)
        {
            return _required ? $"\"{label}\" is required" : null;
        }

        var (number, error) = Convert(value.Value);
        if (error is not null)
        {
            return $"\"{label}\" {error}";
        }

        foreach (var rule in _rules)
        {
            if (rule(label, number) is { } message)
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>
    /// The number Joi validates (and Mongoose then stores, <c>Number(text)</c> gives the same value for every text
    /// Joi accepts), or the error without its label: a JSON number as it is; a string only when it is a decimal
    /// literal (<c>^\s*[+-]?\d+(\.\d+)?(e[+-]?\d+)?\s*$</c>, case-insensitive), and only when its digits survive the
    /// conversion ("must be a safe number" otherwise, with Joi's own textual round-trip test); a magnitude above
    /// 2^53 − 1 is not safe either; ±Infinity (JSON overflow) is an invalid value.
    /// </summary>
    public static (double Value, string? Error) Convert(JsonElement value)
    {
        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out number) || double.IsInfinity(number))
                {
                    return (double.NaN, "contains an invalid value"); // JSON.parse("1e400") is Infinity, a Joi invalid
                }

                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                var match = JsDecimal().Match(text);
                if (!match.Success)
                {
                    return (double.NaN, "must be a number");
                }

                var trimmed = JsWhiteSpace.Trim(text);
                number = double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!IsSafeText(trimmed, number, match.Groups[1]))
                {
                    return (number, "must be a safe number");
                }

                break;
            default:
                return (double.NaN, "must be a number");
        }

        return number is > MaxSafeInteger or < -MaxSafeInteger ? (number, "must be a safe number") : (number, null);
    }

    /// <summary>Joi 14.3.1 <c>number._base</c>: the text and the parsed number must say the same.</summary>
    private static bool IsSafeText(string text, double number, Group exponent)
    {
        if (text.Contains('e', StringComparison.Ordinal))
        {
            var e = double.Parse(exponent.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var mantissa = JsNumber.ToString(number / Math.Pow(10, e));
            return NormalizeExponent($"{mantissa}e{exponent.Value}") == NormalizeExponent(text);
        }

        return JsNumber.ToString(number) == NormalizeDecimal(text);
    }

    private static string NormalizeExponent(string text)
    {
        text = TrailingZerosBeforeE().Replace(text, "e", 1);
        text = ExponentPlus().Replace(text, "e", 1);
        text = LeadingPlus().Replace(text, string.Empty, 1);
        return LeadingZeros().Replace(text, "$1$2", 1);
    }

    private static string NormalizeDecimal(string text)
    {
        text = LeadingPlus().Replace(text, string.Empty, 1);
        text = ZeroFraction().Replace(text, string.Empty, 1);
        text = LeadingZeros().Replace(text, "$1$2", 1);
        if (text.Contains('.', StringComparison.Ordinal) && text.EndsWith('0'))
        {
            text = TrailingZeros().Replace(text, string.Empty, 1);
        }

        return text;
    }

    private JoiNumber Add(Func<string, double, string?> rule)
    {
        _rules.Add(rule);
        return this;
    }

    // JavaScript's \s is JsWhiteSpace.Class and its \d is ASCII; $ (no m flag) is the end of the input (\z).
    [GeneratedRegex("^[" + JsWhiteSpace.Class + "]*[+-]?[0-9]+(?:\\.[0-9]+)?(?:e([+-]?[0-9]+))?[" + JsWhiteSpace.Class + "]*\\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsDecimal();

    [GeneratedRegex(@"\.?0+e")]
    private static partial Regex TrailingZerosBeforeE();

    [GeneratedRegex(@"e\+")]
    private static partial Regex ExponentPlus();

    [GeneratedRegex(@"^\+")]
    private static partial Regex LeadingPlus();

    [GeneratedRegex("^(-?)0+([1-9])")]
    private static partial Regex LeadingZeros();

    [GeneratedRegex(@"\.0+\z")]
    private static partial Regex ZeroFraction();

    [GeneratedRegex(@"0+\z")]
    private static partial Regex TrailingZeros();
}

/// <summary>
/// <c>Joi.array()</c> (any items): presence, then the type, where a string that <c>JSON.parse</c>s to an array counts
/// as that array (Joi's conversion; the route still stores the original string, see <c>MongooseCast</c>), then
/// <c>.min(n)</c>.
/// </summary>
internal sealed class JoiArray : JoiRule
{
    private int? _min;
    private bool _required;

    public static JoiArray Create() => new();

    public JoiArray Required()
    {
        _required = true;
        return this;
    }

    public JoiArray Min(int limit)
    {
        _min = limit;
        return this;
    }

    public override string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent)
    {
        if (value is null)
        {
            return _required ? $"\"{label}\" is required" : null;
        }

        var array = value.Value;
        if (array.ValueKind == JsonValueKind.String && TryParseArray(array.GetString()!, out var parsed))
        {
            array = parsed;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return $"\"{label}\" must be an array";
        }

        return _min is { } min && array.GetArrayLength() < min ? $"\"{label}\" must contain at least {Limit(min)} items" : null;
    }

    private static bool TryParseArray(string text, out JsonElement array)
    {
        try
        {
            array = JsonElement.Parse(text);
            return array.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            array = default;
            return false;
        }
    }
}

/// <summary><c>Joi.any()</c>: any value, optional (<c>.required()</c> only checks presence).</summary>
internal sealed class JoiAny : JoiRule
{
    private bool _required;

    public static JoiAny Create() => new();

    public JoiAny Required()
    {
        _required = true;
        return this;
    }

    public override string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent) =>
        value is null && _required ? $"\"{label}\" is required" : null;
}

/// <summary>
/// A key declared as <c>Joi.ref("other")</c> (<c>passwordConfirmation: Joi.ref("password")</c>): optional; when
/// present it must be deep-equal to the other key's value, else <c>must be one of [ref:other]</c>.
/// </summary>
internal sealed class JoiRef(string key) : JoiRule
{
    public override string? Validate(string label, JsonElement? value, IReadOnlyDictionary<string, JsonElement> parent)
    {
        if (value is null)
        {
            return null;
        }

        return parent.TryGetValue(key, out var other) && JsonElement.DeepEquals(value.Value, other)
            ? null
            : $"\"{label}\" must be one of [ref:{key}]";
    }
}

/// <summary>
/// Joi 14.3.1 <c>string().ip()</c> with the default options (<c>lib/types/string/ip.js</c> + <c>rfc3986.js</c>):
/// IPv4 (each part with up to two leading zeros), IPv6 (incl. an embedded IPv4) or IPvFuture, each with an
/// optional CIDR suffix. The pattern is Joi's own, generated by the legacy code; JavaScript's <c>$</c> is <c>\z</c>.
/// </summary>
internal static partial class JoiIp
{
    private const string Ipv4Part = "(?:0?0?[0-9]|0?[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])";
    private const string Ipv4 = "(?:" + Ipv4Part + "\\.){3}" + Ipv4Part;
    private const string H16 = "[0-9A-Fa-f]{1,4}";
    private const string Ls32 = "(?:" + H16 + ":" + H16 + "|" + Ipv4 + ")";
    private const string Ipv6 =
        "(?:(?:" + H16 + ":){6}" + Ls32
        + "|::(?:" + H16 + ":){5}" + Ls32
        + "|(?:" + H16 + ")?::(?:" + H16 + ":){4}" + Ls32
        + "|(?:(?:" + H16 + ":){0,1}" + H16 + ")?::(?:" + H16 + ":){3}" + Ls32
        + "|(?:(?:" + H16 + ":){0,2}" + H16 + ")?::(?:" + H16 + ":){2}" + Ls32
        + "|(?:(?:" + H16 + ":){0,3}" + H16 + ")?::" + H16 + ":" + Ls32
        + "|(?:(?:" + H16 + ":){0,4}" + H16 + ")?::" + Ls32
        + "|(?:(?:" + H16 + ":){0,5}" + H16 + ")?::" + H16
        + "|(?:(?:" + H16 + ":){0,6}" + H16 + ")?::)";
    private const string Ipv6Cidr = "(?:0?0?[0-9]|0?[1-9][0-9]|1[01][0-9]|12[0-8])";
    private const string IpvFuture = "v[0-9A-Fa-f]+\\.[a-zA-Z0-9-\\._~!\\$&'\\(\\)\\*\\+,;=:]+";

    /// <summary>The pattern of <c>Ip.createIpRegex(['ipv4', 'ipv6', 'ipvfuture'], 'optional')</c> (JavaScript source).</summary>
    public const string JavaScriptSource =
        "^(?:" + Ipv4 + "(?:\\/(?:[0-9]|[1-2][0-9]|3[0-2]))?"
        + "|" + Ipv6 + "(?:\\/" + Ipv6Cidr + ")?"
        + "|" + IpvFuture + "(?:\\/" + Ipv6Cidr + ")?)$";

    public static bool IsValid(string value) => Pattern().IsMatch(value);

    [GeneratedRegex(
        "^(?:" + Ipv4 + "(?:\\/(?:[0-9]|[1-2][0-9]|3[0-2]))?"
        + "|" + Ipv6 + "(?:\\/" + Ipv6Cidr + ")?"
        + "|" + IpvFuture + "(?:\\/" + Ipv6Cidr + ")?)\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>JavaScript white space (<c>\s</c>, <c>String.prototype.trim</c>): WhiteSpace + LineTerminator.</summary>
internal static class JsWhiteSpace
{
    /// <summary>The members, as the body of a regex character class.</summary>
    public const string Class = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

    private static readonly char[] Chars =
    [
        .. "\t\n\v\f\r ",
        (char)0x00A0, (char)0x1680, (char)0x2000, (char)0x2001, (char)0x2002, (char)0x2003, (char)0x2004, (char)0x2005,
        (char)0x2006, (char)0x2007, (char)0x2008, (char)0x2009, (char)0x200A, (char)0x2028, (char)0x2029, (char)0x202F,
        (char)0x205F, (char)0x3000, (char)0xFEFF,
    ];

    public static string Trim(string text) => text.Trim(Chars);
}

/// <summary>A JSON object as <c>JSON.parse</c> builds it and <c>Object.keys</c> lists it.</summary>
internal static class JsObject
{
    /// <summary>
    /// The object's own keys: a duplicate key keeps its first position and its last value; integer-like keys
    /// (array indexes) come first in ascending order; <c>__proto__</c> is left out (Joi copies the keys by assignment,
    /// which sets the prototype instead of a key).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> Properties(JsonElement element)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "__proto__")
            {
                continue;
            }

            if (!values.ContainsKey(property.Name))
            {
                order.Add(property.Name);
            }

            values[property.Name] = property.Value;
        }

        var ordered = order.Where(IsArrayIndex).OrderBy(key => uint.Parse(key, CultureInfo.InvariantCulture))
            .Concat(order.Where(key => !IsArrayIndex(key)));
        var result = new OrderedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in ordered)
        {
            result.Add(key, values[key]);
        }

        return result;
    }

    /// <summary>A canonical uint32 below 2^32 − 1 (<c>"0"</c>, <c>"17"</c>; not <c>"017"</c>).</summary>
    private static bool IsArrayIndex(string key) =>
        key.Length > 0 && (key == "0" || key[0] != '0') && key.All(char.IsAsciiDigit)
        && uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < uint.MaxValue;
}
