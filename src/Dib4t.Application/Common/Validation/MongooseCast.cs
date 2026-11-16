using System.Text.Json;
using Dib4t.SharedKernel;

namespace Dib4t.Application.Common.Validation;

/// <summary>
/// What the legacy Mongoose 5.13 models do with a value that passed Joi (checked against the legacy code:
/// <c>tests/fixtures/legacy/settings/settings-golden.json</c>). The routes hand the <b>original</b> request value to
/// the model, not Joi's converted one, so these casts decide what is stored.
/// </summary>
internal static class MongooseCast
{
    /// <summary>
    /// A <c>[String]</c> path (<c>users.roles</c>): a non-array value becomes a one-element array; each element is
    /// kept if it is a string or <c>null</c>, a number becomes its JavaScript text (<c>1e21</c> → <c>"1e+21"</c>), a
    /// boolean <c>"true"</c>/<c>"false"</c>; an object or array element is a cast error, i.e. the save fails (500).
    /// </summary>
    public static (List<string?>? Values, string? Failure) StringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            var single = String(value);
            return single.Failure is null ? ([single.Value], null) : (null, single.Failure);
        }

        var values = new List<string?>();
        foreach (var element in value.EnumerateArray())
        {
            var (text, failure) = String(element);
            if (failure is not null)
            {
                return (null, failure);
            }

            values.Add(text);
        }

        return (values, null);
    }

    /// <summary>
    /// Mongoose 5.13 <c>castString</c> for JSON values: a string as is, <c>null</c> kept, a number its JavaScript
    /// text, a boolean <c>"true"</c>/<c>"false"</c>; an object whose <c>_id</c> is a non-empty string is that string
    /// ("documents being passed": <c>{ "_id": "abc" }</c> → <c>"abc"</c>, checked in <c>entity-golden.json</c>);
    /// any other object or array is a cast error.
    /// </summary>
    public static (string? Value, string? Failure) String(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => (value.GetString(), null),
        JsonValueKind.Null => (null, null),
        JsonValueKind.Number => (JsNumber.ToString(value.GetDouble()), null),
        JsonValueKind.True => ("true", null),
        JsonValueKind.False => ("false", null),
        JsonValueKind.Object when JsObject.Properties(value).TryGetValue("_id", out var id)
                                  && id.ValueKind == JsonValueKind.String && id.GetString()!.Length > 0 => (id.GetString(), null),
        _ => (null, $"Cast to string failed for value {value.GetRawText()}"),
    };

    /// <summary>JavaScript truthiness of a JSON value (<c>if (req.body.password)</c>).</summary>
    public static bool IsTruthy(JsonElement? value) => value is { } element && element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => element.GetString()!.Length > 0,
        JsonValueKind.Number => element.GetDouble() is not 0 and not double.NaN,
        _ => true,
    };
}

/// <summary>
/// The <c>_id</c> cast of Mongoose <c>findById(req.params.id)</c> (js-bson 1.x <c>ObjectId</c>): 24 hex digits in any
/// case, or a 12-character string taken as the 12 bytes (each character's code &amp; 0xFF, Node's <c>binary</c>
/// encoding). Anything else throws a CastError, which the legacy app answers with 500.
/// </summary>
public static class LegacyObjectId
{
    /// <summary>The id as 24 lowercase hex digits, or <c>null</c> when the legacy cast fails (→ 500).</summary>
    public static string? Cast(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 24 && id.All(char.IsAsciiHexDigit))
        {
            return id.ToLowerInvariant();
        }

        if (id.Length == 12)
        {
            return string.Concat(id.Select(c => ((byte)(c & 0xFF)).ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return null;
    }
}
