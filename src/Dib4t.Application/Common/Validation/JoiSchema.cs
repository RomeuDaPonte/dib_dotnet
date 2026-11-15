using System.Globalization;
using System.Text.Json;

namespace Dib4t.Application.Common.Validation;

/// <summary>
/// The subset of Joi 14 the legacy validations use (<c>back-end/validations/*.js</c>): <c>Joi.validate(body, {…})</c>
/// with the legacy defaults, i.e. <c>abortEarly</c> (the first error only) and no unknown keys. The message is the
/// legacy 400 text, e.g. <c>"email" is required</c>. Rules are added here as the feature slices need them; each one
/// is checked against the legacy code's own answers (golden files in <c>tests/fixtures/legacy</c>).
/// </summary>
/// <remarks>
/// JavaScript semantics kept: the body is read like <c>JSON.parse</c> builds the object (see <see cref="JsObject"/>),
/// the schema keys are checked in schema order, then the first unknown key is reported; lengths are UTF-16 code
/// units (<c>string.length</c>).
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
            if (rule.Validate(key, value) is { } error)
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
    public abstract string? Validate(string label, JsonElement? value);
}

/// <summary>
/// <c>Joi.string()</c>: in Joi's order, presence (<c>.required()</c>), type (<c>null</c> and non-strings → "must be a
/// string"), the empty string (never allowed), then the rules in the order they were added.
/// </summary>
internal sealed class JoiString : JoiRule
{
    private readonly List<Func<string, string, string?>> _rules = [];
    private bool _required;

    public static JoiString Create() => new();

    public JoiString Required()
    {
        _required = true;
        return this;
    }

    public JoiString Min(int limit) => Add((label, value) =>
        value.Length < limit ? $"\"{label}\" length must be at least {limit.ToString(CultureInfo.InvariantCulture)} characters long" : null);

    public JoiString Max(int limit) => Add((label, value) =>
        value.Length > limit ? $"\"{label}\" length must be less than or equal to {limit.ToString(CultureInfo.InvariantCulture)} characters long" : null);

    /// <summary><c>.email()</c> with Joi's defaults: isemail 3.2.0 (<see cref="LegacyEmail"/>).</summary>
    public JoiString Email() => Add((label, value) => LegacyEmail.IsValid(value) ? null : $"\"{label}\" must be a valid email");

    public override string? Validate(string label, JsonElement? value)
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
        if (text.Length == 0)
        {
            return $"\"{label}\" is not allowed to be empty";
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
