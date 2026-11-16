using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dib4t.Server.Http;

/// <summary>
/// <c>req.query</c> as legacy Express 4 builds it (query parser "extended" = qs): one key per name in order of first
/// appearance; a name given more than once is an array, a <c>name[…]</c> key an array or object. Only the shape the
/// Joi schemas look at is kept: strings stay strings, everything else becomes a non-string value under the base name.
/// </summary>
public static class LegacyQuery
{
    public static JsonElement ToJson(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new JsonObject();
        foreach (var (key, values) in query)
        {
            var bracket = key.IndexOf('[', StringComparison.Ordinal);
            var name = bracket > 0 ? key[..bracket] : key;
            if (result.ContainsKey(name) || bracket > 0 || values.Count > 1)
            {
                // qs merges repeated names and bracket keys into an array (or object): not a string for Joi.
                result[name] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
                continue;
            }

            result[name] = values.ToString();
        }

        return JsonSerializer.SerializeToElement(result);
    }
}
