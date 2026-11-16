using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Application.Common.Validation;
using Dib4t.SharedKernel;

namespace Dib4t.Application.Entities;

/// <summary>
/// <c>POST /api/entity/search</c> (legacy <c>entityRepository.search</c>, not used by the legacy UI): every entity
/// matching <b>all five</b> case-insensitive patterns <c>^{value}</c> — <c>name</c> on <c>name</c>, <c>nif</c> on
/// <c>fiscalNumber</c>, <c>localidade</c> on <c>location</c>, <c>morada</c> on <c>address</c>, <c>codigoPostal</c> on
/// <c>zipCode</c>. The body is not validated: each value is JavaScript's <c>String(value)</c> (a missing key is
/// <c>"undefined"</c>, so it only matches texts starting with "undefined"), and the text is a <b>regular
/// expression</b>, evaluated by MongoDB. Checked against the legacy code (<c>entity-golden.json</c>).
/// </summary>
/// <param name="Patterns">(stored field, pattern), in the legacy order; options <c>i</c>.</param>
public sealed record EntitySearch(IReadOnlyList<(string Field, string Pattern)> Patterns)
{
    private static readonly (string Field, string Key)[] Keys =
    [
        ("name", "name"),
        ("fiscalNumber", "nif"),
        ("location", "localidade"),
        ("address", "morada"),
        ("zipCode", "codigoPostal"),
    ];

    /// <summary>
    /// The patterns of <paramref name="body"/>, or the failure that makes the legacy route answer 500 before the query:
    /// a value JavaScript cannot convert to a string (an object with its own <c>toString</c> key) or a pattern with a
    /// NUL character (refused by the legacy driver). A pattern MongoDB refuses fails the query (500 too).
    /// </summary>
    public static Result<EntitySearch> From(JsonElement body)
    {
        var properties = body.ValueKind == JsonValueKind.Object
            ? JsObject.Properties(body)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var patterns = new List<(string, string)>(Keys.Length);
        foreach (var (field, key) in Keys)
        {
            var text = properties.TryGetValue(key, out var value) ? JsString(value) : "undefined";
            if (text is null)
            {
                return Refused($"{key}: Cannot convert object to primitive value");
            }

            var pattern = "^" + text;
            if (pattern.Contains('\0', StringComparison.Ordinal))
            {
                return Refused($"{key}: a regular expression must not contain null bytes");
            }

            patterns.Add((field, pattern));
        }

        return new EntitySearch(patterns);
    }

    /// <summary>
    /// JavaScript <c>String(value)</c> of a JSON value; <c>null</c> when it throws (an object with an own
    /// <c>toString</c> key: not callable, and <c>valueOf</c> gives no primitive). Arrays join their elements with
    /// <c>,</c> (<c>null</c> elements are empty); other objects are <c>[object Object]</c>.
    /// </summary>
    internal static string? JsString(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Null:
                return "null";
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return JsNumber.ToString(value.GetDouble());
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Null)
                    {
                        parts.Add(string.Empty);
                        continue;
                    }

                    if (JsString(element) is not { } part)
                    {
                        return null;
                    }

                    parts.Add(part);
                }

                return string.Join(',', parts);
            default:
                return JsObject.Properties(value).ContainsKey("toString") ? null : "[object Object]";
        }
    }

    private static Error Refused(string reason) => new(ErrorKind.Failure, LegacyMessages.SomethingFailed) { Reason = reason };
}
