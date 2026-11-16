using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dib4t.Server.Http;

/// <summary>
/// Dates in API answers as Express writes them (<c>Date.prototype.toJSON</c> = <c>toISOString</c>): UTC, always three
/// fraction digits, <c>Z</c> (<c>2026-02-01T09:00:00.000Z</c>; .NET would leave out a zero fraction).
/// </summary>
public sealed class JsDateJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}
